namespace EasyOcrSharp.Pdf;

/// <summary>Where the text of a <see cref="PdfPageResult"/> came from.</summary>
public enum PdfPageSource
{
    /// <summary>The page was rendered and recognized by the OCR engine — the default, and the only source in earlier releases.</summary>
    Ocr = 0,

    /// <summary>
    /// The page's embedded text layer was read directly (see <see cref="PdfOcrOptions.TextLayer"/>). No model
    /// ran: every line carries a confidence of 1.0, and its coordinates are in the same rendered-page pixel
    /// space the OCR path reports at <see cref="PdfPageResult.Dpi"/>.
    /// </summary>
    EmbeddedText = 1,
}
