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

using EasyOcrSharp.Internal;

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
    /// (or the EASYOCRSHARP_STRUCTURE_CACHE environment variable, if set).
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
    }

    /// <summary>
    /// Gets the execution provider the ONNX sessions are actually running on <b>right now</b> —
    /// <see cref="OcrExecutionProvider.Cpu"/> when a requested accelerator failed to attach or a session
    /// later degraded to CPU (see <see cref="GpuAccelerationHint"/> for why). This is the truthful, live
    /// counterpart of <see cref="UseGpu"/>.
    /// </summary>
    public OcrExecutionProvider ActiveExecutionProvider => _engine.ActiveProvider;

    /// <summary>
    /// Gets a value indicating whether a GPU accelerator is actually in use — equivalent to
    /// <see cref="ActiveExecutionProvider"/> being a non-CPU provider. Live: an accelerator that was
    /// requested but failed to attach (or degraded to CPU at a model load) reports <c>false</c>.
    /// </summary>
    public bool UseGpu => _engine.ActiveProvider != OcrExecutionProvider.Cpu;

    /// <summary>
    /// An actionable message explaining why OCR is running on CPU: the requested or auto-selected
    /// accelerator failed to attach (with the exact fix — e.g. the CUDA-toolkit-major-mismatch hint or the
    /// provider package to install), or <see cref="OcrExecutionProvider.Auto"/> fell back to CPU while a
    /// usable GPU is physically present. Populated for explicit provider requests too, not only Auto.
    /// Null when an accelerator is in use, CPU was chosen explicitly, or no GPU was detected.
    /// </summary>
    public string? GpuAccelerationHint => _engine.GpuHint;

    /// <summary>
    /// The text engine (DB detector, text-line classifier and recognizer packs) this service owns —
    /// exposed so <c>EasyOcrService</c>'s opt-in PP-OCRv5 plain-OCR path drives the very same ONNX
    /// sessions document analysis uses instead of loading a second set. Owned and disposed by this
    /// service; callers must hold their own dispose-drain scope around every use.
    /// </summary>
    internal StructureTextEngine TextEngine => _engine;

    /// <summary>
    /// Collects a one-call diagnostic snapshot answering "why is my GPU not used": ONNX Runtime's
    /// available providers, the requested/resolved/active execution providers, the GPU probe result and
    /// the acceleration hint. Cheap; safe to log at startup.
    /// </summary>
    public OcrRuntimeInfo GetRuntimeInfo()
        => OcrRuntimeInfo.Describe(
            _engineOptions.ExecutionProvider,
            _engine.ResolvedProvider,
            _engine.ActiveProvider,
            _engine.GpuHint);

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
        using var image = LoadGuarded(imageBytes.Span, cancellationToken);
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
        EnsureNotDisposed();
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("Image path must be provided.", nameof(imagePath));

        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"The image file '{fullPath}' could not be found.", fullPath);

        cancellationToken.ThrowIfCancellationRequested();
        using var image = await LoadGuarded(fullPath, cancellationToken).ConfigureAwait(false);
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
            return BuildResult(Array.Empty<OcrLine>(), langs, sw, activity, image.Width, image.Height, grouping: options.Grouping);
        }

        var lines = await _engine.RecognizeRegionsAsync(image, langs, polygons, options, cancellationToken).ConfigureAwait(false);
        return BuildResult(lines, langs, sw, activity, image.Width, image.Height, grouping: options.Grouping);
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
        using var image = LoadGuarded(imageBytes.Span, cancellationToken);
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
    /// Lazily creates (once) the structure engine, sharing this service's own <see cref="StructureTextEngine"/>
    /// (and therefore its det/cls/rec ONNX sessions) instead of letting the structure engine build a
    /// duplicate. The service retains ownership of the shared engine and disposes it in
    /// <see cref="DisposeAsync"/> after the structure engine is disposed.
    /// </summary>
    private StructureEngine GetOrCreateStructureEngine()
    {
        var existing = Volatile.Read(ref _structureEngine);
        if (existing is not null) return existing;
        lock (_structureEngineLock)
        {
            _structureEngine ??= new StructureEngine(_engineOptions, _engine, _logger);
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

        (IReadOnlyList<OcrLine> Lines, string[] Languages, IReadOnlyList<string> Detected, int Rotation, float DeskewAngle) outcome;

        if (options.Preprocessing.DetectOrientation)
        {
            outcome = await RecognizeBestOrientationAsync(image, languages, options, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            outcome = await CoreAsync(image, languages, options, cancellationToken).ConfigureAwait(false);
        }

        return BuildResult(outcome.Lines, outcome.Languages, sw, activity, image.Width, image.Height, outcome.Detected, options.Grouping, outcome.Rotation, outcome.DeskewAngle);
    }

    /// <summary>
    /// Sorts into reading order, records metrics/trace tags, and assembles the result.
    /// <paramref name="appliedRotation"/> is the clockwise rotation the pipeline applied to upright the
    /// page (0 when none): the returned quads are in the ORIGINAL image's frame, so the reading-order
    /// sort keys off each line's forward-rotated (uprighted) box — otherwise a 180°-rotated page would
    /// come out with its text order reversed.
    /// </summary>
    private OcrResult BuildResult(IReadOnlyList<OcrLine> lines, string[] languages, Stopwatch sw, Activity? activity, int sourceWidth = 0, int sourceHeight = 0, IReadOnlyList<string>? detectedLanguages = null, TextGrouping grouping = TextGrouping.Line, int appliedRotation = 0, float deskewAngle = 0f)
    {
        // Direction is derived from the requested languages: this engine has no per-call direction option.
        var ordered = SortLinesByReadingOrder(lines, appliedRotation, sourceWidth, sourceHeight, ScriptDirection.IsRightToLeft(languages));
        sw.Stop();
        _logger?.LogInformation("OCR completed: {Count} lines in {Ms:F0} ms", ordered.Count, sw.Elapsed.TotalMilliseconds);

        // Read the LIVE provider at result-build time: an accelerator that failed to attach (or degraded
        // to CPU at a model load) must not report UsedGpu = true.
        var activeProvider = _engine.ActiveProvider;
        bool usedGpu = activeProvider != OcrExecutionProvider.Cpu;

        StructureDiagnostics.Operations.Add(1);
        StructureDiagnostics.Duration.Record(sw.Elapsed.TotalMilliseconds);
        StructureDiagnostics.LinesRecognized.Add(ordered.Count);
        if (activity is not null)
        {
            activity.SetTag("paddleocr.languages", string.Join(",", languages));
            activity.SetTag("paddleocr.lines", ordered.Count);
            activity.SetTag("paddleocr.gpu", usedGpu);
            activity.SetTag("paddleocr.provider", activeProvider.ToString());
        }

        return new OcrResult
        {
            FullText = BuildFullText(ordered, grouping),
            Lines = ordered,
            Languages = languages,
            DetectedLanguages = detectedLanguages ?? Array.Empty<string>(),
            // AppliedRotation is the corrective clockwise rotation; the page was DETECTED as rotated by
            // the inverse (e.g. correction 270 ⇒ the page sat 90° clockwise from upright).
            DetectedOrientation = (360 - (appliedRotation % 360 + 360) % 360) % 360,
            DeskewAngle = deskewAngle,
            Duration = sw.Elapsed,
            UsedGpu = usedGpu,
            ExecutionProvider = activeProvider,
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
        };
    }

    /// <summary>
    /// Handles <see cref="PreprocessingOptions.DetectOrientation"/>: the page orientation is decided by
    /// the PP-LCNet document-orientation classifier in a single pass (the same model-based path Python's
    /// <c>use_doc_orientation_classify</c> runs, wired through <see cref="StructureTextEngine"/>). Only when
    /// that classifier model is unavailable (e.g. offline with an empty cache) does it fall back to the
    /// old brute force: OCR at 0/90/180/270° and keep the orientation with the strongest result.
    /// Either way the returned quads are mapped back into the original image's orientation and the
    /// corrective rotation is reported so <see cref="OcrResult.DetectedOrientation"/> can be filled.
    /// </summary>
    private async Task<(IReadOnlyList<OcrLine>, string[], IReadOnlyList<string>, int, float)> RecognizeBestOrientationAsync(
        Image<Rgb24> image, IEnumerable<string> languages, RecognitionOptions options, CancellationToken ct)
    {
        var langsList = languages.ToArray();

        // ---- model-based path (python parity): one pass with the doc-orientation classifier forced on.
        if (await _engine.TryEnsureDocOrientationAsync(ct).ConfigureAwait(false))
        {
            var clsOptions = options with
            {
                Preprocessing = options.Preprocessing with { DetectOrientation = false },
                UseDocOrientation = true,
            };
            return await CoreAsync(image, langsList, clsOptions, ct).ConfigureAwait(false);
        }

        // ---- brute-force fallback (classifier model unavailable). Doc preprocessing stays off so the
        // engine never re-rotates the already-rotated candidates.
        _logger?.LogInformation("Doc-orientation classifier unavailable; falling back to brute-force 4-rotation OCR.");
        var noOrient = options with
        {
            Preprocessing = options.Preprocessing with { DetectOrientation = false },
            UseDocOrientation = false,
            UseDocUnwarp = false,
        };

        (IReadOnlyList<OcrLine> Lines, string[] Langs, IReadOnlyList<string> Detected, float Deskew)? best = null;
        double bestScore = double.NegativeInfinity;
        int bestDegrees = 0;

        foreach (var degrees in new[] { 0, 90, 180, 270 })
        {
            ct.ThrowIfCancellationRequested();
            Image<Rgb24>? rotated = degrees == 0 ? null : ImagePreprocessor.RotateRightAngle(image, degrees);
            try
            {
                var (lines, langs, detected, _, deskew) = await CoreAsync(rotated ?? image, langsList, noOrient, ct).ConfigureAwait(false);
                double score = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).Sum(l => l.Confidence * l.Text.Length);
                _logger?.LogInformation("Orientation {Deg}° scored {Score:F1}", degrees, score);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = (lines, langs, detected, deskew);
                    bestDegrees = degrees;
                }
            }
            finally
            {
                rotated?.Dispose();
            }
        }

        if (best is not { } winner) return (Array.Empty<OcrLine>(), langsList, Array.Empty<string>(), 0, 0f);

        // Map the winning rotation's quads back into the original image's frame (the winning pass ran on
        // the rotated copy, whose dimensions are swapped for 90/270).
        var mapped = winner.Lines;
        if (bestDegrees != 0)
        {
            int rotatedWidth = bestDegrees is 90 or 270 ? image.Height : image.Width;
            int rotatedHeight = bestDegrees is 90 or 270 ? image.Width : image.Height;
            mapped = Geometry.OrientationMapper.MapLinesToOriginalFrame(mapped, bestDegrees, rotatedWidth, rotatedHeight);
        }
        return (mapped, winner.Langs, winner.Detected, bestDegrees, winner.Deskew);
    }

    /// <summary>
    /// Preprocess → resolve languages → region crop → recognize. The recognition itself is delegated to
    /// <see cref="StructureTextEngine.RecognizeAsync"/> (det → cls → rec), whose body the downstream agent fills.
    /// </summary>
    private async Task<(IReadOnlyList<OcrLine> Lines, string[] Languages, IReadOnlyList<string> Detected, int Rotation, float DeskewAngle)> CoreAsync(
        Image<Rgb24> image, IEnumerable<string> languages, RecognitionOptions options, CancellationToken ct)
    {
        // Denoise / deskew / binarize into a working image (orientation handled by the caller).
        bool needsPreprocess = options.Preprocessing.Denoise || options.Preprocessing.Deskew || options.Preprocessing.Binarize;
        float deskewRotation = 0f;
        Image<Rgb24> working = needsPreprocess ? ImagePreprocessor.Apply(image, options.Preprocessing, out deskewRotation) : image;
        try
        {
            // The caller's region is in the source image's frame; re-express it on the deskewed canvas.
            if (deskewRotation != 0f && options.Region is { } region)
            {
                options = options with
                {
                    Region = ImagePreprocessor.MapRegionToRotatedCanvas(
                        region, deskewRotation, working.Width, working.Height, image.Width, image.Height),
                };
            }

            // "auto" is a detection trigger, not a recognizer language; allow it to be the only code (it is
            // dropped by the engine's candidate resolution) so callers can pass languages: ["auto"].
            var langs = ResolveLanguages(languages, allowEmpty: IsAutoRequested(languages, options));
            var (lines, detected, rotation) = await RecognizeWithRegionAsync(working, langs, options, ct).ConfigureAwait(false);

            // Deskew OCR'd the rotated, enlarged canvas: map every quad back onto the caller's image.
            if (deskewRotation != 0f)
            {
                lines = ImagePreprocessor.MapFromRotatedCanvas(
                    lines, deskewRotation, working.Width, working.Height, image.Width, image.Height);
            }
            return (lines, langs, detected, rotation, deskewRotation);
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
    private async Task<(IReadOnlyList<OcrLine> Lines, IReadOnlyList<string> Detected, int Rotation)> RecognizeWithRegionAsync(
        Image<Rgb24> image, string[] langs, RecognitionOptions options, CancellationToken ct)
    {
        if (options.Region is not { } region)
        {
            return await _engine.RecognizeWithDetectedLanguagesAsync(image, langs, options, ct).ConfigureAwait(false);
        }

        var (rx, ry, rw, rh) = region.Resolve(image.Width, image.Height);
        if (rw < 2 || rh < 2) return (Array.Empty<OcrLine>(), Array.Empty<string>(), 0);

        using var roi = image.Clone(ctx => ctx.Crop(new Rectangle(rx, ry, rw, rh)));
        var (roiLines, detected, rotation) = await _engine.RecognizeWithDetectedLanguagesAsync(roi, langs, options, ct).ConfigureAwait(false);
        return (TranslateLines(roiLines, rx, ry), detected, rotation);
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
                Words = Recognition.WordBoxBuilder.Transform(line.Words, p => new OcrPoint(p.X + dx, p.Y + dy)),
            });
        }
        return translated;
    }

    /// <summary>
    /// Orders lines into human reading order: split into columns by a clear vertical gutter (read each
    /// column top-to-bottom before moving right), and within a column chain boxes into rows by vertical
    /// overlap / centre proximity (a tolerance derived from the median line height) — so boxes on one
    /// visual row are never split by a band boundary, large headings / high-DPI scans stay together, and
    /// dense small text isn't merged. On a significantly skewed page the keys are deskewed first.
    /// </summary>
    internal static List<OcrLine> SortLinesByReadingOrder(IReadOnlyList<OcrLine> lines, bool rightToLeft = false)
        => SortLinesByReadingOrder(lines, 0, 0, 0, rightToLeft);

    /// <summary>
    /// Rotation- and skew-aware reading order. When the pipeline uprighted the page (document orientation)
    /// the returned quads are in the ORIGINAL image's frame, so sorting by those coordinates would read a
    /// 180°-rotated page bottom-up: each line's box is rotated forward into the uprighted frame (by
    /// <paramref name="appliedRotation"/>° clockwise, the same rotation the pipeline applied). When the
    /// uprighted quads then show a consistent text skew (<see cref="TextSkew"/>, |θ| ≥ 0.5°) they are also
    /// rotated by −θ. Both rotations only produce sort keys — the emitted lines keep their coordinates.
    /// </summary>
    internal static List<OcrLine> SortLinesByReadingOrder(IReadOnlyList<OcrLine> lines, int appliedRotation, int sourceWidth, int sourceHeight, bool rightToLeft = false)
    {
        if (lines.Count <= 1) return lines.ToList();

        bool rotate = ((appliedRotation % 360) + 360) % 360 != 0 && sourceWidth > 0 && sourceHeight > 0;
        var upright = new IReadOnlyList<OcrPoint>[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            var corners = TextSkew.CornersOf(lines[i]);
            upright[i] = rotate
                ? Geometry.OrientationMapper.RotatePolygon(corners, appliedRotation, sourceWidth, sourceHeight)
                : corners;
        }

        double skew = TextSkew.Estimate(upright);
        bool deskew = TextSkew.IsSignificant(skew);

        var keys = new OcrBoundingBox[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            keys[i] = !rotate && !deskew
                ? lines[i].BoundingBox
                : OcrBoundingBox.FromPoints(deskew ? upright[i].Select(p => TextSkew.Deskew(p, skew)) : upright[i]);
        }
        return SortByReadingOrder(lines, keys, rightToLeft);
    }

    /// <summary>
    /// Core of <see cref="SortLinesByReadingOrder(IReadOnlyList{OcrLine}, int, int, int, bool)"/>: columns,
    /// then rows chained within each column, then along each row, all on the given key boxes.
    /// </summary>
    private static List<OcrLine> SortByReadingOrder(IReadOnlyList<OcrLine> lines, IReadOnlyList<OcrBoundingBox> keys, bool rightToLeft)
    {
        var keyed = lines.Select((l, i) => (Line: l, Box: keys[i])).ToList();
        double medianHeight = Median(keyed.Select(k => k.Box.Height).Where(h => h > 0));
        double tol = Math.Max(4.0, 0.5 * medianHeight);

        var columns = DetectColumns(keyed, medianHeight);

        // A right-to-left page is read starting from the right-most column; within a row the right-most
        // fragment leads.
        if (rightToLeft) columns.Reverse();

        var result = new List<OcrLine>(lines.Count);
        foreach (var column in columns)
        {
            foreach (var row in ChainRows(column, tol))
            {
                result.AddRange((rightToLeft
                    ? row.OrderByDescending(k => k.Box.MaxX)
                    : row.OrderBy(k => k.Box.MinX))
                    .Select(k => k.Line));
            }
        }
        return result;
    }

    /// <summary>
    /// Greedy row chaining: walking the boxes by centre Y, each box joins the current row when it overlaps
    /// the row's mean band by at least half the smaller height, or its centre lies within
    /// <paramref name="tolerance"/> of the row's mean centre; otherwise it starts a new row. Unlike rounding
    /// Y into fixed bands, two boxes on one row can never be separated by a band edge.
    /// </summary>
    private static List<List<(OcrLine Line, OcrBoundingBox Box)>> ChainRows(
        IEnumerable<(OcrLine Line, OcrBoundingBox Box)> column, double tolerance)
    {
        var rows = new List<List<(OcrLine Line, OcrBoundingBox Box)>>();
        List<(OcrLine Line, OcrBoundingBox Box)>? current = null;
        double centerSum = 0, heightSum = 0;

        foreach (var item in column.OrderBy(k => k.Box.CenterY).ThenBy(k => k.Box.MinX))
        {
            if (current is not null
                && IsSameRow(centerSum / current.Count, heightSum / current.Count, item.Box, tolerance))
            {
                current.Add(item);
                centerSum += item.Box.CenterY;
                heightSum += item.Box.Height;
                continue;
            }

            current = new List<(OcrLine Line, OcrBoundingBox Box)> { item };
            rows.Add(current);
            centerSum = item.Box.CenterY;
            heightSum = item.Box.Height;
        }
        return rows;
    }

    /// <summary>
    /// Row-membership test used by <see cref="ChainRows"/>.
    /// </summary>
    internal static bool IsSameRow(double rowCenterY, double rowHeight, OcrBoundingBox box, double tolerance)
    {
        double overlap = Math.Min(rowCenterY + (rowHeight / 2.0), box.MaxY) - Math.Max(rowCenterY - (rowHeight / 2.0), box.MinY);
        double smaller = Math.Min(rowHeight, box.Height);
        if (smaller > 0 && overlap >= 0.5 * smaller) return true;
        return Math.Abs(box.CenterY - rowCenterY) <= tolerance;
    }

    /// <summary>
    /// Groups lines into left-to-right columns separated by a vertical gutter wider than the text. Uses an
    /// interval sweep over left edges: a new column starts only when the next box's left edge clears the
    /// running right edge of the current block by more than a gutter (so a full-width title, which bridges
    /// the gutter, collapses everything back to a single column). Conservative — returns one column when no
    /// clean gutter exists.
    /// </summary>
    private static List<List<(OcrLine Line, OcrBoundingBox Box)>> DetectColumns(
        IReadOnlyList<(OcrLine Line, OcrBoundingBox Box)> lines, double medianHeight)
    {
        var sorted = lines.OrderBy(l => l.Box.MinX).ToList();
        double gutter = Math.Max(20.0, 1.5 * medianHeight);

        var columns = new List<List<(OcrLine Line, OcrBoundingBox Box)>>();
        var current = new List<(OcrLine Line, OcrBoundingBox Box)> { sorted[0] };
        double runningMaxX = sorted[0].Box.MaxX;
        for (int i = 1; i < sorted.Count; i++)
        {
            var box = sorted[i].Box;
            if (box.MinX - runningMaxX > gutter)
            {
                columns.Add(current);
                current = new List<(OcrLine Line, OcrBoundingBox Box)>();
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

    /// <summary>
    /// Concatenates the recognized blocks into <see cref="OcrResult.FullText"/>.
    /// <para>
    /// Under <see cref="TextGrouping.Paragraph"/> each block is itself a multi-line paragraph (the merged
    /// lines are newline-joined by <see cref="ParagraphGrouper"/>), so blocks are separated by a BLANK line:
    /// with a single newline the paragraph boundary would be indistinguishable from the line breaks inside a
    /// paragraph, and the grouping would carry no information into the text. Word/line grouping keeps the
    /// one-newline-per-line layout, where every newline already means the same thing.
    /// </para>
    /// </summary>
    internal static string BuildFullText(IEnumerable<OcrLine> lines, TextGrouping grouping)
    {
        string separator = grouping == TextGrouping.Paragraph ? "\n\n" : "\n";

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line.Text)) continue;
            // LF-based separators, not AppendLine's Environment.NewLine: ParagraphGrouper joins merged lines
            // with LF, so on Windows a single FullText would otherwise mix both separators.
            if (sb.Length > 0) sb.Append(separator);
            // Trim the block's own trailing newline so the separator is never doubled into a wider gap.
            sb.Append(line.Text.TrimEnd('\r', '\n'));
        }
        return sb.ToString();
    }

    // ---- guarded image loading (decompression-bomb / pixel-flood DoS guard) ----

    /// <summary>
    /// The option named in an <c>ImageTooLargeException</c> raised from the decoder on this path.
    /// </summary>
    private const string PixelOptionName = "StructureServiceOptions.MaxImagePixels";

    private async Task<Image<Rgb24>> LoadGuarded(string path, CancellationToken ct)
    {
        if (_maxImagePixels > 0)
        {
            var info = await Image.IdentifyAsync(path, ct).ConfigureAwait(false);
            GuardPixels(info.Width, info.Height);
        }
        return await DecodeLimits.LoadAsync(path, _maxImagePixels, PixelOptionName, ct).ConfigureAwait(false);
    }

    private async Task<Image<Rgb24>> LoadGuarded(Stream stream, CancellationToken ct)
    {
        if (_maxImagePixels <= 0)
            return await DecodeLimits.LoadAsync(stream, _maxImagePixels, PixelOptionName, ct).ConfigureAwait(false);

        if (stream.CanSeek)
        {
            long pos = stream.Position;
            var info = await Image.IdentifyAsync(stream, ct).ConfigureAwait(false);
            GuardPixels(info.Width, info.Height);
            stream.Seek(pos, SeekOrigin.Begin);
            return await DecodeLimits.LoadAsync(stream, _maxImagePixels, PixelOptionName, ct).ConfigureAwait(false);
        }

        // Non-seekable: buffer the (small) compressed bytes once so we can inspect the header before
        // decoding into the full pixel buffer.
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
        return LoadGuarded(ms.GetBuffer().AsSpan(0, (int)ms.Length), ct);
    }

    private Image<Rgb24> LoadGuarded(ReadOnlySpan<byte> bytes, CancellationToken ct)
    {
        if (_maxImagePixels > 0)
        {
            var info = DecodeLimits.Identify(bytes, ct);
            GuardPixels(info.Width, info.Height);
        }
        return DecodeLimits.Load(bytes, _maxImagePixels, PixelOptionName, ct);
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
