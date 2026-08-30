using System.Collections.Concurrent;
using EasyOcrSharp.Structure.Engine.Classification;
using EasyOcrSharp.Structure.Engine.Detection;
using EasyOcrSharp.Structure.Engine.Geometry;
using EasyOcrSharp.Structure.Engine.Recognition;
using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure.Engine.Services;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// Top-level coordinator: loads the DB detector once, lazy-loads the (optional) text-line orientation
/// classifier and the recognizer pack(s) per language, and runs the det → cls → rec → reading-order
/// pipeline producing <see cref="OcrLine"/>s. Thread-safe; all underlying ONNX sessions are reused
/// across calls. Mirrors the lazy-session-cache / WarmUp / DisposeAsync patterns of the reference engine.
/// <para>
/// The pipeline is PaddleOCR's <c>PaddleOCR.ocr()</c>: detect text quads → rectify each quad into an
/// upright crop (<see cref="PerspectiveWarp"/> / <c>get_rotate_crop_image</c>) → optionally flip 180°
/// boxes via the orientation classifier → recognize crops (batched) → drop low-confidence readings →
/// sort into reading order (<see cref="SortBoxes"/> / <c>sorted_boxes</c>) → assemble
/// <see cref="OcrLine"/>s. All ONNX sessions are loaded on demand via the wired loader plumbing
/// (<see cref="GetOrLoadDetectorAsync"/>, <see cref="GetOrLoadClassifierAsync"/>,
/// <see cref="GetOrLoadRecognizerAsync"/>).
/// </para>
/// </summary>
internal sealed class StructureTextEngine : IAsyncDisposable
{
    private readonly StructureEngineOptions _options;
    private readonly ILogger? _logger;

    // Session options for the resolved provider, split by workload: the detector runs one big graph per
    // image (wants intra-op parallelism); the classifier/recognizer run from a data-parallel loop over
    // boxes (on CPU each gets intra-op=1 so the loop supplies the parallelism). If an accelerated session
    // fails to initialize at model-load time we build the matching CPU sets once and route through them.
    private readonly SessionOptions _detectorSessionOptions;
    private readonly SessionOptions _recognizerSessionOptions;
    private SessionOptions? _detectorCpuFallback;
    private SessionOptions? _recognizerCpuFallback;
    private volatile OcrExecutionProvider _activeProvider;
    private readonly object _fallbackLock = new();

    private ITextDetector? _detector;
    private readonly SemaphoreSlim _detectorLock = new(1, 1);

    private IAngleClassifier? _classifier;
    private readonly SemaphoreSlim _classifierLock = new(1, 1);

    private readonly ConcurrentDictionary<string, Lazy<Task<ITextRecognizer>>> _recognizers =
        new(StringComparer.OrdinalIgnoreCase);

    public StructureTextEngine(StructureEngineOptions options, ILogger? logger)
    {
        _options = options;
        _logger = logger;
        ResolvedProvider = ExecutionProviderResolver.Resolve(options.ExecutionProvider, logger);
        _activeProvider = ResolvedProvider;
        _detectorSessionOptions = ExecutionProviderResolver.BuildSessionOptions(ResolvedProvider, options, logger, perBoxParallel: false);
        _recognizerSessionOptions = ExecutionProviderResolver.BuildSessionOptions(ResolvedProvider, options, logger, perBoxParallel: true);
        GpuHint = BuildGpuHint(options.ExecutionProvider, ResolvedProvider, options.LogGpuHint, logger);
    }

    /// <summary>
    /// The accelerator EasyOcrSharp.Structure.Engine resolved to attempt at startup — <see cref="OcrExecutionProvider.Auto"/>
    /// is resolved to a concrete provider here. A non-CPU value may still degrade to CPU at the first
    /// model load if the device turns out to be unusable; this property reflects the initial attempt.
    /// </summary>
    public OcrExecutionProvider ResolvedProvider { get; }

    /// <summary>
    /// A one-time, actionable message naming the exact provider package to install, set only when
    /// <see cref="OcrExecutionProvider.Auto"/> fell back to CPU yet a usable GPU is physically present.
    /// Null whenever a GPU is already in use, CPU was chosen explicitly, or no GPU was detected.
    /// </summary>
    public string? GpuHint { get; }

