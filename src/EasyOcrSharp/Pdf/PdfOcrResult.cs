using EasyOcrSharp.Models;

namespace EasyOcrSharp.Pdf;

/// <summary>OCR result for a single rendered PDF page.</summary>
public sealed record PdfPageResult
{
    /// <summary>1-based page number.</summary>
    public required int PageNumber { get; init; }

    /// <summary>The recognized text and lines for this page.</summary>
    public required OcrResult Ocr { get; init; }

    /// <summary>Rendered page width in pixels (at the configured DPI).</summary>
    public int PixelWidth { get; init; }

    /// <summary>Rendered page height in pixels (at the configured DPI).</summary>
    public int PixelHeight { get; init; }
}

/// <summary>
/// Which font the invisible text layer of a generated searchable PDF ended up using.
/// </summary>
public enum PdfTextLayerFontStatus
{
    /// <summary>
    /// The standard base-14 Helvetica font with <c>WinAnsiEncoding</c> — either because every recognized
    /// character is Latin-1 (the common case, and the only behaviour before embedding existed) or because
    /// <see cref="PdfTextLayerFontMode.Never"/> was requested. Also the value on results that did not
    /// produce a PDF at all.
    /// </summary>
    Standard = 0,

    /// <summary>
    /// A subsetted TrueType font was embedded as an <c>/Identity-H</c> composite font with a
    /// <c>/ToUnicode</c> map, so non-Latin text is selectable, searchable and copy-pasteable.
    /// <see cref="PdfOcrResult.TextLayerFontPath"/> names the font that was used.
    /// </summary>
    Embedded = 1,

    /// <summary>
    /// The text needed an embedded font but none could be resolved, so the standard Helvetica layer was
    /// written instead and every character outside Latin-1 became <c>?</c> in it. The page images and the
    /// OCR results are unaffected — only the hidden text layer is degraded. Set
    /// <see cref="PdfOcrOptions.TextLayerFontPath"/> to a <c>.ttf</c>/<c>.ttc</c> file to fix it.
    /// </summary>
    Unavailable = 2,
}

/// <summary>Aggregate OCR result for a whole PDF document.</summary>
public sealed record PdfOcrResult
{
    /// <summary>Per-page results in document order.</summary>
    public required IReadOnlyList<PdfPageResult> Pages { get; init; }

    /// <summary>All pages' text concatenated, separated by blank lines.</summary>
    public string FullText => string.Join("\n\n", Pages.Select(p => p.Ocr.FullText));

    /// <summary>
    /// Gets how the invisible text layer of the generated searchable PDF was encoded. Always
    /// <see cref="PdfTextLayerFontStatus.Standard"/> for plain text extraction, which produces no PDF.
    /// Check for <see cref="PdfTextLayerFontStatus.Unavailable"/> to detect a document whose non-Latin
    /// text did not make it into the searchable layer.
    /// </summary>
    public PdfTextLayerFontStatus TextLayerFontStatus { get; init; }

    /// <summary>
    /// Gets the font file embedded in the text layer, or <see langword="null"/> when no font was embedded.
    /// </summary>
    public string? TextLayerFontPath { get; init; }
}
