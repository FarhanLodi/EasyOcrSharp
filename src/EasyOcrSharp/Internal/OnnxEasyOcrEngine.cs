using System.Collections.Concurrent;
using EasyOcrSharp.Diagnostics;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Top-level coordinator: loads the CRAFT detector once, lazy-loads recognizers per language pack,
/// and exposes a single Recognize entry point that produces OcrLines.
/// Thread-safe; all underlying ONNX sessions are reused across calls.
/// </summary>
internal sealed class OnnxEasyOcrEngine : IAsyncDisposable
{
    private readonly EngineOptions _options;
    private readonly ILogger? _logger;
    // Session options for the resolved provider. If an accelerated session fails to initialize at
    // model-load time we build a CPU set once and route every later session through it.
    private readonly SessionOptions _primarySessionOptions;
    private SessionOptions? _cpuFallbackOptions;
    private volatile OcrExecutionProvider _activeProvider;
    private readonly object _fallbackLock = new();

    private CraftDetector? _detector;
    private readonly SemaphoreSlim _detectorLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Lazy<Task<CrnnRecognizer>>> _recognizers = new(StringComparer.OrdinalIgnoreCase);
    // Custom recognizers, indexed by language code; consulted before the built-in registry.
    private readonly Dictionary<string, RecognizerSpec> _customByLanguage;

    public OnnxEasyOcrEngine(EngineOptions options, ILogger? logger)
    {
        _options = options;
        _logger = logger;
        ResolvedProvider = ExecutionProviderResolver.Resolve(options.ExecutionProvider, logger);
        _activeProvider = ResolvedProvider;
        _primarySessionOptions = ExecutionProviderResolver.BuildSessionOptions(ResolvedProvider, options, logger);
        GpuHint = BuildGpuHint(options.ExecutionProvider, ResolvedProvider, options.LogGpuHint, logger);
        _customByLanguage = BuildCustomIndex(options.CustomRecognizers);
    }

