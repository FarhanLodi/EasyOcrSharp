using System.Collections.Concurrent;
using EasyOcrSharp.Models;
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
    private readonly string? _modelCachePath;
    private readonly ILogger? _logger;
    private readonly bool _useGpu;
    private readonly SessionOptions _sessionOptions;

    private CraftDetector? _detector;
    private readonly SemaphoreSlim _detectorLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Lazy<Task<CrnnRecognizer>>> _recognizers = new(StringComparer.OrdinalIgnoreCase);

    public OnnxEasyOcrEngine(string? modelCachePath, bool useGpu, ILogger? logger)
    {
        _modelCachePath = modelCachePath;
        _useGpu = useGpu;
        _logger = logger;
        _sessionOptions = BuildSessionOptions(useGpu, logger);
    }

    private static SessionOptions BuildSessionOptions(bool useGpu, ILogger? logger)
    {
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        if (useGpu)
        {
            try
            {
                opts.AppendExecutionProvider_CUDA();
                logger?.LogInformation("ONNX Runtime: CUDA execution provider enabled.");
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "CUDA execution provider unavailable. Falling back to CPU. Install EasyOcrSharp.Gpu for GPU support.");
            }
        }

        return opts;
    }

    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(
        Image<Rgb24> image,
        IReadOnlyList<string> languages,
        CancellationToken cancellationToken)
    {
        var detector = await GetOrLoadDetectorAsync(cancellationToken).ConfigureAwait(false);
        var rawPolygons = detector.Detect(image);
        // Group raw detections into reading-order lines (EasyOCR's group_text_box) so each
        // recognized region is a text line, matching upstream readtext() output.
        var polygons = TextBoxGrouper.Group(rawPolygons, image.Width, image.Height);
        _logger?.LogInformation("CRAFT detected {Raw} regions, grouped into {Count} lines", rawPolygons.Count, polygons.Count);

        if (polygons.Count == 0)
        {
            return Array.Empty<OcrLine>();
        }

        // Group requested languages by recognizer pack so we run each model at most once.
        var packs = new Dictionary<string, RecognizerDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in languages)
        {
            var def = ModelRegistry.FindByLanguage(lang);
            if (def is null)
            {
                _logger?.LogWarning("Language '{Lang}' is not supported by any recognizer pack.", lang);
                continue;
            }
            packs[def.Name] = def;
        }

        if (packs.Count == 0)
        {
            return Array.Empty<OcrLine>();
        }

        var perPackResults = new List<IReadOnlyList<OcrLine>>(packs.Count);
        foreach (var def in packs.Values)
        {
            var recognizer = await GetOrLoadRecognizerAsync(def, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var lines = recognizer.Recognize(image, polygons);
            perPackResults.Add(lines);
        }

        if (perPackResults.Count == 1) return perPackResults[0];

        // Pick the highest-confidence result per polygon across packs.
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
            if (best is not null) merged.Add(best);
        }
        return merged;
    }

    private async Task<CraftDetector> GetOrLoadDetectorAsync(CancellationToken cancellationToken)
    {
        if (_detector is not null) return _detector;

        await _detectorLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_detector is not null) return _detector;
            var path = await ModelDownloadManager.EnsureModelAsync(ModelRegistry.Detector, _modelCachePath, _logger, cancellationToken).ConfigureAwait(false);
            _detector = new CraftDetector(path, _sessionOptions);
            _logger?.LogInformation("CRAFT detector loaded from {Path}", path);
            return _detector;
        }
        finally
        {
            _detectorLock.Release();
        }
    }

    private Task<CrnnRecognizer> GetOrLoadRecognizerAsync(RecognizerDefinition def, CancellationToken cancellationToken)
    {
        var lazy = _recognizers.GetOrAdd(def.Name, _ => new Lazy<Task<CrnnRecognizer>>(
            () => LoadRecognizerAsync(def, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    private async Task<CrnnRecognizer> LoadRecognizerAsync(RecognizerDefinition def, CancellationToken cancellationToken)
    {
        var path = await ModelDownloadManager.EnsureModelAsync(def.Model, _modelCachePath, _logger, cancellationToken).ConfigureAwait(false);
        var vocabPath = await ModelDownloadManager.EnsureModelAsync(def.Vocab, _modelCachePath, _logger, cancellationToken).ConfigureAwait(false);
        var characters = await ReadVocabAsync(vocabPath, cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Recognizer '{Name}' loaded from {Path} ({Count} chars)", def.Name, path, characters.Length);
        return new CrnnRecognizer(path, characters, _sessionOptions);
    }

    /// <summary>
    /// Reads a vocabulary sidecar — a JSON-encoded string holding the recognizer's exact
    /// ordered character set. JSON encoding preserves significant leading/trailing spaces
    /// (e.g. latin_g2's vocab starts with a space) that a raw text file would make ambiguous.
    /// </summary>
    private static async Task<string> ReadVocabAsync(string vocabPath, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(vocabPath, cancellationToken).ConfigureAwait(false);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var chars = doc.RootElement.GetString();
        if (string.IsNullOrEmpty(chars))
        {
            throw new EasyOcrSharpException($"Vocabulary file '{vocabPath}' is empty or not a JSON string.");
        }
        return chars;
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
        _sessionOptions.Dispose();
    }
}
