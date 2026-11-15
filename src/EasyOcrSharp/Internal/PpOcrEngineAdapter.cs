using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using EasyOcrSharp.Structure.Engine;

using PaddleDetectionOptions = EasyOcrSharp.Structure.Engine.Models.DetectionOptions;
using PaddleGrouping = EasyOcrSharp.Structure.Engine.Models.TextGrouping;
using PaddleProvider = EasyOcrSharp.Structure.Engine.Services.OcrExecutionProvider;
using PaddleRecognitionOptions = EasyOcrSharp.Structure.Engine.Models.RecognitionOptions;
using PaddleRegion = EasyOcrSharp.Structure.Engine.Models.DetectedRegion;

namespace EasyOcrSharp.Internal;

/// <summary>
/// How one call's language request maps onto PP-OCRv5 recognizer packs: the codes handed to the
/// structure engine's text pipeline and whether it must read every line with several packs.
/// Built by <see cref="PpOcrEngineAdapter.CreatePlan"/>; immutable and cheap, so a streamed page
/// rebuilds it per chunk.
/// </summary>
internal sealed record PpOcrLanguagePlan
{
    /// <summary>
    /// The caller's language codes, trimmed, lower-cased and de-duplicated — what the result reports when
    /// no language detection ran. May be empty only when <see cref="AutoDetect"/> is set.
    /// </summary>
    public required string[] Requested { get; init; }

    /// <summary>
    /// The codes the engine is driven with: one representative per distinct PP-OCRv5 pack. A single entry
    /// for an ordinary request; one entry per candidate pack when <see cref="MultiPack"/> is set.
    /// </summary>
    public required string[] EngineLanguages { get; init; }

    /// <summary>
    /// True when every line is recognized with each pack in <see cref="EngineLanguages"/> and the most
    /// confident reading wins — a request spanning several packs, or language auto-detection. Drives the
    /// engine's per-crop best-of sweep (its auto-detection mode) with <see cref="EngineLanguages"/> as
    /// the candidate list.
    /// </summary>
    public bool MultiPack { get; init; }

    /// <summary>
    /// True when the caller asked for <see cref="RecognitionOptions.AutoDetectLanguage"/>, so the result
    /// reports the detected languages instead of <see cref="Requested"/>.
    /// </summary>
    public bool AutoDetect { get; init; }

