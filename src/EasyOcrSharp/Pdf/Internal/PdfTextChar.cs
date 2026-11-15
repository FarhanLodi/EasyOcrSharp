namespace EasyOcrSharp.Pdf.Internal;

/// <summary>
/// One character of a PDF page's embedded text layer, in rendered-page pixel coordinates (top-left origin).
/// Decoupled from Docnet's <c>Character</c> type so the line-building logic can be unit-tested without PDFium.
/// </summary>
/// <param name="Value">The UTF-16 code unit PDFium reported for the character.</param>
/// <param name="Left">Left edge in pixels.</param>
/// <param name="Top">Top edge in pixels.</param>
/// <param name="Right">Right edge in pixels.</param>
/// <param name="Bottom">Bottom edge in pixels.</param>
/// <param name="FontSizePx">The font size converted to pixels, or 0 when PDFium did not report one.</param>
internal readonly record struct PdfTextChar(char Value, double Left, double Top, double Right, double Bottom, double FontSizePx)
{
    /// <summary>
    /// The height that scales the word and column gap thresholds: the font size, or the glyph box height when
    /// that is larger.
    /// </summary>
    /// <remarks>
    /// PDFium reports the <c>Tf</c> operand, not the size the text is drawn at. A generator that sets
    /// <c>/F1 1 Tf</c> and scales the text matrix to 12 instead reports 1 pt for 12 pt text; trusting it would
    /// shrink every threshold twelvefold and split each word at its letter gaps. The glyph box is always drawn
    /// at the real size, so the larger of the two is never that wrong — and for a correctly reported size it is
    /// the font size, since a glyph box (ascender to descender at most) does not exceed the em.
    /// </remarks>
    public double EmHeight => Math.Max(Math.Max(1.0, Bottom - Top), FontSizePx);

    /// <summary>Horizontal centre in pixels.</summary>
    public double CenterX => (Left + Right) / 2.0;

    /// <summary>Vertical centre in pixels.</summary>
    public double CenterY => (Top + Bottom) / 2.0;
}
