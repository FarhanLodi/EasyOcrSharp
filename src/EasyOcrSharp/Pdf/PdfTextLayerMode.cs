namespace EasyOcrSharp.Pdf;

/// <summary>
/// Controls whether a PDF's embedded (born-digital) text layer is read instead of OCR-ing the rendered page.
/// Set through <see cref="PdfOcrOptions.TextLayer"/>.
/// </summary>
/// <remarks>
/// <para>
/// A page produced by a word processor, a report generator or a "print to PDF" driver already carries its
/// exact text. Reading it skips detection and recognition entirely — milliseconds per page instead of
/// seconds — and returns the text the author typed rather than a recognition of it. Pages are accepted
/// one at a time, so a document mixing born-digital and scanned pages is read the fast way where it can be
/// and OCR'd where it must be. <see cref="PdfPageResult.Source"/> says which path produced each page.
/// </para>
/// <para>
/// Embedded text cannot be trusted blindly: a PDF whose fonts lack a usable <c>/ToUnicode</c> map
/// extracts as replacement characters, private-use code points or random symbols while looking perfect on
/// screen. Every page therefore has to pass a quality gate before its text is used — at least 20
/// non-whitespace characters, fewer than 5% of them replacement, control or private-use characters, and at
/// least 60% letters or digits. A page that fails is OCR'd as if this option were off.
/// </para>
/// </remarks>
public enum PdfTextLayerMode
{
    /// <summary>
    /// Ignore any embedded text and OCR every page. This is the default and exactly the behaviour of earlier
    /// releases.
    /// </summary>
    Ignore = 0,

    /// <summary>
    /// Use the embedded text layer of every page whose text passes the quality gate; OCR the rest (scans,
    /// and documents with a broken <c>/ToUnicode</c> map).
    /// </summary>
    PreferEmbedded = 1,

    /// <summary>
    /// Like <see cref="PreferEmbedded"/>, but also OCRs a page whose embedded text lines cover less than 1% of
    /// the page area. That catches a scanned page that carries only a small digital stamp, Bates number,
    /// header or footer line — enough characters to pass the quality gate, but nowhere near the page's real
    /// content, which is in the scanned image. The recommended setting for mixed or unknown input.
    /// </summary>
    Auto = 2,
}
