using System.Diagnostics;
using System.Text;
using EasyOcrSharp.Structure.Engine;
using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure;
using Microsoft.Extensions.Logging;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Engine.Services;

/// <summary>
/// High-level OCR service. Native .NET implementation running PaddleOCR's DB detector, text-line
/// orientation classifier and SVTR/CRNN recognizers via ONNX Runtime — no Python required.
/// <para>
/// SKELETON: the public API surface, the decompression-bomb image guard, the dispose-drain concurrency
/// machinery, and the reading-order/result assembly are implemented and final. The OCR pipeline bodies
/// (<see cref="CoreAsync"/> and the orientation pass) delegate to <see cref="StructureTextEngine"/>, whose
/// pipeline methods a downstream agent fills in. Method names/signatures are final.
/// </para>
/// </summary>
internal sealed class StructureService : IStructureService
{
    private readonly ILogger<StructureService>? _logger;
    private readonly StructureTextEngine _engine;
    // Engine config retained so the structure engine (created lazily on first AnalyzeDocumentAsync) shares
    // the same provider / cache / download configuration as the OCR engine.
    private readonly StructureEngineOptions _engineOptions;
    private StructureEngine? _structureEngine;
    private readonly object _structureEngineLock = new();
    private readonly bool _useGpu;
    private readonly long _maxImagePixels;
    private volatile bool _disposed;
    // Count of OCR operations currently touching the engine's ONNX sessions. DisposeAsync drains this
    // to zero before disposing the sessions so a session is never freed while a Recognize is in flight
    // (which would be a native use-after-free, not a clean managed exception).
    private int _activeOperations;
    private int _disposeGuard;