    /// <summary>
    /// Runs the full pipeline (detect → optional classify → recognize) and returns the recognized lines
    /// in reading order. This is PaddleOCR's <c>ocr(det=True, rec=True)</c>: the DB detector locates text
    /// quadrilaterals, each quad is rectified into an upright crop, recognized, filtered by
    /// <see cref="RecognitionOptions.DropScore"/>, and sorted top-to-bottom / left-to-right.
    /// </summary>
    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(
        Image<Rgb24> image,
        IReadOnlyList<string> languages,
        RecognitionOptions options,
        CancellationToken cancellationToken)
        => (await RecognizeWithDetectedLanguagesAsync(image, languages, options, cancellationToken).ConfigureAwait(false)).Lines;

    /// <summary>
    /// Same as <see cref="RecognizeAsync"/> but also reports the language code(s) inferred when language
    /// auto-detection is active (see <see cref="RecognitionOptions.AutoDetectLanguage"/> or the <c>"auto"</c>
    /// language code). When auto-detection did not run, <c>DetectedLanguages</c> is empty.
    /// </summary>
    public async Task<(IReadOnlyList<OcrLine> Lines, IReadOnlyList<string> DetectedLanguages)> RecognizeWithDetectedLanguagesAsync(
        Image<Rgb24> image,
        IReadOnlyList<string> languages,
        RecognitionOptions options,
        CancellationToken cancellationToken)
    {
        var detector = await GetOrLoadDetectorAsync(cancellationToken).ConfigureAwait(false);
        var quads = detector.Detect(image, options.Detection);
        _logger?.LogInformation("DB detector located {Count} text regions", quads.Count);
        if (quads.Count == 0) return (Array.Empty<OcrLine>(), Array.Empty<string>());

        // De-duplicate overlapping detections (the detector can emit near-identical boxes) so the same
        // text isn't cropped and recognized twice.
        var polygons = BoxNms.Reduce(quads.Select(q => q.ToOcrPoints()).ToArray(), options.Detection.NmsIouThreshold);
        var detected = new List<string>();
        var lines = await RecognizePolygonsAsync(image, languages, polygons, options, detected, cancellationToken).ConfigureAwait(false);
        return (lines, detected);
    }

    /// <summary>
    /// Recognizes a caller-supplied set of region polygons (skipping detection) — PaddleOCR's
    /// <c>ocr(det=False)</c>. Applies the optional text-line orientation classifier and the requested
    /// grouping. Boxes are returned in reading order, in the image's pixel coordinates.
    /// </summary>
    public Task<IReadOnlyList<OcrLine>> RecognizeRegionsAsync(
        Image<Rgb24> image,
        IReadOnlyList<string> languages,
        IReadOnlyList<OcrPoint[]> polygons,
        RecognitionOptions options,
        CancellationToken cancellationToken)
        => RecognizePolygonsAsync(image, languages, polygons, options, detectedLanguagesSink: null, cancellationToken);

    /// <summary>
    /// Runs only the detector (no recognition) and returns the located regions in reading order.
    /// </summary>
    public async Task<IReadOnlyList<DetectedRegion>> DetectRegionsAsync(
        Image<Rgb24> image,
        DetectionOptions detection,
        TextGrouping grouping,
        CancellationToken cancellationToken)
    {
        var detector = await GetOrLoadDetectorAsync(cancellationToken).ConfigureAwait(false);
        var quads = detector.Detect(image, detection);
        if (quads.Count == 0) return Array.Empty<DetectedRegion>();

        var polygons = BoxNms.Reduce(quads.Select(q => q.ToOcrPoints()).ToArray(), detection.NmsIouThreshold);
        var ordered = SortBoxes(polygons);

        var regions = new List<DetectedRegion>(ordered.Count);
        foreach (var poly in ordered)
        {
            regions.Add(new DetectedRegion
            {
                BoundingPolygon = poly,
                BoundingBox = OcrBoundingBox.FromPoints(poly),
            });
        }
        return regions;
    }

