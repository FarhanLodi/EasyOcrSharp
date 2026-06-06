namespace EasyOcrSharp.Pdf;

/// <summary>Progress for PDF processing, reported per page via <see cref="PdfOcrOptions.Progress"/>.</summary>
/// <param name="PageNumber">1-based page being processed.</param>
/// <param name="PageCount">Total pages in the document.</param>
public readonly record struct PdfPageProgress(int PageNumber, int PageCount)
{
    /// <summary>Completion fraction (0–1).</summary>
    public double Fraction => PageCount > 0 ? (double)PageNumber / PageCount : 0;
}

/// <summary>Options for rasterizing and OCR-ing PDFs.</summary>
public sealed class PdfOcrOptions
{
    /// <summary>
    /// Rendering resolution. Higher = better OCR accuracy but slower and larger searchable PDFs.
    /// 200–300 is a good range for scanned documents. Default 200.
    /// </summary>
    public int Dpi { get; set; } = 200;

    /// <summary>
    /// JPEG quality (1–100) for the page images embedded in a <i>searchable</i> PDF. Lower = smaller
    /// file. Default 75. Ignored when only extracting text.
    /// </summary>
    public int JpegQuality { get; set; } = 75;

    /// <summary>Optional per-page progress callback.</summary>
    public IProgress<PdfPageProgress>? Progress { get; set; }

    internal void Validate()
    {
        if (Dpi is < 36 or > 1200)
            throw new ArgumentOutOfRangeException(nameof(Dpi), Dpi, "Dpi must be between 36 and 1200.");
        if (JpegQuality is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(JpegQuality), JpegQuality, "JpegQuality must be between 1 and 100.");
    }
}
