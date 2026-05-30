using System.Diagnostics;
using System.Text;
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
    {
        _logger = logger;
        _useGpu = useGpu;
        var cachePath = string.IsNullOrWhiteSpace(modelCachePath) ? null : Path.GetFullPath(modelCachePath);
        _engine = new OnnxEasyOcrEngine(cachePath, useGpu, logger);
    }

    /// <summary>
    /// Gets a value indicating whether GPU acceleration was requested for this service.
    /// (The CUDA provider may silently fall back to CPU if the GPU runtime is missing.)
    /// </summary>
    public bool UseGpu => _useGpu;

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

    // ---- pipeline ----

    private async Task<OcrResult> RunPipelineAsync(
        Image<Rgb24> image,
        IEnumerable<string> languages,
        RecognitionOptions? options,
        CancellationToken cancellationToken)
    {
        options ??= RecognitionOptions.Default;
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

        var ordered = SortLinesByReadingOrder(outcome.Lines);
        sw.Stop();
        _logger?.LogInformation("OCR completed: {Count} lines in {Ms:F0} ms", ordered.Count, sw.Elapsed.TotalMilliseconds);

        return new OcrResult
        {
            FullText = BuildFullText(ordered),
            Lines = ordered,
            Languages = outcome.Languages,
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