    /// <summary>
    /// Shared det-less core: rectify every polygon into an upright crop, optionally flip 180° crops,
    /// recognize the crops in batches, drop low-confidence readings, sort the survivors into reading order
    /// and (when requested) merge them into paragraphs.
    /// <para>
    /// When language auto-detection is active (see <see cref="IsAutoDetect"/>) the crops are recognized with
    /// every candidate pack and the best per-crop reading is kept; the winning language code(s) are appended
    /// to <paramref name="detectedLanguagesSink"/> when one is supplied.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<OcrLine>> RecognizePolygonsAsync(
        Image<Rgb24> image,
        IReadOnlyList<string> languages,
        IReadOnlyList<OcrPoint[]> polygons,
        RecognitionOptions options,
        List<string>? detectedLanguagesSink,
        CancellationToken cancellationToken)
    {
        if (polygons.Count == 0) return Array.Empty<OcrLine>();

        bool auto = IsAutoDetect(languages, options);

        // For the normal (non-auto) path, resolve the single requested pack up front so a wholly
        // unsupported language set short-circuits before any cropping work.
        RecognizerPack? fixedPack = null;
        if (!auto)
        {
            fixedPack = ResolvePacks(languages).FirstOrDefault();
            if (fixedPack is null) return Array.Empty<OcrLine>();
        }

        // Honor the per-call orientation flag even when the engine default left the classifier off; the
        // classifier session is loaded on demand and reused thereafter.
        IAngleClassifier? classifier = null;
        if (options.UseTextLineOrientation || _options.UseTextLineOrientation)
        {
            classifier = await GetOrLoadClassifierAsync(cancellationToken).ConfigureAwait(false);
        }

        // Rectify each quad into an upright crop. A null crop means the polygon was degenerate
        // (sub-2px after rectification); skip it but keep the polygon→crop index mapping intact so
        // recognition results line back up with their source polygons.
        var crops = new List<Image<Rgb24>>(polygons.Count);
        var cropPolygons = new List<OcrPoint[]>(polygons.Count);
        try
        {
            foreach (var poly in polygons)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var crop = PerspectiveWarp.Rectify(image, poly);
                if (crop is null) continue;

                // Optional text-line orientation: a crop the classifier marks as upside-down is rotated
                // 180° in place before it reaches the recognizer.
                if (classifier is not null)
                {
                    var (rotated, _) = classifier.Classify(crop);
                    if (rotated) crop.Mutate(ctx => ctx.Rotate(RotateMode.Rotate180));
                }

                crops.Add(crop);
                cropPolygons.Add(poly);
            }

            if (crops.Count == 0) return Array.Empty<OcrLine>();

            cancellationToken.ThrowIfCancellationRequested();

            // Per-crop best reading: either from the single requested pack, or — when auto-detecting — the
            // highest-confidence reading across all candidate packs.
            IReadOnlyList<(string Text, float Confidence)> readings = auto
                ? await RecognizeAutoAsync(crops, options, detectedLanguagesSink, cancellationToken).ConfigureAwait(false)
                : (await GetOrLoadRecognizerAsync(fixedPack!, cancellationToken).ConfigureAwait(false)).Recognize(crops, options);

            // Drop low-confidence / empty readings (PaddleOCR's drop_score), then keep each surviving
            // line paired with its source polygon.
            var lines = new List<OcrLine>(crops.Count);
            for (int i = 0; i < crops.Count && i < readings.Count; i++)
            {
                var (text, confidence) = readings[i];
                if (string.IsNullOrWhiteSpace(text) || confidence < options.DropScore) continue;

                var poly = cropPolygons[i];
                lines.Add(new OcrLine
                {
                    Text = text,
                    Confidence = confidence,
                    BoundingPolygon = poly,
                    BoundingBox = OcrBoundingBox.FromPoints(poly),
                });
            }

            bool rightToLeft = EasyOcrSharp.Internal.ScriptDirection.IsRightToLeft(languages);
            var ordered = SortLines(lines, rightToLeft);
            return options.Grouping == TextGrouping.Paragraph
                ? ParagraphGrouper.Merge(ordered, rightToLeft: rightToLeft)
                : ordered;
        }
        finally
        {
            foreach (var crop in crops) crop.Dispose();
        }
    }

    // ---- language auto-detection ----

    /// <summary>
    /// The mean default-pack confidence at/above which the Latin/CJK fast path accepts without trying other packs.
    /// </summary>
    private const double AutoFastPathConfidence = 0.85;

    /// <summary>
    /// The curated cross-script candidate shortlist used when auto-detection is requested without an explicit
    /// <see cref="RecognitionOptions.AutoDetectCandidates"/>: the default PP-OCRv5 pack ("ch") plus the major
    /// per-script packs. Kept small so a worst-case run downloads ~11 (not all) recognizers on demand.
    /// </summary>
    private static readonly string[] DefaultAutoDetectCandidates =
    {
        "ch", "latin", "cyrillic", "arabic", "devanagari", "korean", "japan", "thai", "greek", "telugu", "tamil",
    };

    /// <summary>
    /// True when the caller asked for language auto-detection — either by setting
    /// <see cref="RecognitionOptions.AutoDetectLanguage"/> or by passing the literal language code
    /// <c>"auto"</c> in <paramref name="languages"/> (case-insensitive).
    /// </summary>
    private static bool IsAutoDetect(IReadOnlyList<string> languages, RecognitionOptions options)
        => options.AutoDetectLanguage
           || languages.Any(l => string.Equals(l, "auto", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Recognizes every crop with each candidate recognizer pack and keeps, per crop, the highest-confidence
    /// reading across packs. The pack that wins the most crops (weighted by confidence) is the detected
    /// language; its representative code (plus any runners-up that won crops) is appended to
    /// <paramref name="detectedLanguagesSink"/>. Each pack is loaded — and auto-downloaded if missing — via
    /// <see cref="GetOrLoadRecognizerAsync"/>.
    /// <para>
    /// Fast path: the default PP-OCRv5 pack is tried first; if its mean confidence over all crops is at least
    /// <see cref="AutoFastPathConfidence"/> and the recognized text is dominantly Latin/CJK, that result is
    /// accepted and the remaining candidates are skipped — so a clean English page never downloads ten extra
    /// models.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<(string Text, float Confidence)>> RecognizeAutoAsync(
        IReadOnlyList<Image<Rgb24>> crops,
        RecognitionOptions options,
        List<string>? detectedLanguagesSink,
        CancellationToken cancellationToken)
    {
        // Resolve candidate codes → distinct packs, preserving order (default pack first so the fast path
        // can short-circuit on it).
        var candidateCodes = options.AutoDetectCandidates is { Count: > 0 }
            ? options.AutoDetectCandidates
            : DefaultAutoDetectCandidates;
        var candidatePacks = ResolveOrderedPacks(candidateCodes);
        if (candidatePacks.Count == 0)
        {
            // No candidate resolved: fall back to the default recognizer so we still return readings.
            var fallback = await GetOrLoadRecognizerAsync(StructureModelRegistry.MobileRecognizer, cancellationToken).ConfigureAwait(false);
            return fallback.Recognize(crops, options);
        }

        int n = crops.Count;
        var best = new (string Text, float Confidence)[n];
        var bestPack = new RecognizerPack?[n];
        // Per-pack tally of crops won, weighted by the winning confidence — the detection vote.
        var packScore = new Dictionary<string, double>(StringComparer.Ordinal);

        for (int p = 0; p < candidatePacks.Count; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pack = candidatePacks[p];
            var recognizer = await GetOrLoadRecognizerAsync(pack, cancellationToken).ConfigureAwait(false);
            var readings = recognizer.Recognize(crops, options);

            double confSum = 0;
            for (int i = 0; i < n && i < readings.Count; i++)
            {
                var reading = readings[i];
                confSum += reading.Confidence;
                // Keep this pack's reading for crop i if it beats the best so far (or is the first reading).
                if (bestPack[i] is null || reading.Confidence > best[i].Confidence)
                {
                    best[i] = reading;
                    bestPack[i] = pack;
                }
            }

            // Fast path: after the very first (default) pack, accept immediately when it is already confident
            // and the page is dominantly Latin/CJK — avoids downloading the remaining candidate models.
            if (p == 0 && pack.Name == StructureModelRegistry.MobileRecognizer.Name && n > 0)
            {
                double meanConf = confSum / n;
                string combined = string.Concat(readings.Take(n).Select(r => r.Text));
                var script = ScriptDetection.DominantScript(combined);
                bool latinOrCjk = script is DetectedScript.Latin or DetectedScript.Han
                    or DetectedScript.Kana or DetectedScript.Hangul;
                if (meanConf >= AutoFastPathConfidence && latinOrCjk)
                {
                    // Report the script the default pack actually read (latin/ch/japan/korean), which is more
                    // informative than the pack's first registered code.
                    if (detectedLanguagesSink is not null)
                    {
                        var code = ScriptDetection.ToLanguageCode(script) ?? pack.Languages.FirstOrDefault() ?? pack.Name;
                        if (!detectedLanguagesSink.Contains(code, StringComparer.OrdinalIgnoreCase))
                            detectedLanguagesSink.Add(code);
                    }
                    _logger?.LogInformation(
                        "Auto-detect fast path: default pack mean confidence {Conf:F2} on {Script} text; skipping {Remaining} other candidates.",
                        meanConf, script, candidatePacks.Count - 1);
                    return best;
                }
            }
        }

        // Tally the vote: each crop's winning pack gets its winning confidence added to its running score.
        for (int i = 0; i < n; i++)
        {
            var pack = bestPack[i];
            if (pack is null) continue;
            packScore[pack.Name] = packScore.GetValueOrDefault(pack.Name) + best[i].Confidence;
        }

        // Report the winning language(s): packs that actually won crops, ordered by weighted score (most
        // first). Map each back to its representative language code.
        var ranked = candidatePacks
            .Where(pk => packScore.ContainsKey(pk.Name))
            .OrderByDescending(pk => packScore[pk.Name])
            .Select(pk => (pk, packScore[pk.Name]))
            .ToArray();
        AppendDetectedLanguages(detectedLanguagesSink, ranked);

        if (ranked.Length > 0)
        {
            _logger?.LogInformation("Auto-detect chose '{Lang}' (won the weighted crop vote across {Packs} packs).",
                ranked[0].pk.Languages.FirstOrDefault() ?? ranked[0].pk.Name, candidatePacks.Count);
        }

        return best;
    }

    /// <summary>
    /// Appends the representative language code of each scored pack (highest score first) to
    /// <paramref name="sink"/>, de-duplicating. No-op when <paramref name="sink"/> is null.
    /// </summary>
    private static void AppendDetectedLanguages(List<string>? sink, IReadOnlyList<(RecognizerPack Pack, double Score)> ranked)
    {
        if (sink is null) return;
        foreach (var (pack, _) in ranked)
        {
            var code = pack.Languages.FirstOrDefault() ?? pack.Name;
            if (!sink.Contains(code, StringComparer.OrdinalIgnoreCase)) sink.Add(code);
        }
    }

    /// <summary>
    /// Resolves language codes to their recognizer packs, preserving the input order and dropping duplicate
    /// packs (two codes can map to the same pack) and unsupported codes. Unlike <see cref="ResolvePacks"/>
    /// this keeps the caller's ordering, which the auto-detect fast path relies on (default pack first).
    /// </summary>
    private IReadOnlyList<RecognizerPack> ResolveOrderedPacks(IReadOnlyList<string> languages)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packs = new List<RecognizerPack>(languages.Count);
        foreach (var lang in languages)
        {
            if (string.Equals(lang, "auto", StringComparison.OrdinalIgnoreCase)) continue;
            var pack = StructureModelRegistry.FindByLanguage(lang);
            if (pack is null)
            {
                _logger?.LogWarning("Auto-detect candidate '{Lang}' is not supported by any recognizer pack.", lang);
                continue;
            }
            if (seen.Add(pack.Name)) packs.Add(pack);
        }
        return packs;
    }

    /// <summary>
    /// Eagerly loads the DB detector, the recognizer pack(s) for the given languages, and (when enabled)
    /// the text-line classifier so the first real OCR call doesn't pay cold-start latency.
    /// </summary>
    public async Task WarmUp(IReadOnlyList<string> languages, CancellationToken cancellationToken)
    {
        await GetOrLoadDetectorAsync(cancellationToken).ConfigureAwait(false);
        if (_options.UseTextLineOrientation)
        {
            await GetOrLoadClassifierAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var pack in ResolvePacks(languages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await GetOrLoadRecognizerAsync(pack, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the shared <see cref="ITextRecognizer"/> for the first resolvable language (falling back to
    /// the default pack), loading and caching it via the same on-demand path as the OCR pipeline. Used by
    /// the structure subsystem (e.g. the seal recognizer) so it reuses the one cached recognizer session
    /// rather than loading its own. The returned recognizer is owned by this engine and disposed by it.
    /// </summary>
    public Task<ITextRecognizer> GetSharedRecognizerAsync(IReadOnlyList<string> languages, CancellationToken cancellationToken)
    {
        var pack = ResolvePacks(languages).FirstOrDefault() ?? StructureModelRegistry.MobileRecognizer;
        return GetOrLoadRecognizerAsync(pack, cancellationToken);
    }

    /// <summary>
    /// Resolves the distinct recognizer packs for the requested languages (unsupported codes are skipped).
    /// </summary>
    private IReadOnlyList<RecognizerPack> ResolvePacks(IReadOnlyList<string> languages)
    {
        var packs = new Dictionary<string, RecognizerPack>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in languages)
        {
            var pack = StructureModelRegistry.FindByLanguage(lang);
            if (pack is null)
            {
                _logger?.LogWarning("Language '{Lang}' is not supported by any recognizer pack.", lang);
                continue;
            }
            packs[pack.Name] = pack;
        }
        return packs.Values.ToArray();
    }

    // ---- reading order (PaddleOCR's sorted_boxes) ----

    /// <summary>
    /// Vertical reading tolerance (px) within which two boxes are treated as being on the same text line
    /// and therefore ordered left-to-right. PaddleOCR's <c>sorted_boxes</c> uses 10px.
    /// </summary>
    private const double SameLineTolerance = 10.0;

    /// <summary>
    /// Orders detected polygons into reading order — PaddleOCR's <c>sorted_boxes</c>. Boxes are stably
    /// sorted by top-y then left-x, then a single adjacent-swap (bubble) pass fixes pairs that belong to
    /// the same text line (their top-y differs by less than <see cref="SameLineTolerance"/>) but landed
    /// out of left-to-right order.
    /// </summary>
    private static IReadOnlyList<OcrPoint[]> SortBoxes(IReadOnlyList<OcrPoint[]> polygons)
    {
        if (polygons.Count <= 1) return polygons;
        var boxes = polygons
            .Select(p => (Poly: p, Box: OcrBoundingBox.FromPoints(p)))
            .ToList();
        SortByReadingOrder(boxes, b => b.Box);
        return boxes.Select(b => b.Poly).ToArray();
    }

    /// <summary>
    /// Orders recognized lines into reading order using the same <c>sorted_boxes</c> rule as
    /// <see cref="SortBoxes"/>, keying off each line's axis-aligned bounding box.
    /// </summary>
    private static List<OcrLine> SortLines(List<OcrLine> lines, bool rightToLeft = false)
    {
        if (lines.Count <= 1) return lines;
        SortByReadingOrder(lines, l => l.BoundingBox, rightToLeft);
        return lines;
    }

    /// <summary>
    /// In-place <c>sorted_boxes</c>: a stable sort by top-y then left-x, followed by a one-pass bubble
    /// that swaps an out-of-order neighbour pair that actually sits on the same text line.
    /// </summary>
    private static void SortByReadingOrder<T>(List<T> items, Func<T, OcrBoundingBox> boxOf, bool rightToLeft = false)
    {
        // Stable primary sort: top-y, then left-x. List.Sort is not stable, so use OrderBy/ThenBy (stable)
        // and copy back — matching numpy's lexicographic sort used by PaddleOCR. On a right-to-left page the
        // horizontal key inverts: right-most first, keyed on MaxX so a short fragment to the right of a
        // longer one still leads.
        var byRow = items.OrderBy(i => boxOf(i).MinY);
        var sorted = (rightToLeft
            ? byRow.ThenByDescending(i => boxOf(i).MaxX)
            : byRow.ThenBy(i => boxOf(i).MinX))
            .ToList();
        for (int i = 0; i < items.Count; i++) items[i] = sorted[i];

        // Bubble pass: when two adjacent boxes are within the same-line tolerance vertically but the
        // earlier one is further right, swap them so the line reads left-to-right. The inner loop walks
        // back while the pair stays on the same line; the `else break` is load-bearing — the moment a
        // neighbour is on a *different* line we must stop, otherwise a later same-line pair could pull a
        // box across a line boundary and corrupt the vertical order.
        for (int i = 0; i < items.Count - 1; i++)
        {
            for (int j = i; j >= 0; j--)
            {
                var a = boxOf(items[j + 1]);
                var b = boxOf(items[j]);
                bool outOfOrder = rightToLeft ? a.MaxX > b.MaxX : a.MinX < b.MinX;
                if (Math.Abs(a.MinY - b.MinY) < SameLineTolerance && outOfOrder)
                {
                    (items[j], items[j + 1]) = (items[j + 1], items[j]);
                }
                else
                {
                    break;
                }
            }
        }
    }

    // ---- session loading (wired; reused by the pipeline methods) ----

    private async Task<ITextDetector> GetOrLoadDetectorAsync(CancellationToken cancellationToken)
    {
        if (_detector is not null) return _detector;

        await _detectorLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_detector is not null) return _detector;
            var path = await ModelDownloadManager.EnsureModelAsync(
                StructureModelRegistry.Detector, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
            _detector = CreateSessionBacked(so => (ITextDetector)new DbTextDetector(new InferenceSession(path, so)), recognizer: false);
            StructureDiagnostics.ModelLoads.Add(1, new KeyValuePair<string, object?>("model", "det"));
            _logger?.LogInformation("DB detector loaded from {Path}", path);
            return _detector;
        }
        finally
        {
            _detectorLock.Release();
        }
    }

    private async Task<IAngleClassifier> GetOrLoadClassifierAsync(CancellationToken cancellationToken)
    {
        if (_classifier is not null) return _classifier;

        await _classifierLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_classifier is not null) return _classifier;
            var path = await ModelDownloadManager.EnsureModelAsync(
                StructureModelRegistry.Classifier, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
            _classifier = CreateSessionBacked(so => (IAngleClassifier)new TextLineClassifier(new InferenceSession(path, so)), recognizer: true);
            StructureDiagnostics.ModelLoads.Add(1, new KeyValuePair<string, object?>("model", "cls"));
            _logger?.LogInformation("Text-line orientation classifier loaded from {Path}", path);
            return _classifier;
        }
        finally
        {
            _classifierLock.Release();
        }
    }

    private async Task<ITextRecognizer> GetOrLoadRecognizerAsync(RecognizerPack pack, CancellationToken cancellationToken)
    {
        // The load Task is shared across all callers, so it must NOT capture any single caller's
        // CancellationToken — otherwise the first caller cancelling would poison the cached Task and
        // every later caller (with a live token) would observe that cancellation. Load with None and
        // let each caller observe only its own token via WaitAsync.
        var lazy = _recognizers.GetOrAdd(pack.Name, _ => new Lazy<Task<ITextRecognizer>>(
            () => LoadRecognizerAsync(pack, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));

        var loadTask = lazy.Value;
        try
        {
            return await loadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // If the shared load itself failed (download/IO/corrupt model), evict the poisoned entry so a
            // later call retries instead of being served the cached faulted Task forever. A per-caller
            // cancellation leaves a still-running/successful task in place, so only the genuine fault path
            // evicts. TryRemove(pair) is a CAS: it removes only when the value is still this exact Lazy.
            if (loadTask.IsFaulted)
            {
                _recognizers.TryRemove(new KeyValuePair<string, Lazy<Task<ITextRecognizer>>>(pack.Name, lazy));
            }
            throw;
        }
    }

    private async Task<ITextRecognizer> LoadRecognizerAsync(RecognizerPack pack, CancellationToken cancellationToken)
    {
        var modelPath = await ModelDownloadManager.EnsureModelAsync(
            pack.Model, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
        var dictPath = await ModelDownloadManager.EnsureModelAsync(
            pack.Dictionary, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);

        // Pass the raw dictionary lines; SvtrRecognizer builds the vocab to match the model's class count.
        var dictLines = CharacterDictionary.LoadLines(dictPath);
        StructureDiagnostics.ModelLoads.Add(1, new KeyValuePair<string, object?>("model", pack.Name));
        _logger?.LogInformation("Recognizer '{Name}' loaded from {Path} ({Count} dict lines)", pack.Name, modelPath, dictLines.Count);
        return CreateSessionBacked(so => (ITextRecognizer)new SvtrRecognizer(new InferenceSession(modelPath, so), dictLines), recognizer: true);
    }

    /// <summary>
    /// Creates an ONNX-session-backed object via <paramref name="factory"/> using the active session
    /// options. When an accelerated provider was resolved but its session fails to initialize, this
    /// permanently downgrades the engine to CPU and retries once, so the very first model load can't
    /// hard-fail on a bad accelerator.
    /// </summary>
    private T CreateSessionBacked<T>(Func<SessionOptions, T> factory, bool recognizer)
    {
        var primary = recognizer ? _recognizerSessionOptions : _detectorSessionOptions;
        if (_activeProvider == OcrExecutionProvider.Cpu)
        {
            var fallback = recognizer ? _recognizerCpuFallback : _detectorCpuFallback;
            return factory(fallback ?? primary);
        }

        try
        {
            return factory(primary);
        }
        catch (Exception ex)
        {
            return factory(DowngradeToCpu(ex, recognizer));
        }
    }

    private SessionOptions DowngradeToCpu(Exception cause, bool recognizer)
    {
        lock (_fallbackLock)
        {
            if (_detectorCpuFallback is null)
            {
                _logger?.LogWarning(cause,
                    "{Provider} session initialization failed at model load; falling back to CPU for all sessions.",
                    _activeProvider);
                _detectorCpuFallback = ExecutionProviderResolver.BuildSessionOptions(OcrExecutionProvider.Cpu, _options, _logger, perBoxParallel: false);
                _recognizerCpuFallback = ExecutionProviderResolver.BuildSessionOptions(OcrExecutionProvider.Cpu, _options, _logger, perBoxParallel: true);
                _activeProvider = OcrExecutionProvider.Cpu;
            }
            return (recognizer ? _recognizerCpuFallback : _detectorCpuFallback)!;
        }
    }

    /// <summary>
    /// When auto-detection landed on CPU but the host actually has a GPU, builds the package-specific
    /// upgrade hint. The string is always returned (and exposed via <see cref="GpuHint"/>); it is only
    /// logged as a startup warning when <paramref name="logHint"/> is true (off by default).
    /// </summary>
    private static string? BuildGpuHint(OcrExecutionProvider requested, OcrExecutionProvider resolved, bool logHint, ILogger? logger)
    {
        if (requested != OcrExecutionProvider.Auto || resolved != OcrExecutionProvider.Cpu) return null;

        var vendor = GpuProbe.Detect();
        if (vendor == GpuProbe.GpuVendor.None) return null;

        var message = vendor == GpuProbe.GpuVendor.Nvidia
            ? "EasyOcrSharp: an NVIDIA GPU was detected but OCR is running on CPU. Install the " +
              "'EasyOcrSharp.Gpu' NuGet package for CUDA acceleration. It is then used automatically — " +
              "no code change needed."
            : $"EasyOcrSharp: a GPU ({vendor}) was detected but OCR is running on CPU. GPU acceleration is " +
              "currently available for NVIDIA GPUs via the 'EasyOcrSharp.Gpu' package (CUDA 12+).";

        if (logHint) logger?.LogWarning("{GpuHint}", message);
        return message;
    }

    public async ValueTask DisposeAsync()
    {
        _detector?.Dispose();
        _detector = null;
        _classifier?.Dispose();
        _classifier = null;

        foreach (var entry in _recognizers.Values)
        {
            if (entry.IsValueCreated)
            {
                try { (await entry.Value.ConfigureAwait(false)).Dispose(); }
                catch { /* dispose best-effort */ }
            }
        }
        _recognizers.Clear();
        _detectorSessionOptions.Dispose();
        _recognizerSessionOptions.Dispose();
        _detectorCpuFallback?.Dispose();
        _recognizerCpuFallback?.Dispose();
    }
}
