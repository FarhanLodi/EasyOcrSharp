namespace EasyOcrSharp.Models;

/// <summary>
/// Which detection + recognition model family runs a plain OCR call
/// (<c>ExtractTextFromImage</c>, <c>ExtractTextStreamAsync</c>, <c>RecognizeRegionsAsync</c>,
/// <c>DetectRegionsAsync</c>). Selected per call through <see cref="RecognitionOptions.Engine"/>.
/// </summary>
/// <remarks>
/// Both engines return the same <see cref="OcrResult"/> / <see cref="OcrLine"/> shapes and honor the same
/// service configuration (execution provider, thread limits, model cache, download resilience,
/// <c>MaxImagePixels</c>, concurrency governor, metrics and tracing). They differ in the models — and
/// therefore in which <see cref="RecognitionOptions"/> members apply; see
/// <see cref="RecognitionOptions.Engine"/> for the per-option support matrix.
/// </remarks>
public enum RecognitionEngine
{
    /// <summary>
    /// EasyOCR's pipeline — the CRAFT text detector plus EasyOCR's per-script CRNN recognizers (the
    /// default, and the behaviour of every release before this option existed). Supports all of
    /// EasyOCR's languages and every <see cref="RecognitionOptions"/> member.
    /// </summary>
    EasyOcr = 0,

    /// <summary>
    /// PaddleOCR's PP-OCRv5 pipeline — the DB text-line detector, the PP-LCNet text-line orientation
    /// classifier and the SVTR recognizers with per-script language packs. The same models
    /// <see cref="Services.EasyOcrService.AnalyzeDocumentAsync(string, DocumentAnalysisOptions?, System.Threading.CancellationToken)"/>
    /// runs; a service shares one set of those ONNX sessions between document analysis and plain OCR.
    /// Models are downloaded on first use. Supports EasyOCR's language codes except Bengali-script
    /// (<c>bn</c>, <c>as</c>, <c>mni</c>) and Kannada (<c>kn</c>), which have no PP-OCRv5 pack, and Vietnamese
    /// (<c>vi</c>), whose tone-marked vowels the PP-OCRv5 Latin dictionary lacks; it adds
    /// Greek (<c>el</c>); an unsupported code throws <see cref="System.ArgumentException"/> naming the
    /// supported ones. Some EasyOCR-specific options are ignored — see <see cref="RecognitionOptions.Engine"/>.
    /// </summary>
    PpOcrV5 = 1,
}