    /// <summary>
    /// Every normalized auto-detect candidate (before collapsing to one code per pack), so a detected pack
    /// can be reported as the code the caller named — <c>ja</c> rather than the <c>en</c> that represents
    /// the shared default pack. Empty unless <see cref="AutoDetect"/> is set.
    /// </summary>
    public string[] Candidates { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Thin adapter between EasyOcrSharp's public plain-OCR contract and the PP-OCRv5 text pipeline of the
/// in-repo structure engine (<see cref="StructureTextEngine"/>): maps EasyOCR language codes to PP-OCRv5
/// packs, the public <see cref="RecognitionOptions"/> onto the engine's own options, and the engine's
/// regions and execution provider back onto the public types. Pure functions only — the routing, the
/// operation scope and the result assembly live in <c>EasyOcrService.PaddleEngine.cs</c>.
/// </summary>
/// <remarks>
/// Every per-script PP-OCRv5 dictionary also carries the 52 ASCII letters and the digits, so <c>en</c>
/// requested next to another script needs no recognizer of its own — the other script's pack reads the
/// English too — while a request spanning two genuinely different scripts (<c>ch_sim</c> + <c>ko</c>)
/// reads each line with both packs and keeps the more confident reading.
/// </remarks>
internal static class PpOcrEngineAdapter
{
    /// <summary>
    /// PP-OCRv5's default recognition batch (<c>rec_batch_num</c>), used when the caller leaves
    /// <see cref="RecognitionOptions.BatchSize"/> at its EasyOCR default of 1.
    /// </summary>
    internal const int DefaultBatchSize = 6;

    /// <summary>
    /// Candidates considered by <see cref="RecognitionOptions.AutoDetectLanguage"/> when the caller supplies
    /// none — the same Latin / Cyrillic / Chinese / Japanese / Korean set the default engine uses, which on
    /// PP-OCRv5 resolves to three packs (the default ch/en/ja pack, East-Slavic and Korean).
    /// </summary>
    internal static readonly string[] DefaultAutoDetectCandidates = { "en", "ru", "ch_sim", "ja", "ko" };

    /// <summary>
    /// Maps the representative codes the engine reports from language detection (PP-OCRv5 pack and script
    /// names such as <c>latin</c>, <c>japan</c>, <c>eslav</c>) to the EasyOCR-style codes the rest of the
    /// public API speaks.
    /// </summary>
    private static readonly Dictionary<string, string> DetectedCodeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["latin"] = "en",
        ["ch"] = "ch_sim",
        ["chinese_cht"] = "ch_tra",
        ["japan"] = "ja",
        ["korean"] = "ko",
        ["cyrillic"] = "ru",
        ["eslav"] = "ru",
        ["arabic"] = "ar",
        ["devanagari"] = "hi",
        ["thai"] = "th",
        ["greek"] = "el",
        ["telugu"] = "te",
        ["tamil"] = "ta",
    };

    /// <summary>
    /// Codes PaddleOCR routes to a pack whose dictionary cannot spell the language, so they are refused rather
    /// than read with letters missing. Vietnamese goes to the Latin pack, whose dictionary has 22 of the 67
    /// lower-case Vietnamese letters and none of the tone-marked vowels (<c>ả ạ ằ ệ ự …</c>; the default pack
    /// lacks 48): a sign reading <c>KHÔNG PHẬN SỰ</c> came back as <c>KHÔNG PHÂN SU</c>. The default engine
    /// reads it.
    /// </summary>
    private static readonly HashSet<string> UnrepresentableLanguages = new(StringComparer.OrdinalIgnoreCase) { "vi" };

    /// <summary>
    /// Every language code the PP-OCRv5 engine accepts, sorted ordinally — the list an unsupported-language
    /// error names.
    /// </summary>
    internal static IReadOnlyList<string> SupportedLanguages { get; } = StructureModelRegistry.All
        .SelectMany(pack => pack.Languages)
        .Where(code => !UnrepresentableLanguages.Contains(code))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// True when <paramref name="language"/> resolves to a PP-OCRv5 recognizer pack whose dictionary can spell
    /// it.
    /// </summary>
    internal static bool IsSupported(string language)
        => !string.IsNullOrWhiteSpace(language)
           && !UnrepresentableLanguages.Contains(language.Trim())
           && StructureModelRegistry.FindByLanguage(language.Trim()) is not null;

    /// <summary>
    /// Rejects an <see cref="RecognitionOptions.Engine"/> value this build does not know — the routing sends
    /// every non-default value here, so an out-of-range cast fails loudly instead of silently running
    /// some engine.
    /// </summary>
    internal static void EnsureKnownEngine(RecognitionEngine engine)
    {
        if (engine is not (RecognitionEngine.EasyOcr or RecognitionEngine.PpOcrV5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(RecognitionOptions.Engine), engine,
                $"Unknown recognition engine {(int)engine}. Use {nameof(RecognitionEngine)}.{nameof(RecognitionEngine.EasyOcr)} " +
                $"or {nameof(RecognitionEngine)}.{nameof(RecognitionEngine.PpOcrV5)}.");
        }
    }

    /// <summary>
    /// Resolves the caller's (already normalized) language codes — or, with
    /// <see cref="RecognitionOptions.AutoDetectLanguage"/>, the auto-detect candidates — to PP-OCRv5 packs.
    /// Throws <see cref="ArgumentException"/> naming every unsupported code and the supported set.
    /// </summary>
    /// <param name="requested">Trimmed, lower-cased, de-duplicated codes; empty only for auto-detection.</param>
    /// <param name="options">The call's options (only the auto-detection members are read).</param>
    internal static PpOcrLanguagePlan CreatePlan(IReadOnlyList<string> requested, RecognitionOptions options)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(options);
        var requestedArray = requested.ToArray();

        if (options.AutoDetectLanguage)
        {
            var candidates = Normalize(options.AutoDetectCandidates);
            if (candidates.Length == 0) candidates = DefaultAutoDetectCandidates;
            EnsureSupported(candidates, nameof(RecognitionOptions.AutoDetectCandidates));

            // Candidate order is kept: the default pack leading (as it does for the built-in candidates)
            // lets the engine accept a confident Latin/CJK page without loading the other packs.
            return new PpOcrLanguagePlan
            {
                Requested = requestedArray,
                EngineLanguages = GroupByPack(candidates).Select(g => g.Codes[0]).ToArray(),
                MultiPack = true,
                AutoDetect = true,
                Candidates = candidates,
            };
        }

        if (requestedArray.Length == 0)
            throw new ArgumentException("At least one valid language must be specified.", "languages");
        EnsureSupported(requestedArray, "languages");

        var groups = GroupByPack(requestedArray);
        if (groups.Count > 1)
        {
            // en alone never needs the default pack beside another script: every PP-OCRv5 dictionary
            // carries the ASCII letters, so the other pack reads the English as well.
            groups.RemoveAll(g => g.IsDefaultPack
                && g.Codes.All(code => string.Equals(code, "en", StringComparison.OrdinalIgnoreCase)));
        }

        if (groups.Count == 1)
        {
            return new PpOcrLanguagePlan
            {
                Requested = requestedArray,
                EngineLanguages = new[] { groups[0].Codes[0] },
            };
        }

        // Several scripts: each line is read by every pack and the most confident reading wins. The
        // default pack goes last so the engine's "confident Latin/CJK page" shortcut — which stops after
        // the first pack when it is the default one — can never skip a script the caller asked for.
        return new PpOcrLanguagePlan
        {
            Requested = requestedArray,
            EngineLanguages = groups
                .OrderBy(g => g.IsDefaultPack)
                .Select(g => g.Codes[0])
                .ToArray(),
            MultiPack = true,
        };
    }

