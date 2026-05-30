using System.Diagnostics;
using System.Text;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace EasyOcrSharp.Services;

/// <summary>
/// High-level OCR service. Native .NET implementation running EasyOCR's CRAFT detector
/// and per-language CRNN recognizers via ONNX Runtime — no Python required.
/// </summary>
public sealed class EasyOcrService : IEasyOcrService
{
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

        var resolved = ResolveLanguages(languages);
        cancellationToken.ThrowIfCancellationRequested();
        using var image = await Image.LoadAsync<Rgb24>(fullPath, cancellationToken).ConfigureAwait(false);
        return await RecognizeAsync(image, resolved, options, cancellationToken).ConfigureAwait(false);
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

        var resolved = ResolveLanguages(languages);
        using var image = await Image.LoadAsync<Rgb24>(imageStream, cancellationToken).ConfigureAwait(false);
        return await RecognizeAsync(image, resolved, options, cancellationToken).ConfigureAwait(false);
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

        var resolved = ResolveLanguages(languages);
        cancellationToken.ThrowIfCancellationRequested();
        using var image = Image.Load<Rgb24>(imageBytes.Span);
        return await RecognizeAsync(image, resolved, options, cancellationToken).ConfigureAwait(false);
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
        var resolved = ResolveLanguages(languages);
        // Caller owns the image — do not dispose it here.
        return RecognizeAsync(image, resolved, options, cancellationToken);
    }

    private async Task<OcrResult> RecognizeAsync(
        Image<Rgb24> image,
        string[] resolved,
        RecognitionOptions? options,
        CancellationToken cancellationToken)
    {
        options ??= RecognitionOptions.Default;

        var sw = Stopwatch.StartNew();
        var lines = await _engine.RecognizeAsync(image, resolved, options, cancellationToken).ConfigureAwait(false);
        var ordered = SortLinesByReadingOrder(lines);
        sw.Stop();

        _logger?.LogInformation("OCR completed: {Count} lines in {Ms:F0} ms", ordered.Count, sw.Elapsed.TotalMilliseconds);

        return new OcrResult
        {
            FullText = BuildFullText(ordered),
            Lines = ordered,
            Languages = resolved,
            Duration = sw.Elapsed,
            UsedGpu = _useGpu,
        };
    }

    private static string[] ResolveLanguages(IEnumerable<string> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        var arr = languages
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (arr.Length == 0)
            throw new ArgumentException("At least one valid language must be specified.", nameof(languages));

        return arr;
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