    /// <summary>
    /// Initializes a new instance of the <see cref="StructureService"/> class.
    /// </summary>
    /// <param name="modelCachePath">
    /// Optional path where ONNX models should be cached. If null, uses LocalAppData\EasyOcrSharp.Structure.Engine\models
    /// (or the PADDLEOCRNET_CACHE environment variable, if set).
    /// </param>
    /// <param name="logger">Optional logger instance for diagnostic messages.</param>
    /// <param name="useGpu">
    /// If true, attempts to use the CUDA execution provider. Requires the EasyOcrSharp.Gpu package
    /// and a CUDA-capable GPU; silently falls back to CPU on failure.
    /// </param>
    public StructureService(string? modelCachePath = null, ILogger<StructureService>? logger = null, bool useGpu = false)
        : this(new StructureServiceOptions { ModelCachePath = modelCachePath, UseGpu = useGpu }, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance configured by <see cref="StructureServiceOptions"/> — the way to opt
    /// into execution providers, thread limits, the text-line orientation classifier, and download
    /// resilience.
    /// </summary>
    public StructureService(StructureServiceOptions options, ILogger<StructureService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        _maxImagePixels = options.MaxImagePixels;
        var engineOptions = options.ToEngineOptions();
        _engineOptions = engineOptions;
        _engine = new StructureTextEngine(engineOptions, logger);
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
    /// NVIDIA GPU). Null when a GPU is already in use, CPU was chosen explicitly, or no GPU was detected.
    /// </summary>
    public string? GpuAccelerationHint => _engine.GpuHint;

    /// <inheritdoc />
    public async Task<OcrResult> ExtractTextFromImage(
        string imagePath,
        IReadOnlyList<OcrLanguage> languages,
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
        using var image = await LoadGuarded(fullPath, cancellationToken).ConfigureAwait(false);
        return await RunPipelineAsync(image, languages.ToCodes(), options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<OcrResult> ExtractTextFromImage(
        Stream imageStream,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(imageStream);
        using var image = await LoadGuarded(imageStream, cancellationToken).ConfigureAwait(false);
        return await RunPipelineAsync(image, languages.ToCodes(), options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<OcrResult> ExtractTextFromImage(
        byte[] imageBytes,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        return ExtractTextFromImage(new ReadOnlyMemory<byte>(imageBytes), languages, options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<OcrResult> ExtractTextFromImage(
        ReadOnlyMemory<byte> imageBytes,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (imageBytes.IsEmpty)
            throw new ArgumentException("Image bytes must not be empty.", nameof(imageBytes));

        cancellationToken.ThrowIfCancellationRequested();
        using var image = LoadGuarded(imageBytes.Span);
        return await RunPipelineAsync(image, languages.ToCodes(), options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Internal code-based image OCR entry point. The public <c>Image&lt;Rgb24&gt;</c> overload is
    /// <see cref="OcrLanguage"/>-only; this string-code path backs it and is reused by the PDF pipeline
    /// (which carries string language codes) so rasterized pages don't have to be re-encoded. The caller
    /// owns the image — the pipeline never disposes the original.
    /// </summary>
    internal Task<OcrResult> OcrDecodedImageAsync(
        Image<Rgb24> image,
        IReadOnlyList<string> codes,
        RecognitionOptions? options,
        CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(image);
        return RunPipelineAsync(image, codes, options, cancellationToken);
    }

    /// <summary>
    /// OCR an already-decoded image across several <see cref="OcrLanguage"/> values (the in-memory entry point).
    /// </summary>
    public Task<OcrResult> ExtractTextFromImage(
        Image<Rgb24> image,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => OcrDecodedImageAsync(image, languages.ToCodes(), options, cancellationToken);

    // ---- single-language convenience overloads ----
    // Concrete here (not only as IStructureService default methods) so they're callable on a concrete
    // StructureService reference — e.g. new StructureService().ExtractTextFromImage("x.png", OcrLanguage.English).
    // Default of OcrLanguage.Auto makes the zero-config call (ExtractTextFromImage("x.png")) just work.

    /// <summary>
    /// OCR an image file in a single <see cref="OcrLanguage"/> (defaults to <see cref="OcrLanguage.Auto"/>).
    /// </summary>
    public Task<OcrResult> ExtractTextFromImage(
        string imagePath,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => ExtractTextFromImage(imagePath, new[] { language }, options, cancellationToken);

    /// <summary>
    /// OCR an image stream in a single <see cref="OcrLanguage"/> (defaults to <see cref="OcrLanguage.Auto"/>).
    /// </summary>
    public Task<OcrResult> ExtractTextFromImage(
        Stream imageStream,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => ExtractTextFromImage(imageStream, new[] { language }, options, cancellationToken);

    /// <summary>
    /// OCR an encoded image byte array in a single <see cref="OcrLanguage"/> (defaults to <see cref="OcrLanguage.Auto"/>).
    /// </summary>
    public Task<OcrResult> ExtractTextFromImage(
        byte[] imageBytes,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => ExtractTextFromImage(imageBytes, new[] { language }, options, cancellationToken);

    /// <summary>
    /// OCR encoded image bytes in a single <see cref="OcrLanguage"/> (defaults to <see cref="OcrLanguage.Auto"/>).
    /// </summary>
    public Task<OcrResult> ExtractTextFromImage(
        ReadOnlyMemory<byte> imageBytes,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => ExtractTextFromImage(imageBytes, new[] { language }, options, cancellationToken);

    /// <summary>
    /// OCR an already-decoded image in a single <see cref="OcrLanguage"/> (defaults to <see cref="OcrLanguage.Auto"/>).
    /// </summary>
    public Task<OcrResult> ExtractTextFromImage(
        Image<Rgb24> image,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => ExtractTextFromImage(image, new[] { language }, options, cancellationToken);

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
        using var op = BeginOperation();
        ArgumentNullException.ThrowIfNull(image);
        options ??= RecognitionOptions.Default;

        if (options.Region is not { } region)
        {
            return await _engine.DetectRegionsAsync(image, options.Detection, options.Grouping, cancellationToken).ConfigureAwait(false);
        }

        var (rx, ry, rw, rh) = region.Resolve(image.Width, image.Height);
        if (rw < 2 || rh < 2) return Array.Empty<DetectedRegion>();

        using var roi = image.Clone(ctx => ctx.Crop(new Rectangle(rx, ry, rw, rh)));
        var regions = await _engine.DetectRegionsAsync(roi, options.Detection, options.Grouping, cancellationToken).ConfigureAwait(false);
        return TranslateRegions(regions, rx, ry);
    }

    /// <summary>
    /// Locates text regions in an image file without recognizing them.
    /// </summary>
    public async Task<IReadOnlyList<DetectedRegion>> DetectRegionsAsync(
        string imagePath,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var image = await LoadGuarded(Path.GetFullPath(imagePath), cancellationToken).ConfigureAwait(false);
        return await DetectRegionsAsync(image, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recognizes text inside caller-supplied regions, <b>skipping detection</b>. Each region is a
    /// polygon (3+ points) in the image's pixel coordinates, e.g. from a prior
    /// <see cref="DetectRegionsAsync(Image{Rgb24}, RecognitionOptions?, CancellationToken)"/> pass or your
    /// own layout analysis. Boxes are reported back in the same coordinates.
    /// </summary>
    public async Task<OcrResult> RecognizeRegionsAsync(
        Image<Rgb24> image,
        IEnumerable<IReadOnlyList<OcrPoint>> regions,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var op = BeginOperation();
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(regions);
        options ??= RecognitionOptions.Default;

        var polygons = regions
            .Where(r => r is { Count: >= 3 })
            .Select(r => r.ToArray())
            .ToArray();

        using var activity = StructureDiagnostics.ActivitySource.StartActivity("Structure.Recognize", ActivityKind.Internal);
        var sw = Stopwatch.StartNew();

        var langs = ResolveLanguages(languages.ToCodes(), allowEmpty: false);
        if (polygons.Length == 0)
        {
            return BuildResult(Array.Empty<OcrLine>(), langs, sw, activity, image.Width, image.Height);
        }

        var lines = await _engine.RecognizeRegionsAsync(image, langs, polygons, options, cancellationToken).ConfigureAwait(false);
        return BuildResult(lines, langs, sw, activity, image.Width, image.Height);
    }

    /// <summary>
    /// Recognizes text inside regions located by a prior detection pass.
    /// </summary>
    public Task<OcrResult> RecognizeRegionsAsync(
        Image<Rgb24> image,
        IEnumerable<DetectedRegion> regions,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regions);
        return RecognizeRegionsAsync(image, regions.Select(r => r.BoundingPolygon), languages, options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task WarmUp(IReadOnlyList<OcrLanguage> languages, CancellationToken cancellationToken = default)
    {
        using var op = BeginOperation();
        var langs = ResolveLanguages(languages.ToCodes(), allowEmpty: false);
        await _engine.WarmUp(langs, cancellationToken).ConfigureAwait(false);
    }

    // ---- document-structure analysis (PP-StructureV3) ----

    /// <inheritdoc />
    public async Task<StructureResult> AnalyzeDocumentAsync(
        string imagePath,
        StructureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("Image path must be provided.", nameof(imagePath));

        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"The image file '{fullPath}' could not be found.", fullPath);

        cancellationToken.ThrowIfCancellationRequested();
        using var image = await LoadGuarded(fullPath, cancellationToken).ConfigureAwait(false);
        return await RunStructureAsync(image, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StructureResult> AnalyzeDocumentAsync(
        Stream imageStream,
        StructureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(imageStream);
        using var image = await LoadGuarded(imageStream, cancellationToken).ConfigureAwait(false);
        return await RunStructureAsync(image, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<StructureResult> AnalyzeDocumentAsync(
        byte[] imageBytes,
        StructureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        return AnalyzeDocumentAsync(new ReadOnlyMemory<byte>(imageBytes), options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StructureResult> AnalyzeDocumentAsync(
        ReadOnlyMemory<byte> imageBytes,
        StructureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (imageBytes.IsEmpty)
            throw new ArgumentException("Image bytes must not be empty.", nameof(imageBytes));

        cancellationToken.ThrowIfCancellationRequested();
        using var image = LoadGuarded(imageBytes.Span);
        return await RunStructureAsync(image, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<StructureResult> AnalyzeDocumentAsync(
        Image<Rgb24> image,
        StructureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(image);
        // Caller owns the image — RunStructureAsync never disposes the original.
        return RunStructureAsync(image, options, cancellationToken);
    }

    /// <summary>
    /// Runs the structure pipeline against the lazily-created <see cref="StructureEngine"/> (built on
    /// first use with the same engine configuration as the OCR engine), under the dispose-drain gate.
    /// </summary>
    private async Task<StructureResult> RunStructureAsync(
        Image<Rgb24> image, StructureOptions? options, CancellationToken cancellationToken)
    {
        using var op = BeginOperation();
        var engine = GetOrCreateStructureEngine();
        return await engine.AnalyzeAsync(image, options ?? StructureOptions.Default, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lazily creates (once) the structure engine, sharing the OCR engine's configuration.
    /// </summary>
    private StructureEngine GetOrCreateStructureEngine()
    {
        var existing = Volatile.Read(ref _structureEngine);
        if (existing is not null) return existing;
        lock (_structureEngineLock)
        {
            _structureEngine ??= new StructureEngine(_engineOptions, _logger);
            return _structureEngine;
        }
    }

    // ---- pipeline ----

    private async Task<OcrResult> RunPipelineAsync(
        Image<Rgb24> image,
        IEnumerable<string> languages,
        RecognitionOptions? options,
        CancellationToken cancellationToken)
    {
        using var op = BeginOperation();
        options ??= RecognitionOptions.Default;
        using var activity = StructureDiagnostics.ActivitySource.StartActivity("Structure.Extract", ActivityKind.Internal);
        var sw = Stopwatch.StartNew();

        (IReadOnlyList<OcrLine> Lines, string[] Languages, IReadOnlyList<string> Detected) outcome;

        if (options.Preprocessing.DetectOrientation)
        {
            outcome = await RecognizeBestOrientationAsync(image, languages, options, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            outcome = await CoreAsync(image, languages, options, cancellationToken).ConfigureAwait(false);
        }

        return BuildResult(outcome.Lines, outcome.Languages, sw, activity, image.Width, image.Height, outcome.Detected);
    }

    /// <summary>
    /// Sorts into reading order, records metrics/trace tags, and assembles the result.
    /// </summary>
    private OcrResult BuildResult(IReadOnlyList<OcrLine> lines, string[] languages, Stopwatch sw, Activity? activity, int sourceWidth = 0, int sourceHeight = 0, IReadOnlyList<string>? detectedLanguages = null)
    {
        var ordered = SortLinesByReadingOrder(lines);
        sw.Stop();
        _logger?.LogInformation("OCR completed: {Count} lines in {Ms:F0} ms", ordered.Count, sw.Elapsed.TotalMilliseconds);

        StructureDiagnostics.Operations.Add(1);
        StructureDiagnostics.Duration.Record(sw.Elapsed.TotalMilliseconds);
        StructureDiagnostics.LinesRecognized.Add(ordered.Count);
        if (activity is not null)
        {
            activity.SetTag("paddleocr.languages", string.Join(",", languages));
            activity.SetTag("paddleocr.lines", ordered.Count);
            activity.SetTag("paddleocr.gpu", _useGpu);
        }

        return new OcrResult
        {
            FullText = BuildFullText(ordered),
            Lines = ordered,
            Languages = languages,
            DetectedLanguages = detectedLanguages ?? Array.Empty<string>(),
            Duration = sw.Elapsed,
            UsedGpu = _useGpu,
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
        };
    }

    /// <summary>
    /// Runs OCR at 0/90/180/270° and keeps the orientation with the strongest result.
    /// </summary>
    private async Task<(IReadOnlyList<OcrLine>, string[], IReadOnlyList<string>)> RecognizeBestOrientationAsync(
        Image<Rgb24> image, IEnumerable<string> languages, RecognitionOptions options, CancellationToken ct)
    {
        var langsList = languages.ToArray();
        var noOrient = options with { Preprocessing = options.Preprocessing with { DetectOrientation = false } };

        (IReadOnlyList<OcrLine> Lines, string[] Langs, IReadOnlyList<string> Detected)? best = null;
        double bestScore = double.NegativeInfinity;

        foreach (var degrees in new[] { 0, 90, 180, 270 })
        {
            ct.ThrowIfCancellationRequested();
            Image<Rgb24>? rotated = degrees == 0 ? null : ImagePreprocessor.RotateRightAngle(image, degrees);
            try
            {
                var (lines, langs, detected) = await CoreAsync(rotated ?? image, langsList, noOrient, ct).ConfigureAwait(false);
                double score = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).Sum(l => l.Confidence * l.Text.Length);
                _logger?.LogInformation("Orientation {Deg}° scored {Score:F1}", degrees, score);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = (lines, langs, detected);
                }
            }
            finally
            {
                rotated?.Dispose();
            }
        }

        return best ?? (Array.Empty<OcrLine>(), langsList, Array.Empty<string>());
    }

    /// <summary>
    /// Preprocess → resolve languages → region crop → recognize. The recognition itself is delegated to
    /// <see cref="StructureTextEngine.RecognizeAsync"/> (det → cls → rec), whose body the downstream agent fills.
    /// </summary>
    private async Task<(IReadOnlyList<OcrLine> Lines, string[] Languages, IReadOnlyList<string> Detected)> CoreAsync(
        Image<Rgb24> image, IEnumerable<string> languages, RecognitionOptions options, CancellationToken ct)
    {
        // Denoise / deskew / binarize into a working image (orientation handled by the caller).
        bool needsPreprocess = options.Preprocessing.Denoise || options.Preprocessing.Deskew || options.Preprocessing.Binarize;
        Image<Rgb24> working = needsPreprocess ? ImagePreprocessor.Apply(image, options.Preprocessing) : image;
        try
        {
            // "auto" is a detection trigger, not a recognizer language; allow it to be the only code (it is
            // dropped by the engine's candidate resolution) so callers can pass languages: ["auto"].
            var langs = ResolveLanguages(languages, allowEmpty: IsAutoRequested(languages, options));
            var (lines, detected) = await RecognizeWithRegionAsync(working, langs, options, ct).ConfigureAwait(false);
            return (lines, langs, detected);
        }
        finally
        {
            if (needsPreprocess) working.Dispose();
        }
    }

    /// <summary>
    /// True when language auto-detection is requested — either <see cref="RecognitionOptions.AutoDetectLanguage"/>
    /// is set or the literal language code <c>"auto"</c> appears in the requested languages.
    /// </summary>
    private static bool IsAutoRequested(IEnumerable<string> languages, RecognitionOptions options)
        => options.AutoDetectLanguage
           || languages.Any(l => string.Equals(l?.Trim(), "auto", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Applies the optional region-of-interest crop and translates boxes back to image coordinates.
    /// </summary>
    private async Task<(IReadOnlyList<OcrLine> Lines, IReadOnlyList<string> Detected)> RecognizeWithRegionAsync(
        Image<Rgb24> image, string[] langs, RecognitionOptions options, CancellationToken ct)
    {
        if (options.Region is not { } region)
        {
            return await _engine.RecognizeWithDetectedLanguagesAsync(image, langs, options, ct).ConfigureAwait(false);
        }

        var (rx, ry, rw, rh) = region.Resolve(image.Width, image.Height);
        if (rw < 2 || rh < 2) return (Array.Empty<OcrLine>(), Array.Empty<string>());

        using var roi = image.Clone(ctx => ctx.Crop(new Rectangle(rx, ry, rw, rh)));
        var (roiLines, detected) = await _engine.RecognizeWithDetectedLanguagesAsync(roi, langs, options, ct).ConfigureAwait(false);
        return (TranslateLines(roiLines, rx, ry), detected);
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

    internal static IReadOnlyList<DetectedRegion> TranslateRegions(IReadOnlyList<DetectedRegion> regions, int dx, int dy)
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

    internal static IReadOnlyList<OcrLine> TranslateLines(IReadOnlyList<OcrLine> lines, int dx, int dy)
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

    /// <summary>
    /// Orders lines into human reading order: split into columns by a clear vertical gutter (read each
    /// column top-to-bottom before moving right), and within a column band rows by a tolerance derived
    /// from the median line height — so large headings / high-DPI scans aren't split across bands and
    /// dense small text isn't merged, unlike a fixed pixel tolerance.
    /// </summary>
    internal static List<OcrLine> SortLinesByReadingOrder(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count <= 1) return lines.ToList();

        double medianHeight = Median(lines.Select(l => (double)l.BoundingBox.Height).Where(h => h > 0));
        double tol = Math.Max(4.0, 0.5 * medianHeight);

        var result = new List<OcrLine>(lines.Count);
        foreach (var column in DetectColumns(lines, medianHeight))
        {
            result.AddRange(column
                .OrderBy(l => Math.Round(l.BoundingBox.MinY / tol) * tol)
                .ThenBy(l => l.BoundingBox.MinX));
        }
        return result;
    }

    /// <summary>
    /// Groups lines into left-to-right columns separated by a vertical gutter wider than the text. Uses an
    /// interval sweep over left edges: a new column starts only when the next box's left edge clears the
    /// running right edge of the current block by more than a gutter (so a full-width title, which bridges
    /// the gutter, collapses everything back to a single column). Conservative — returns one column when no
    /// clean gutter exists.
    /// </summary>
    private static List<List<OcrLine>> DetectColumns(IReadOnlyList<OcrLine> lines, double medianHeight)
    {
        var sorted = lines.OrderBy(l => l.BoundingBox.MinX).ToList();
        double gutter = Math.Max(20.0, 1.5 * medianHeight);

        var columns = new List<List<OcrLine>>();
        var current = new List<OcrLine> { sorted[0] };
        double runningMaxX = sorted[0].BoundingBox.MaxX;
        for (int i = 1; i < sorted.Count; i++)
        {
            var box = sorted[i].BoundingBox;
            if (box.MinX - runningMaxX > gutter)
            {
                columns.Add(current);
                current = new List<OcrLine>();
                runningMaxX = box.MaxX;
            }
            else
            {
                runningMaxX = Math.Max(runningMaxX, box.MaxX);
            }
            current.Add(sorted[i]);
        }
        columns.Add(current);
        return columns;
    }

    private static double Median(IEnumerable<double> values)
    {
        var arr = values.ToArray();
        if (arr.Length == 0) return 0;
        Array.Sort(arr);
        int mid = arr.Length / 2;
        return (arr.Length & 1) == 1 ? arr[mid] : (arr[mid - 1] + arr[mid]) / 2.0;
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

    // ---- guarded image loading (decompression-bomb / pixel-flood DoS guard) ----

    private async Task<Image<Rgb24>> LoadGuarded(string path, CancellationToken ct)
    {
        if (_maxImagePixels > 0)
        {
            var info = await Image.IdentifyAsync(path, ct).ConfigureAwait(false);
            GuardPixels(info.Width, info.Height);
        }
        return await Image.LoadAsync<Rgb24>(path, ct).ConfigureAwait(false);
    }

    private async Task<Image<Rgb24>> LoadGuarded(Stream stream, CancellationToken ct)
    {
        if (_maxImagePixels <= 0)
            return await Image.LoadAsync<Rgb24>(stream, ct).ConfigureAwait(false);

        if (stream.CanSeek)
        {
            long pos = stream.Position;
            var info = await Image.IdentifyAsync(stream, ct).ConfigureAwait(false);
            GuardPixels(info.Width, info.Height);
            stream.Seek(pos, SeekOrigin.Begin);
            return await Image.LoadAsync<Rgb24>(stream, ct).ConfigureAwait(false);
        }

        // Non-seekable: buffer the (small) compressed bytes once so we can inspect the header before
        // decoding into the full pixel buffer.
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
        return LoadGuarded(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    private Image<Rgb24> LoadGuarded(ReadOnlySpan<byte> bytes)
    {
        if (_maxImagePixels > 0)
        {
            var info = Image.Identify(bytes);
            GuardPixels(info.Width, info.Height);
        }
        return Image.Load<Rgb24>(bytes);
    }

    private void GuardPixels(int width, int height)
    {
        long pixels = (long)width * height;
        if (pixels > _maxImagePixels)
            throw new ImageTooLargeException(
                $"Image is {width}x{height} ({pixels:N0} px), exceeding the configured limit of " +
                $"{_maxImagePixels:N0} px (StructureServiceOptions.MaxImagePixels). Raise the limit or downscale " +
                "the image. This guard protects against decompression-bomb / pixel-flood denial of service.");
    }

    /// <summary>
    /// Releases the underlying ONNX sessions. Prefer <see cref="DisposeAsync"/>.
    /// </summary>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Asynchronously releases the underlying ONNX detector, classifier and recognizer sessions.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Dispose-once: a second concurrent or repeated call is a no-op.
        if (Interlocked.Exchange(ref _disposeGuard, 1) != 0) return;

        // Stop new operations entering the gate, then wait for everything already in flight to finish so
        // we never free an ONNX session out from under an active Recognize.
        _disposed = true;
        while (Volatile.Read(ref _activeOperations) > 0)
        {
            await Task.Delay(15).ConfigureAwait(false);
        }

        var structureEngine = Volatile.Read(ref _structureEngine);
        if (structureEngine is not null)
        {
            await structureEngine.DisposeAsync().ConfigureAwait(false);
        }
        await _engine.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StructureService));
    }

    /// <summary>
    /// Registers an in-flight engine operation and returns a scope that deregisters it on dispose.
    /// Increment-then-check ordering (paired with <see cref="DisposeAsync"/>'s set-then-drain) guarantees
    /// that once disposal starts no new operation slips past the gate, and disposal waits for every
    /// operation already past the gate to finish before the sessions are released.
    /// </summary>
    private OperationScope BeginOperation()
    {
        Interlocked.Increment(ref _activeOperations);
        if (_disposed)
        {
            Interlocked.Decrement(ref _activeOperations);
            throw new ObjectDisposedException(nameof(StructureService));
        }
        return new OperationScope(this);
    }

    private readonly struct OperationScope(StructureService owner) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref owner._activeOperations);
    }
}
