using System.Diagnostics;
using System.Text;
using EasyOcrSharp.Diagnostics;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace EasyOcrSharp.Services;

/// <summary>
/// High-level OCR service. Native .NET implementation running EasyOCR's CRAFT detector
/// and per-language CRNN recognizers via ONNX Runtime — no Python required.
/// </summary>
public sealed class EasyOcrService : IEasyOcrService
{
    /// <summary>Default candidate scripts considered by auto language detection.</summary>
    private static readonly string[] DefaultAutoDetectCandidates = { "en", "ru", "ch_sim", "ja", "ko" };

    private readonly ILogger<EasyOcrService>? _logger;
    private readonly OnnxEasyOcrEngine _engine;
    private readonly bool _useGpu;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="EasyOcrService"/> class.
    /// </summary>
    /// <param name="modelCachePath">
    /// Optional path where ONNX models should be cached. If null, uses LocalAppData\EasyOcrSharp\models
    /// (or the EASYOCRSHARP_CACHE environment variable, if set).
    /// </param>
    /// <param name="logger">Optional logger instance for diagnostic messages.</param>
    /// <param name="useGpu">
    /// If true, attempts to use the CUDA execution provider. Requires the EasyOcrSharp.Gpu package
    /// and a CUDA-capable GPU; silently falls back to CPU on failure.
    /// </param>
    public EasyOcrService(string? modelCachePath = null, ILogger<EasyOcrService>? logger = null, bool useGpu = false)
        : this(new EasyOcrServiceOptions { ModelCachePath = modelCachePath, UseGpu = useGpu }, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance configured by <see cref="EasyOcrServiceOptions"/> — the way to opt
    /// into execution providers, thread limits, and download resilience without changing the legacy
    /// constructor.
    /// </summary>
    public EasyOcrService(EasyOcrServiceOptions options, ILogger<EasyOcrService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        var engineOptions = options.ToEngineOptions();
        _engine = new OnnxEasyOcrEngine(engineOptions, logger);
        // ResolvedProvider has already turned Auto into a concrete choice based on the installed runtime.
        _useGpu = _engine.ResolvedProvider != OcrExecutionProvider.Cpu;
    }

    /// <summary>
    /// Gets a value indicating whether a GPU accelerator was selected for this service — either requested
    /// explicitly or chosen by <see cref="OcrExecutionProvider.Auto"/> detection. (The provider may still
    /// silently fall back to CPU if the device turns out to be unusable at the first model load.)
    /// </summary>
    public bool UseGpu => _useGpu;

    /// <summary>
    /// When <see cref="OcrExecutionProvider.Auto"/> fell back to CPU but a usable GPU is physically present,
    /// an actionable message naming the exact provider package to install (<c>EasyOcrSharp.Gpu</c> for an
    /// NVIDIA GPU, <c>EasyOcrSharp.DirectMl</c> otherwise). Null when a GPU is already in use, CPU was
    /// chosen explicitly, or no GPU was detected. The same text is also logged once at startup as a warning.
    /// </summary>
    public string? GpuAccelerationHint => _engine.GpuHint;

    /// <inheritdoc />
    public async Task<OcrResult> ExtractTextFromImage(
        string imagePath,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("Image path must be provided.", nameof(imagePath));

        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"The image file '{fullPath}' could not be found.", fullPath);

        cancellationToken.ThrowIfCancellationRequested();
        using var image = await Image.LoadAsync<Rgb24>(fullPath, cancellationToken).ConfigureAwait(false);
        return await RunPipelineAsync(image, languages, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<OcrResult> ExtractTextFromImage(
        Stream imageStream,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(imageStream);
        using var image = await Image.LoadAsync<Rgb24>(imageStream, cancellationToken).ConfigureAwait(false);
        return await RunPipelineAsync(image, languages, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<OcrResult> ExtractTextFromImage(
        byte[] imageBytes,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        return ExtractTextFromImage(new ReadOnlyMemory<byte>(imageBytes), languages, options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<OcrResult> ExtractTextFromImage(
        ReadOnlyMemory<byte> imageBytes,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (imageBytes.IsEmpty)
            throw new ArgumentException("Image bytes must not be empty.", nameof(imageBytes));

        cancellationToken.ThrowIfCancellationRequested();
        using var image = Image.Load<Rgb24>(imageBytes.Span);
        return await RunPipelineAsync(image, languages, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<OcrResult> ExtractTextFromImage(
        Image<Rgb24> image,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(image);
        // Caller owns the image — RunPipelineAsync never disposes the original.
        return RunPipelineAsync(image, languages, options, cancellationToken);
    }

    /// <summary>
    /// Detects the dominant script(s) of an image and returns representative language codes
    /// (e.g. "en", "ru", "ja"). Candidate scripts default to a common set; widen with
    /// <paramref name="candidates"/> to consider heavier scripts (e.g. "ar", "hi").
    /// </summary>
    public async Task<IReadOnlyList<string>> DetectLanguagesAsync(
        Image<Rgb24> image,
        IEnumerable<string>? candidates = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(image);
        var cand = (candidates?.ToArray() is { Length: > 0 } c) ? c : DefaultAutoDetectCandidates;
        return await _engine.DetectLanguagesAsync(image, cand, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Detects the dominant script(s) of an image file.</summary>
    public async Task<IReadOnlyList<string>> DetectLanguagesAsync(
        string imagePath,
        IEnumerable<string>? candidates = null,
        CancellationToken cancellationToken = default)
    {
        using var image = await Image.LoadAsync<Rgb24>(Path.GetFullPath(imagePath), cancellationToken).ConfigureAwait(false);
        return await DetectLanguagesAsync(image, candidates, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Locates text regions <b>without</b> recognizing them — fast, language-independent, and useful
    /// for layout analysis, redaction, or cropping fields for a later recognition pass. Honors
    /// <see cref="RecognitionOptions.Region"/>, <see cref="RecognitionOptions.Grouping"/> and
    /// <see cref="RecognitionOptions.Detection"/>; recognition-only options are ignored.
    /// </summary>
    public async Task<IReadOnlyList<DetectedRegion>> DetectRegionsAsync(
        Image<Rgb24> image,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(image);
        options ??= RecognitionOptions.Default;

        if (options.Region is not { } region)
        {
            return await _engine.DetectRegionsAsync(image, options.Detection, options.Grouping, options.GroupingOptions, cancellationToken).ConfigureAwait(false);
        }

        var (rx, ry, rw, rh) = region.Resolve(image.Width, image.Height);
        if (rw < 2 || rh < 2) return Array.Empty<DetectedRegion>();

        using var roi = image.Clone(ctx => ctx.Crop(new Rectangle(rx, ry, rw, rh)));
        var regions = await _engine.DetectRegionsAsync(roi, options.Detection, options.Grouping, options.GroupingOptions, cancellationToken).ConfigureAwait(false);
        return TranslateRegions(regions, rx, ry);
    }

    /// <summary>Locates text regions in an image file without recognizing them.</summary>
    public async Task<IReadOnlyList<DetectedRegion>> DetectRegionsAsync(
        string imagePath,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var image = await Image.LoadAsync<Rgb24>(Path.GetFullPath(imagePath), cancellationToken).ConfigureAwait(false);
        return await DetectRegionsAsync(image, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recognizes text inside caller-supplied regions, <b>skipping detection</b> — EasyOCR's
    /// <c>recognize()</c>. Each region is a polygon (3+ points) in the image's pixel coordinates, e.g.
    /// from a prior <see cref="DetectRegionsAsync(Image{Rgb24}, RecognitionOptions?, CancellationToken)"/>
    /// pass or your own layout analysis. Boxes are reported back in the same coordinates. Honors the
    /// character filters, decoder, rotation and paragraph grouping in <paramref name="options"/>;
    /// <see cref="RecognitionOptions.Region"/> and detection thresholds are ignored.
    /// </summary>
    public async Task<OcrResult> RecognizeRegionsAsync(
        Image<Rgb24> image,
        IEnumerable<IReadOnlyList<OcrPoint>> regions,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(regions);
        options ??= RecognitionOptions.Default;

        var polygons = regions
            .Where(r => r is { Count: >= 3 })
            .Select(r => r.ToArray())
            .ToArray();

        using var activity = EasyOcrDiagnostics.ActivitySource.StartActivity("EasyOcr.Recognize", ActivityKind.Internal);
        var sw = Stopwatch.StartNew();

        var langs = ResolveLanguages(languages, allowEmpty: false);
        if (polygons.Length == 0)
        {
            return BuildResult(Array.Empty<OcrLine>(), langs, sw, activity);
        }

        var lines = await _engine.RecognizeRegionsAsync(image, langs, polygons, options, cancellationToken).ConfigureAwait(false);
        return BuildResult(lines, langs, sw, activity);
    }

    /// <summary>Recognizes text inside regions located by a prior <see cref="DetectRegionsAsync(Image{Rgb24}, RecognitionOptions?, CancellationToken)"/> pass.</summary>
    public Task<OcrResult> RecognizeRegionsAsync(
        Image<Rgb24> image,
        IEnumerable<DetectedRegion> regions,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regions);
        return RecognizeRegionsAsync(image, regions.Select(r => r.BoundingPolygon), languages, options, cancellationToken);
    }

    // ---- pipeline ----

    private async Task<OcrResult> RunPipelineAsync(
        Image<Rgb24> image,
        IEnumerable<string> languages,
        RecognitionOptions? options,
        CancellationToken cancellationToken)
    {
        options ??= RecognitionOptions.Default;
        using var activity = EasyOcrDiagnostics.ActivitySource.StartActivity("EasyOcr.Extract", ActivityKind.Internal);
        var sw = Stopwatch.StartNew();

        (IReadOnlyList<OcrLine> Lines, string[] Languages) outcome;

        if (options.Preprocessing.DetectOrientation)
        {
            outcome = await RecognizeBestOrientationAsync(image, languages, options, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            outcome = await CoreAsync(image, languages, options, cancellationToken).ConfigureAwait(false);
        }

        return BuildResult(outcome.Lines, outcome.Languages, sw, activity);
    }

    /// <summary>Sorts into reading order, records metrics/trace tags, and assembles the result.</summary>
    private OcrResult BuildResult(IReadOnlyList<OcrLine> lines, string[] languages, Stopwatch sw, Activity? activity)
    {
        var ordered = SortLinesByReadingOrder(lines);
        sw.Stop();
        _logger?.LogInformation("OCR completed: {Count} lines in {Ms:F0} ms", ordered.Count, sw.Elapsed.TotalMilliseconds);

        EasyOcrDiagnostics.Operations.Add(1);
        EasyOcrDiagnostics.Duration.Record(sw.Elapsed.TotalMilliseconds);
        EasyOcrDiagnostics.LinesRecognized.Add(ordered.Count);
        if (activity is not null)
        {
            activity.SetTag("easyocr.languages", string.Join(",", languages));
            activity.SetTag("easyocr.lines", ordered.Count);
            activity.SetTag("easyocr.gpu", _useGpu);
        }

        return new OcrResult
        {
            FullText = BuildFullText(ordered),
            Lines = ordered,
            Languages = languages,
            Duration = sw.Elapsed,
            UsedGpu = _useGpu,
        };
    }

    /// <summary>Runs OCR at 0/90/180/270° and keeps the orientation with the strongest result.</summary>
    private async Task<(IReadOnlyList<OcrLine>, string[])> RecognizeBestOrientationAsync(
        Image<Rgb24> image, IEnumerable<string> languages, RecognitionOptions options, CancellationToken ct)
    {
        var langsList = languages.ToArray();
        var noOrient = options with { Preprocessing = options.Preprocessing with { DetectOrientation = false } };

        (IReadOnlyList<OcrLine> Lines, string[] Langs)? best = null;
        double bestScore = double.NegativeInfinity;

        foreach (var degrees in new[] { 0, 90, 180, 270 })
        {
            ct.ThrowIfCancellationRequested();
            Image<Rgb24>? rotated = degrees == 0 ? null : ImagePreprocessor.RotateRightAngle(image, degrees);
            try
            {
                var (lines, langs) = await CoreAsync(rotated ?? image, langsList, noOrient, ct).ConfigureAwait(false);
                double score = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).Sum(l => l.Confidence * l.Text.Length);
                _logger?.LogInformation("Orientation {Deg}° scored {Score:F1}", degrees, score);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = (lines, langs);
                }
            }
            finally
            {
                rotated?.Dispose();
            }
        }

        return best ?? (Array.Empty<OcrLine>(), langsList);
    }

    /// <summary>Preprocess → resolve/auto-detect languages → region crop → recognize.</summary>
    private async Task<(IReadOnlyList<OcrLine> Lines, string[] Languages)> CoreAsync(
        Image<Rgb24> image, IEnumerable<string> languages, RecognitionOptions options, CancellationToken ct)
    {
        // Denoise / deskew / binarize into a working image (orientation handled by the caller).
        bool needsPreprocess = options.Preprocessing.Denoise || options.Preprocessing.Deskew || options.Preprocessing.Binarize;
        Image<Rgb24> working = needsPreprocess ? ImagePreprocessor.Apply(image, options.Preprocessing) : image;
        try
        {
            string[] langs;
            if (options.AutoDetectLanguage)
            {
                var candidates = (options.AutoDetectCandidates?.ToArray() is { Length: > 0 } c) ? c : DefaultAutoDetectCandidates;
                var detected = await _engine.DetectLanguagesAsync(working, candidates, ct).ConfigureAwait(false);
                langs = detected.Count > 0 ? detected.ToArray() : ResolveLanguages(languages, allowEmpty: true);
                if (langs.Length == 0) langs = new[] { "en" };
                _logger?.LogInformation("Auto-detected languages: {Langs}", string.Join(", ", langs));
            }
            else
            {
                langs = ResolveLanguages(languages, allowEmpty: false);
            }

            var lines = await RecognizeRegionsAsync(working, langs, options, ct).ConfigureAwait(false);
            return (lines, langs);
        }
        finally
        {
            if (needsPreprocess) working.Dispose();
        }
    }

    /// <summary>Applies the optional region-of-interest crop and translates boxes back to image coordinates.</summary>
    private async Task<IReadOnlyList<OcrLine>> RecognizeRegionsAsync(
        Image<Rgb24> image, string[] langs, RecognitionOptions options, CancellationToken ct)
    {
        if (options.Region is not { } region)
        {
            return await _engine.RecognizeAsync(image, langs, options, ct).ConfigureAwait(false);
        }

        var (rx, ry, rw, rh) = region.Resolve(image.Width, image.Height);
        if (rw < 2 || rh < 2) return Array.Empty<OcrLine>();

        using var roi = image.Clone(ctx => ctx.Crop(new Rectangle(rx, ry, rw, rh)));
        var roiLines = await _engine.RecognizeAsync(roi, langs, options, ct).ConfigureAwait(false);
        return TranslateLines(roiLines, rx, ry);
    }

    // ---- helpers ----

    private static string[] ResolveLanguages(IEnumerable<string> languages, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(languages);
        var arr = languages
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (arr.Length == 0 && !allowEmpty)
            throw new ArgumentException("At least one valid language must be specified.", nameof(languages));

        return arr;
    }

    private static IReadOnlyList<DetectedRegion> TranslateRegions(IReadOnlyList<DetectedRegion> regions, int dx, int dy)
    {
        if (dx == 0 && dy == 0) return regions;
        var translated = new List<DetectedRegion>(regions.Count);
        foreach (var r in regions)
        {
            var poly = r.BoundingPolygon.Select(p => new OcrPoint(p.X + dx, p.Y + dy)).ToArray();
            translated.Add(r with { BoundingPolygon = poly, BoundingBox = OcrBoundingBox.FromPoints(poly) });
        }
        return translated;
    }

    private static IReadOnlyList<OcrLine> TranslateLines(IReadOnlyList<OcrLine> lines, int dx, int dy)
    {
        if (dx == 0 && dy == 0) return lines;
        var translated = new List<OcrLine>(lines.Count);
        foreach (var line in lines)
        {
            var poly = line.BoundingPolygon.Select(p => new OcrPoint(p.X + dx, p.Y + dy)).ToArray();
            var box = line.BoundingBox;
            translated.Add(line with
            {
                BoundingPolygon = poly,
                BoundingBox = new OcrBoundingBox(box.MinX + dx, box.MinY + dy, box.MaxX + dx, box.MaxY + dy),
            });
        }
        return translated;
    }

    private static List<OcrLine> SortLinesByReadingOrder(IReadOnlyList<OcrLine> lines)
    {
        const double yTolerance = 10.0;
        return lines
            .OrderBy(l => Math.Round(l.BoundingBox.MinY / yTolerance) * yTolerance)
            .ThenBy(l => l.BoundingBox.MinX)
            .ToList();
    }

    private static string BuildFullText(IEnumerable<OcrLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line.Text)) continue;
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(line.Text);
        }
        return sb.ToString();
    }

    /// <summary>Releases the underlying ONNX sessions. Prefer <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <summary>Asynchronously releases the underlying ONNX detector and recognizer sessions.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _engine.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EasyOcrService));
    }
}
