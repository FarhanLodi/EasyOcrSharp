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

    // ---- streaming recognition ----

    /// <summary>
    /// OCRs an already-decoded image and yields each <see cref="OcrLine"/> <b>as soon as its region has
    /// been recognized</b>, instead of buffering the whole page — the responsive alternative to
    /// <see cref="ExtractTextFromImage(Image{Rgb24}, IEnumerable{string}, RecognitionOptions?, CancellationToken)"/>
    /// for large scans and progressive UIs. Detection still runs once up front (it is a single whole-image
    /// model pass); only recognition is streamed. Lines arrive in <b>reading order</b>, not completion
    /// order, so the sequence reads like the page. The caller retains ownership of the image, which must
    /// stay alive and unmodified until the enumeration completes.
    /// Implemented by <see cref="EasyOcrService"/>; a default-implementing stub throws so custom
    /// <see cref="IEasyOcrService"/> implementations and mocks keep compiling unchanged.
    /// </summary>
    /// <remarks>
    /// Two option combinations cannot be delivered incrementally and are handled as follows:
    /// <list type="bullet">
    /// <item><description><see cref="TextGrouping.Paragraph"/> merges lines across the whole page, so every
    /// line is recognized before the first paragraph can be emitted. Recognition still runs concurrently
    /// and the results are correct — they simply all arrive at the end.</description></item>
    /// <item><description><see cref="PreprocessingOptions.DetectOrientation"/> scores four whole-page OCR
    /// passes against each other, so the buffered pipeline runs and its lines are replayed at the end.</description></item>
    /// </list>
    /// <see cref="TextGrouping.Word"/> and <see cref="TextGrouping.Line"/> stream line by line: box merging
    /// happens during detection, so a streamed <see cref="TextGrouping.Line"/> result is genuinely
    /// line-grouped, never raw word boxes.
    /// </remarks>
    IAsyncEnumerable<OcrLine> ExtractTextStreamAsync(
        Image<Rgb24> image,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement ExtractTextStreamAsync. Use {nameof(EasyOcrService)}.");

    /// <summary>
    /// OCRs an image file on disk, yielding each <see cref="OcrLine"/> in reading order as its region is
    /// recognized. See
    /// <see cref="ExtractTextStreamAsync(Image{Rgb24}, IEnumerable{string}, RecognitionOptions?, CancellationToken)"/>
    /// for the ordering and grouping guarantees.
    /// </summary>
    IAsyncEnumerable<OcrLine> ExtractTextStreamAsync(
        string imagePath,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement ExtractTextStreamAsync. Use {nameof(EasyOcrService)}.");

    /// <summary>
    /// OCRs an image read from a stream (format auto-detected), yielding each <see cref="OcrLine"/> in
    /// reading order as its region is recognized. The stream is fully read before the first line is
    /// produced. See
    /// <see cref="ExtractTextStreamAsync(Image{Rgb24}, IEnumerable{string}, RecognitionOptions?, CancellationToken)"/>
    /// for the ordering and grouping guarantees.
    /// </summary>
    IAsyncEnumerable<OcrLine> ExtractTextStreamAsync(
        Stream imageStream,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement ExtractTextStreamAsync. Use {nameof(EasyOcrService)}.");

    /// <summary>
    /// OCRs an image from an encoded byte array (PNG/JPEG/etc.), yielding each <see cref="OcrLine"/> in
    /// reading order as its region is recognized. See
    /// <see cref="ExtractTextStreamAsync(Image{Rgb24}, IEnumerable{string}, RecognitionOptions?, CancellationToken)"/>
    /// for the ordering and grouping guarantees.
    /// </summary>
    IAsyncEnumerable<OcrLine> ExtractTextStreamAsync(
        byte[] imageBytes,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement ExtractTextStreamAsync. Use {nameof(EasyOcrService)}.");

    /// <summary>
    /// Optionally preloads the detector and the recognizer pack(s) for the given languages so the first
    /// real OCR call doesn't pay model-download + ONNX session-initialization latency. A no-op by default
    /// on custom implementations; <see cref="EasyOcrService"/> performs the warm-up.
    /// </summary>
    Task WarmUp(IEnumerable<string> languages, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    // ---- document-structure analysis (layout, tables, formulas, seals) ----

    /// <summary>
    /// Analyzes the full structure of a document image file — layout regions, <b>tables</b> (recovered
    /// as HTML), formulas, seals, and reading order — using PaddleOCR's PP-StructureV3 models (via the
    /// PaddleOcrNet engine). The returned <see cref="PaddleOcrNet.Structure.StructureResult"/> exposes
    /// typed blocks plus <c>ToMarkdown()</c> / <c>ToJson()</c>. Implemented by
    /// <see cref="EasyOcrService"/>; a default-implementing stub throws so custom
    /// <see cref="IEasyOcrService"/> implementations and mocks keep compiling unchanged.
    /// </summary>
    Task<PaddleOcrNet.Structure.StructureResult> AnalyzeDocumentAsync(
        string imagePath,
        DocumentAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement AnalyzeDocumentAsync. Use {nameof(EasyOcrService)}.");

    /// <summary>Analyzes the structure of a document image from a stream (format auto-detected).</summary>
    Task<PaddleOcrNet.Structure.StructureResult> AnalyzeDocumentAsync(
        Stream imageStream,
        DocumentAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement AnalyzeDocumentAsync. Use {nameof(EasyOcrService)}.");

    /// <summary>Analyzes the structure of a document image from an encoded byte array (PNG/JPEG/etc.).</summary>
    Task<PaddleOcrNet.Structure.StructureResult> AnalyzeDocumentAsync(
        byte[] imageBytes,
        DocumentAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement AnalyzeDocumentAsync. Use {nameof(EasyOcrService)}.");

    /// <summary>Analyzes the structure of a document image from encoded bytes.</summary>
    Task<PaddleOcrNet.Structure.StructureResult> AnalyzeDocumentAsync(
        ReadOnlyMemory<byte> imageBytes,
        DocumentAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement AnalyzeDocumentAsync. Use {nameof(EasyOcrService)}.");

    /// <summary>
    /// Analyzes the structure of an already-decoded document image. The caller retains ownership of
    /// the image (it is not disposed by this method).
    /// </summary>
    Task<PaddleOcrNet.Structure.StructureResult> AnalyzeDocumentAsync(
        Image<Rgb24> image,
        DocumentAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement AnalyzeDocumentAsync. Use {nameof(EasyOcrService)}.");
}
