using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Engine.Services;

/// <summary>
/// Abstraction over <see cref="StructureService"/>, kept for testability. Not registered in DI:
/// <see cref="EasyOcrSharp.Services.EasyOcrService"/> owns the single instance and creates it lazily on
/// the first <c>AnalyzeDocumentAsync</c> call.
/// </summary>
internal interface IStructureService : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// OCR an image file on disk across the given <see cref="OcrLanguage"/> values.
    /// </summary>
    Task<OcrResult> ExtractTextFromImage(
        string imagePath,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// OCR an image from a stream (format auto-detected) across the given <see cref="OcrLanguage"/> values.
    /// </summary>
    Task<OcrResult> ExtractTextFromImage(
        Stream imageStream,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// OCR an image from an encoded byte array (PNG/JPEG/etc.) across the given <see cref="OcrLanguage"/> values.
    /// </summary>
    Task<OcrResult> ExtractTextFromImage(
        byte[] imageBytes,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// OCR an image from encoded bytes across the given <see cref="OcrLanguage"/> values.
    /// </summary>
    Task<OcrResult> ExtractTextFromImage(
        ReadOnlyMemory<byte> imageBytes,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// OCR an already-decoded ImageSharp image across the given <see cref="OcrLanguage"/> values — the
    /// in-memory entry point. The caller retains ownership of the image (it is not disposed).
    /// </summary>
    Task<OcrResult> ExtractTextFromImage(
        Image<Rgb24> image,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// OCR an image file on disk in a single <see cref="OcrLanguage"/> (defaults to <see cref="OcrLanguage.Auto"/>).
    /// </summary>
    Task<OcrResult> ExtractTextFromImage(
        string imagePath,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => ExtractTextFromImage(imagePath, new[] { language }, options, cancellationToken);

    /// <summary>
    /// OCR an image from a stream in a single <see cref="OcrLanguage"/> (defaults to <see cref="OcrLanguage.Auto"/>).
    /// </summary>
    Task<OcrResult> ExtractTextFromImage(
        Stream imageStream,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => ExtractTextFromImage(imageStream, new[] { language }, options, cancellationToken);

    /// <summary>
    /// OCR an image from an encoded byte array in a single <see cref="OcrLanguage"/> (defaults to <see cref="OcrLanguage.Auto"/>).
    /// </summary>
    Task<OcrResult> ExtractTextFromImage(
        byte[] imageBytes,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => ExtractTextFromImage(imageBytes, new[] { language }, options, cancellationToken);

    /// <summary>
    /// OCR an image from encoded bytes in a single <see cref="OcrLanguage"/> (defaults to <see cref="OcrLanguage.Auto"/>).
    /// </summary>
    Task<OcrResult> ExtractTextFromImage(
        ReadOnlyMemory<byte> imageBytes,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => ExtractTextFromImage(imageBytes, new[] { language }, options, cancellationToken);

    /// <summary>
    /// OCR an already-decoded image in a single <see cref="OcrLanguage"/> (defaults to <see cref="OcrLanguage.Auto"/>).
    /// </summary>
    Task<OcrResult> ExtractTextFromImage(
        Image<Rgb24> image,
        OcrLanguage language = OcrLanguage.Auto,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => ExtractTextFromImage(image, new[] { language }, options, cancellationToken);

    /// <summary>
    /// Locates text regions without recognizing them (layout analysis / redaction / field cropping).
    /// Implemented by <see cref="StructureService"/>; a default-implementing stub throws so custom
    /// <see cref="IStructureService"/> implementations and mocks keep compiling unchanged.
    /// </summary>
    Task<IReadOnlyList<DetectedRegion>> DetectRegionsAsync(
        Image<Rgb24> image,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement DetectRegionsAsync. Use {nameof(StructureService)}.");

    /// <summary>
    /// Locates text regions in an image file without recognizing them.
    /// </summary>
    Task<IReadOnlyList<DetectedRegion>> DetectRegionsAsync(
        string imagePath,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement DetectRegionsAsync. Use {nameof(StructureService)}.");

    /// <summary>
    /// Recognizes text inside caller-supplied region polygons, skipping detection. Polygons are in the
    /// image's pixel coordinates.
    /// </summary>
    Task<OcrResult> RecognizeRegionsAsync(
        Image<Rgb24> image,
        IEnumerable<IReadOnlyList<OcrPoint>> regions,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement RecognizeRegionsAsync. Use {nameof(StructureService)}.");

    /// <summary>
    /// Recognizes text inside regions located by a prior detection pass.
    /// </summary>
    Task<OcrResult> RecognizeRegionsAsync(
        Image<Rgb24> image,
        IEnumerable<DetectedRegion> regions,
        IReadOnlyList<OcrLanguage> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement RecognizeRegionsAsync. Use {nameof(StructureService)}.");

    /// <summary>
    /// Optionally preloads the detector, the (optional) text-line classifier, and the recognizer pack(s)
    /// for the given languages so the first real OCR call doesn't pay model-download + ONNX session
    /// initialization latency. A no-op by default on custom implementations; <see cref="StructureService"/>
    /// performs the warm-up.
    /// </summary>
    Task WarmUp(IReadOnlyList<OcrLanguage> languages, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    // ---- document-structure analysis (PP-StructureV3) ----

    /// <summary>
    /// Analyzes the full structure of a document image file (layout regions, tables, formulas, seals,
    /// reading order). Implemented by <see cref="StructureService"/>; a default-implementing stub throws so
    /// custom <see cref="IStructureService"/> implementations and mocks keep compiling unchanged.
    /// </summary>
    Task<StructureResult> AnalyzeDocumentAsync(
        string imagePath,
        StructureOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement AnalyzeDocumentAsync. Use {nameof(StructureService)}.");

    /// <summary>
    /// Analyzes the structure of a document image from a stream (format auto-detected).
    /// </summary>
    Task<StructureResult> AnalyzeDocumentAsync(
        Stream imageStream,
        StructureOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement AnalyzeDocumentAsync. Use {nameof(StructureService)}.");

    /// <summary>
    /// Analyzes the structure of a document image from an encoded byte array (PNG/JPEG/etc.).
    /// </summary>
    Task<StructureResult> AnalyzeDocumentAsync(
        byte[] imageBytes,
        StructureOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement AnalyzeDocumentAsync. Use {nameof(StructureService)}.");

    /// <summary>
    /// Analyzes the structure of a document image from encoded bytes.
    /// </summary>
    Task<StructureResult> AnalyzeDocumentAsync(
        ReadOnlyMemory<byte> imageBytes,
        StructureOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement AnalyzeDocumentAsync. Use {nameof(StructureService)}.");

    /// <summary>
    /// Analyzes the structure of an already-decoded document image. The caller retains ownership of the
    /// image (it is not disposed by this method).
    /// </summary>
    Task<StructureResult> AnalyzeDocumentAsync(
        Image<Rgb24> image,
        StructureOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement AnalyzeDocumentAsync. Use {nameof(StructureService)}.");
}