    /// <summary>
    /// Maps the public per-call options onto the engine's options for one PP-OCRv5 run. Pre-processing,
    /// document orientation/unwarp, the region of interest and paragraph merging are applied by the caller
    /// around the run (exactly as on the default engine), so the engine is always told to skip them.
    /// </summary>
    /// <param name="options">The caller's options.</param>
    /// <param name="plan">The resolved language plan.</param>
    /// <param name="perRegion">
    /// True for caller-supplied (or streamed) regions: one result per region, never merged across regions —
    /// matching <c>RecognizeRegionsAsync</c> on the default engine.
    /// </param>
    internal static PaddleRecognitionOptions ToEngineOptions(RecognitionOptions options, PpOcrLanguagePlan plan, bool perRegion)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(plan);

        // Allowlist and blocklist are mutually exclusive on the public options (the allowlist wins), whereas
        // the engine honors both at once — so only one is ever forwarded.
        bool hasAllowlist = !string.IsNullOrEmpty(options.Allowlist);
        return new PaddleRecognitionOptions
        {
            Grouping = perRegion || options.Grouping == TextGrouping.Word ? PaddleGrouping.Word : PaddleGrouping.Line,
            MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
            BatchSize = options.BatchSize > 1 ? options.BatchSize : DefaultBatchSize,
            DropScore = options.MinConfidence,
            UseTextLineOrientation = true,
            VerifyOrientationByRecognition = true,
            UseDocOrientation = false,
            UseDocUnwarp = false,
            AutoDetectLanguage = plan.MultiPack,
            AutoDetectCandidates = plan.MultiPack ? plan.EngineLanguages : null,
            Allowlist = hasAllowlist ? PaddleRecognitionOptions.FromCharacters(options.Allowlist) : null,
            Blocklist = !hasAllowlist && !string.IsNullOrEmpty(options.Blocklist)
                ? PaddleRecognitionOptions.FromCharacters(options.Blocklist)
                : null,
            // Word boxes come from the recognizer's CTC timesteps (PaddleOCR's return_word_box); PP-OCRv5 has
            // no per-character boxes, so Characters detail yields the words only.
            ReturnWordBoxes = options.WordLevelDetail != WordLevelDetail.None,
            Detection = ToEngineDetection(options.Detection),
        };
    }

    /// <summary>
    /// The DB detector keeps PaddleOCR's own thresholds — CRAFT's are meaningless to it — but takes the
    /// engine-neutral opt-ins: tiling, the small-text rescue and the contrast-enhanced detector input.
    /// </summary>
    internal static PaddleDetectionOptions ToEngineDetection(DetectionOptions detection)
    {
        ArgumentNullException.ThrowIfNull(detection);
        if (!detection.TileLargeImages && detection.MinTextHeight <= 0 && !detection.EnhanceContrast)
        {
            return PaddleDetectionOptions.Default;
        }

        return new PaddleDetectionOptions
        {
            TileLargeImages = detection.TileLargeImages,
            MinTextHeight = Math.Max(0, detection.MinTextHeight),
            EnhanceContrast = detection.EnhanceContrast,
        };
    }

    /// <summary>
    /// The languages a result reports: the detected ones (mapped back to EasyOCR-style codes) when
    /// auto-detection ran and found any, otherwise the requested ones — with the same <c>en</c> fallback
    /// the default engine uses when auto-detection had nothing to go on.
    /// </summary>
    /// <param name="plan">The call's language plan.</param>
    /// <param name="detected">The codes the engine reported from language detection (may be empty).</param>
    /// <param name="recognizedText">
    /// The page's recognized text, when available. The default PP-OCRv5 pack serves Chinese, Japanese and
    /// English alike, so when it wins the detection vote the dominant script of what it read decides which
    /// of those to report.
    /// </param>
    internal static string[] ReportedLanguages(PpOcrLanguagePlan plan, IReadOnlyList<string> detected, string? recognizedText = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.AutoDetect && detected is { Count: > 0 })
        {
            var candidates = plan.Candidates.Length > 0 ? plan.Candidates : plan.EngineLanguages;
            var mapped = detected
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => MapDetectedCode(RefineDefaultPackCode(code, recognizedText), candidates))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (mapped.Length > 0) return mapped;
        }

        if (plan.Requested.Length > 0) return plan.Requested;
        return plan.AutoDetect ? new[] { "en" } : plan.EngineLanguages;
    }

    /// <summary>
    /// Replaces the default pack's representative code (<c>ch</c>) with the script the recognized text is
    /// actually written in (<c>latin</c> / <c>ch</c> / <c>japan</c>); every other code passes through.
    /// </summary>
    private static string RefineDefaultPackCode(string detected, string? recognizedText)
    {
        if (string.IsNullOrWhiteSpace(recognizedText)) return detected;
        if (StructureModelRegistry.FindByLanguage(detected) is not { } pack
            || PackKey(pack) != PackKey(StructureModelRegistry.MobileRecognizer))
        {
            return detected;
        }

        return ScriptDetection.DominantScript(recognizedText) switch
        {
            DetectedScript.Latin => "latin",
            DetectedScript.Han => "ch",
            DetectedScript.Kana => "japan",
            _ => detected,
        };
    }

    /// <summary>
    /// Maps one detected code to the code to report: its EasyOCR alias when the caller considered that
    /// language, else the first candidate the same pack serves, else the alias (or the code unchanged).
    /// </summary>
    private static string MapDetectedCode(string detected, IReadOnlyList<string> candidates)
    {
        if (DetectedCodeAliases.TryGetValue(detected, out var alias)
            && candidates.Contains(alias, StringComparer.OrdinalIgnoreCase))
        {
            return alias;
        }

        if (StructureModelRegistry.FindByLanguage(detected) is { } pack)
        {
            var key = PackKey(pack);
            foreach (var candidate in candidates)
            {
                if (StructureModelRegistry.FindByLanguage(candidate) is { } other && PackKey(other) == key)
                    return candidate;
            }
        }

        return alias ?? detected.ToLowerInvariant();
    }

    /// <summary>Maps the engine's regions onto the public <see cref="DetectedRegion"/> type.</summary>
    internal static IReadOnlyList<DetectedRegion> ToPublicRegions(IReadOnlyList<PaddleRegion> regions)
    {
        if (regions.Count == 0) return Array.Empty<DetectedRegion>();
        var mapped = new DetectedRegion[regions.Count];
        for (int i = 0; i < regions.Count; i++)
        {
            mapped[i] = new DetectedRegion
            {
                BoundingPolygon = regions[i].BoundingPolygon,
                BoundingBox = regions[i].BoundingBox,
            };
        }
        return mapped;
    }

    /// <summary>Maps the engine's execution provider onto the public enum.</summary>
    internal static OcrExecutionProvider ToPublicProvider(PaddleProvider provider) => provider switch
    {
        PaddleProvider.Cuda => OcrExecutionProvider.Cuda,
        PaddleProvider.DirectMl => OcrExecutionProvider.DirectMl,
        PaddleProvider.CoreMl => OcrExecutionProvider.CoreMl,
        PaddleProvider.Auto => OcrExecutionProvider.Auto,
        _ => OcrExecutionProvider.Cpu,
    };

    // ---- helpers ----

    /// <summary>One distinct recognizer (network + dictionary) and the requested codes it serves.</summary>
    private sealed record PackGroup(string Key, bool IsDefaultPack, List<string> Codes);

    /// <summary>
    /// Groups codes by the recognizer they resolve to, preserving first-appearance order. Packs are keyed
    /// by their model + dictionary files rather than their names, so <c>ch_sim</c> and <c>ch_tra</c> —
    /// registered as two packs over the very same network — collapse into one and are never run twice.
    /// </summary>
    private static List<PackGroup> GroupByPack(IReadOnlyList<string> codes)
    {
        var groups = new List<PackGroup>();
        foreach (var code in codes)
        {
            var pack = StructureModelRegistry.FindByLanguage(code)!;
            var key = PackKey(pack);
            var group = groups.Find(g => g.Key == key);
            if (group is null)
            {
                group = new PackGroup(key, key == PackKey(StructureModelRegistry.MobileRecognizer), new List<string>());
                groups.Add(group);
            }
            group.Codes.Add(code);
        }
        return groups;
    }

    private static string PackKey(RecognizerPack pack) => pack.Model.FileName + "|" + pack.Dictionary.FileName;

    private static string[] Normalize(IEnumerable<string>? codes)
        => codes is null
            ? Array.Empty<string>()
            : codes.Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    /// <summary>
    /// Throws when any code has no PP-OCRv5 pack, naming each offender, pointing those the default engine
    /// can read at it, and listing the supported codes.
    /// </summary>
    private static void EnsureSupported(IReadOnlyList<string> codes, string paramName)
    {
        var unsupported = codes.Where(code => !IsSupported(code)).ToArray();
        if (unsupported.Length == 0) return;

        var easyOcrOnly = unsupported.Where(code => ModelRegistry.FindByLanguage(code) is not null).ToArray();
        var message =
            $"The PP-OCRv5 engine ({nameof(RecognitionOptions)}.{nameof(RecognitionOptions.Engine)} = " +
            $"{nameof(RecognitionEngine)}.{nameof(RecognitionEngine.PpOcrV5)}) has no recognizer for language code(s) " +
            $"{string.Join(", ", unsupported.Select(c => $"'{c}'"))}. " +
            (easyOcrOnly.Length > 0
                ? $"{string.Join(", ", easyOcrOnly.Select(c => $"'{c}'"))} " +
                  $"{(easyOcrOnly.Length == 1 ? "is" : "are")} supported by the default " +
                  $"{nameof(RecognitionEngine)}.{nameof(RecognitionEngine.EasyOcr)} engine. "
                : string.Empty) +
            $"Supported codes: {string.Join(", ", SupportedLanguages)}.";
        throw new ArgumentException(message, paramName);
    }
}
