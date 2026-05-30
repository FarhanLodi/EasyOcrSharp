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
}
