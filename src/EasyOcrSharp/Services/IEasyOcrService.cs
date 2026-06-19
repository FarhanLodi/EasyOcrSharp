using EasyOcrSharp.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace EasyOcrSharp.Services;

/// <summary>
/// Abstraction over <see cref="EasyOcrService"/> for dependency injection and testing.
/// Register with <c>services.AddEasyOcrSharp()</c>.
/// </summary>
public interface IEasyOcrService : IAsyncDisposable, IDisposable
{
    /// <summary>OCR an image file on disk.</summary>
    Task<OcrResult> ExtractTextFromImage(
        string imagePath,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>OCR an image from a stream (format auto-detected).</summary>
    Task<OcrResult> ExtractTextFromImage(
        Stream imageStream,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>OCR an image from an encoded byte array (PNG/JPEG/etc.).</summary>
    Task<OcrResult> ExtractTextFromImage(
        byte[] imageBytes,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>OCR an image from encoded bytes.</summary>
    Task<OcrResult> ExtractTextFromImage(
        ReadOnlyMemory<byte> imageBytes,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// OCR an already-decoded ImageSharp image. The caller retains ownership of the image
    /// (it is not disposed by this method).
    /// </summary>
    Task<OcrResult> ExtractTextFromImage(
        Image<Rgb24> image,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Detects the dominant script(s) of an image and returns representative language codes.</summary>
    Task<IReadOnlyList<string>> DetectLanguagesAsync(
        Image<Rgb24> image,
        IEnumerable<string>? candidates = null,
        CancellationToken cancellationToken = default);

    /// <summary>Detects the dominant script(s) of an image file and returns representative language codes.</summary>
    Task<IReadOnlyList<string>> DetectLanguagesAsync(
        string imagePath,
        IEnumerable<string>? candidates = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Locates text regions without recognizing them (layout analysis / redaction / field cropping).
    /// Implemented by <see cref="EasyOcrService"/>; a default-implementing stub throws so custom
    /// <see cref="IEasyOcrService"/> implementations and mocks keep compiling unchanged.
    /// </summary>
    Task<IReadOnlyList<DetectedRegion>> DetectRegionsAsync(
        Image<Rgb24> image,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement DetectRegionsAsync. Use {nameof(EasyOcrService)}.");

    /// <summary>Locates text regions in an image file without recognizing them.</summary>
    Task<IReadOnlyList<DetectedRegion>> DetectRegionsAsync(
        string imagePath,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement DetectRegionsAsync. Use {nameof(EasyOcrService)}.");

    /// <summary>
    /// Recognizes text inside caller-supplied region polygons, skipping detection — EasyOCR's
    /// <c>recognize()</c>. Polygons are in the image's pixel coordinates.
    /// </summary>
    Task<OcrResult> RecognizeRegionsAsync(
        Image<Rgb24> image,
        IEnumerable<IReadOnlyList<OcrPoint>> regions,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement RecognizeRegionsAsync. Use {nameof(EasyOcrService)}.");

    /// <summary>Recognizes text inside regions located by a prior detection pass.</summary>
    Task<OcrResult> RecognizeRegionsAsync(
        Image<Rgb24> image,
        IEnumerable<DetectedRegion> regions,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement RecognizeRegionsAsync. Use {nameof(EasyOcrService)}.");

    /// <summary>
    /// Optionally preloads the detector and the recognizer pack(s) for the given languages so the first
    /// real OCR call doesn't pay model-download + ONNX session-initialization latency. A no-op by default
    /// on custom implementations; <see cref="EasyOcrService"/> performs the warm-up.
    /// </summary>
    Task WarmUp(IEnumerable<string> languages, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