    /// <summary>
    /// The accelerator EasyOcrSharp resolved to attempt at startup — <see cref="OcrExecutionProvider.Auto"/>
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
    /// When auto-detection landed on CPU but the host actually has a GPU, builds the package-specific
    /// upgrade hint. The string is always returned (and exposed via <see cref="GpuHint"/>); it is only
    /// logged as a startup warning when <paramref name="logHint"/> is true (off by default, so the
    /// library stays silent). The provider package can't be added at runtime, so the most we can do is
    /// tell the user precisely which one to install.
    /// </summary>
    private static string? BuildGpuHint(OcrExecutionProvider requested, OcrExecutionProvider resolved, bool logHint, ILogger? logger)
    {
        // Only nudge when WE chose CPU via Auto. An explicit Cpu request is a deliberate choice (don't
        // nag); an explicit GPU request that's missing its package is already warned about at append time.
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

    private static Dictionary<string, RecognizerSpec> BuildCustomIndex(IReadOnlyList<CustomRecognizer> custom)
    {
        var map = new Dictionary<string, RecognizerSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in custom)
        {
            if (string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.ModelPath)) continue;
            if (string.IsNullOrEmpty(c.Characters) && string.IsNullOrEmpty(c.VocabPath))
                throw new EasyOcrSharpException($"Custom recognizer '{c.Name}' must set either Characters or VocabPath.");
            var spec = RecognizerSpec.FromCustom(c);
            foreach (var lang in c.Languages)
            {
                if (!string.IsNullOrWhiteSpace(lang)) map[lang.Trim()] = spec;
            }
        }
        return map;
    }

    /// <summary>
    /// Creates an ONNX-session-backed object via <paramref name="factory"/> using the active session
    /// options. When an accelerated provider was resolved but its session fails to initialize (e.g. the
    /// provider is compiled in yet no usable device is present), this permanently downgrades the engine
    /// to CPU and retries once, so the very first model load can't hard-fail on a bad accelerator.
    /// </summary>
    private T CreateSessionBacked<T>(Func<SessionOptions, T> factory)
    {
        if (_activeProvider == OcrExecutionProvider.Cpu)
            return factory(_cpuFallbackOptions ?? _primarySessionOptions);

        try
        {
            return factory(_primarySessionOptions);
        }
        catch (Exception ex)
        {
            return factory(DowngradeToCpu(ex));
        }
    }

    private SessionOptions DowngradeToCpu(Exception cause)
    {
        lock (_fallbackLock)
        {
            if (_cpuFallbackOptions is null)
            {
                _logger?.LogWarning(cause,
                    "{Provider} session initialization failed at model load; falling back to CPU for all sessions.",
                    _activeProvider);
                _cpuFallbackOptions = ExecutionProviderResolver.BuildSessionOptions(OcrExecutionProvider.Cpu, _options, _logger);
                _activeProvider = OcrExecutionProvider.Cpu;
            }
            return _cpuFallbackOptions;
        }
    }

    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(
        Image<Rgb24> image,
        IReadOnlyList<string> languages,
        RecognitionOptions options,
        CancellationToken cancellationToken)
    {
        var detector = await GetOrLoadDetectorAsync(cancellationToken).ConfigureAwait(false);
        var rawPolygons = detector.Detect(image, options.Detection);

        // Word grouping keeps the raw per-box detections; Line/Paragraph merge adjacent boxes
        // into reading-order lines (EasyOCR's group_text_box) before recognition.
        var polygons = options.Grouping == TextGrouping.Word
            ? rawPolygons
            : TextBoxGrouper.Group(rawPolygons, image.Width, image.Height, options.GroupingOptions);
        _logger?.LogInformation("CRAFT detected {Raw} regions, using {Count} regions ({Mode} grouping)",
            rawPolygons.Count, polygons.Count, options.Grouping);

        return await RecognizePolygonsAsync(image, languages, polygons, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recognizes a caller-supplied set of region polygons (skipping detection) — EasyOCR's
    /// <c>recognize()</c>. Applies the requested character filters, decoder and (when
    /// <see cref="TextGrouping.Paragraph"/>) paragraph merging.
    /// </summary>
    public Task<IReadOnlyList<OcrLine>> RecognizeRegionsAsync(
        Image<Rgb24> image,
        IReadOnlyList<string> languages,
        IReadOnlyList<OcrPoint[]> polygons,
        RecognitionOptions options,
        CancellationToken cancellationToken)
        => RecognizePolygonsAsync(image, languages, polygons, options, cancellationToken);

    private async Task<IReadOnlyList<OcrLine>> RecognizePolygonsAsync(
        Image<Rgb24> image,
        IReadOnlyList<string> languages,
        IReadOnlyList<OcrPoint[]> polygons,
        RecognitionOptions options,
        CancellationToken cancellationToken)
    {
        if (polygons.Count == 0) return Array.Empty<OcrLine>();

        var packs = ResolvePacks(languages);
        if (packs.Count == 0) return Array.Empty<OcrLine>();

        var run = CrnnRunOptions.FromRecognition(options);
        var perPackResults = new List<IReadOnlyList<OcrLine>>(packs.Count);
        foreach (var spec in packs.Values)
        {
            var recognizer = await GetOrLoadRecognizerAsync(spec, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            perPackResults.Add(recognizer.Recognize(image, polygons, run));
        }

        // Pick the highest-confidence non-empty result per polygon across packs.
        var merged = new List<OcrLine>(polygons.Count);
        for (int i = 0; i < polygons.Count; i++)
        {
            OcrLine? best = null;
            foreach (var pack in perPackResults)
            {
                if (i >= pack.Count) continue;
                var candidate = pack[i];
                if (string.IsNullOrWhiteSpace(candidate.Text)) continue;
                if (best is null || candidate.Confidence > best.Confidence)
                {
                    best = candidate;
                }
            }
            if (best is not null && best.Confidence >= options.MinConfidence) merged.Add(best);
        }

        return options.Grouping == TextGrouping.Paragraph
            ? ParagraphGrouper.Merge(merged, options.GroupingOptions)
            : merged;
    }

    /// <summary>Resolves the distinct recognizer packs (custom first, then built-in) for the languages.</summary>
    private Dictionary<string, RecognizerSpec> ResolvePacks(IReadOnlyList<string> languages)
    {
        var packs = new Dictionary<string, RecognizerSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in languages)
        {
            var spec = ResolveSpec(lang);
            if (spec is null)
            {
                _logger?.LogWarning("Language '{Lang}' is not supported by any recognizer pack.", lang);
                continue;
            }
            packs[spec.Name] = spec;
        }
        return packs;
    }

    private RecognizerSpec? ResolveSpec(string language)
    {
        if (_customByLanguage.TryGetValue(language, out var custom)) return custom;
        var def = ModelRegistry.FindByLanguage(language);
        if (def is null) return null;
        var spec = RecognizerSpec.FromDefinition(def);

        // EasyOCR's quantize=True: prefer the int8 recognizer variant when requested. Distinct cache
        // name so float and int8 sessions never collide; the vocab sidecar is shared (unchanged).
        if (_options.Quantize)
        {
            spec = spec with { Name = def.Name + "_int8", RemoteModel = ModelRegistry.QuantizedModel(def) };
        }
        return spec;
    }

    /// <summary>
    /// Detects which recognizer pack(s) best match the image by sampling the largest text regions and
    /// scoring each candidate pack by mean recognition confidence. Returns the representative language
    /// code of each winning pack (the dominant script, plus any co-dominant one).
    /// </summary>
    public async Task<IReadOnlyList<string>> DetectLanguagesAsync(
        Image<Rgb24> image,
        IReadOnlyList<string> candidateLanguages,
        CancellationToken cancellationToken)
    {
        var detector = await GetOrLoadDetectorAsync(cancellationToken).ConfigureAwait(false);
        var grouped = TextBoxGrouper.Group(detector.Detect(image), image.Width, image.Height);
        if (grouped.Count == 0) return Array.Empty<string>();

        // Sample the largest few regions — they're the most reliable for scoring a script.
        var samples = grouped
            .OrderByDescending(PolygonArea)
            .Take(5)
            .ToList();

        var packs = ResolvePacks(candidateLanguages);
        if (packs.Count == 0) return Array.Empty<string>();

        var probe = new CrnnRunOptions { MaxDegreeOfParallelism = 1, AdjustContrast = false };
        var scored = new List<(RecognizerSpec Spec, double Score)>(packs.Count);
        foreach (var spec in packs.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recognizer = await GetOrLoadRecognizerAsync(spec, cancellationToken).ConfigureAwait(false);
            var lines = recognizer.Recognize(image, samples, probe);
            var confs = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).Select(l => l.Confidence).ToList();
            double score = confs.Count > 0 ? confs.Average() : 0;
            _logger?.LogInformation("Auto-detect: pack '{Pack}' scored {Score:F3}", spec.Name, score);
            scored.Add((spec, score));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        double best = scored[0].Score;
        if (best <= 0) return Array.Empty<string>();

        // Keep the winner plus any co-dominant pack (helps bilingual pages).
        return scored
            .Where(s => s.Score >= Math.Max(0.35, 0.85 * best))
            .Select(s => s.Spec.Languages[0])
            .ToList();
    }

    /// <summary>Runs only the detector (no recognition) and returns the located regions.</summary>
    public async Task<IReadOnlyList<DetectedRegion>> DetectRegionsAsync(
        Image<Rgb24> image, DetectionOptions detection, TextGrouping grouping, GroupingOptions groupingOptions, CancellationToken cancellationToken)
    {
        var detector = await GetOrLoadDetectorAsync(cancellationToken).ConfigureAwait(false);
        var raw = detector.Detect(image, detection);
        var polygons = grouping == TextGrouping.Word
            ? raw
            : TextBoxGrouper.Group(raw, image.Width, image.Height, groupingOptions);

        var regions = new List<DetectedRegion>(polygons.Count);
        foreach (var poly in polygons)
        {
            regions.Add(new DetectedRegion
            {
                BoundingPolygon = poly,
                BoundingBox = OcrBoundingBox.FromPoints(poly),
            });
        }
        return regions;
    }

    private static double PolygonArea(OcrPoint[] poly)
    {
        var box = OcrBoundingBox.FromPoints(poly);
        return box.Width * box.Height;
    }

    private async Task<CraftDetector> GetOrLoadDetectorAsync(CancellationToken cancellationToken)
    {
        if (_detector is not null) return _detector;

        await _detectorLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_detector is not null) return _detector;
            var path = await ModelDownloadManager.EnsureModelAsync(ModelRegistry.Detector, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
            _detector = CreateSessionBacked(so => new CraftDetector(path, so));
            EasyOcrDiagnostics.ModelLoads.Add(1, new KeyValuePair<string, object?>("model", "craft"));
            _logger?.LogInformation("CRAFT detector loaded from {Path}", path);
            return _detector;
        }
        finally
        {
            _detectorLock.Release();
        }
    }

    private Task<CrnnRecognizer> GetOrLoadRecognizerAsync(RecognizerSpec spec, CancellationToken cancellationToken)
    {
        var lazy = _recognizers.GetOrAdd(spec.Name, _ => new Lazy<Task<CrnnRecognizer>>(
            () => LoadRecognizerAsync(spec, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    private async Task<CrnnRecognizer> LoadRecognizerAsync(RecognizerSpec spec, CancellationToken cancellationToken)
    {
        string modelPath;
        string characters;
        if (spec.IsLocal)
        {
            modelPath = spec.LocalModelPath!;
            if (!File.Exists(modelPath))
                throw new EasyOcrSharpException($"Custom recognizer '{spec.Name}' model not found at '{modelPath}'.");
            characters = spec.InlineCharacters is { Length: > 0 } inline
                ? inline
                : await ReadCharactersAsync(spec.LocalVocabPath!, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            modelPath = await ModelDownloadManager.EnsureModelAsync(spec.RemoteModel!, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
            var vocabPath = await ModelDownloadManager.EnsureModelAsync(spec.RemoteVocab!, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
            characters = await ReadCharactersAsync(vocabPath, cancellationToken).ConfigureAwait(false);
        }

        EasyOcrDiagnostics.ModelLoads.Add(1, new KeyValuePair<string, object?>("model", spec.Name));
        _logger?.LogInformation("Recognizer '{Name}' loaded from {Path} ({Count} chars)", spec.Name, modelPath, characters.Length);
        return CreateSessionBacked(so => new CrnnRecognizer(modelPath, characters, so));
    }

    /// <summary>
    /// Reads a vocabulary sidecar — the recognizer's exact ordered character set. Built-in packs encode
    /// it as a JSON string (so significant leading/trailing spaces survive); custom vocabularies may
    /// also be a plain UTF-8 text file, which is accepted verbatim.
    /// </summary>
    private static async Task<string> ReadCharactersAsync(string vocabPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(vocabPath))
            throw new EasyOcrSharpException($"Vocabulary file '{vocabPath}' was not found.");

        var raw = await File.ReadAllTextAsync(vocabPath, cancellationToken).ConfigureAwait(false);
        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith('"'))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var chars = doc.RootElement.GetString();
                    if (!string.IsNullOrEmpty(chars)) return chars;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Not JSON after all — fall through to plain-text handling.
            }
        }

        // Plain text: drop a single trailing newline but keep all other characters verbatim.
        var plain = raw.TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(plain))
            throw new EasyOcrSharpException($"Vocabulary file '{vocabPath}' is empty.");
        return plain;
    }

    public async ValueTask DisposeAsync()
    {
        _detector?.Dispose();
        _detector = null;

        foreach (var entry in _recognizers.Values)
        {
            if (entry.IsValueCreated)
            {
                try { (await entry.Value.ConfigureAwait(false)).Dispose(); }
                catch { /* dispose best-effort */ }
            }
        }
        _recognizers.Clear();
        _primarySessionOptions.Dispose();
        _cpuFallbackOptions?.Dispose();
    }
}
