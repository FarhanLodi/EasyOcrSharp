using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Exceptions;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace EasyOcrSharp.Pdf.Internal;

/// <summary>
/// Rasterizes PDF pages to images via PDFium (Docnet.Core). Pages are rendered and handed off one at
/// a time so peak memory stays at roughly a single page regardless of document length.
/// </summary>
internal static class PdfRasterizer
{
    /// <summary>
    /// Renders each page at <paramref name="dpi"/> and invokes <paramref name="handler"/> with the
    /// 0-based index, total page count, and the rendered image (disposed automatically after the
    /// handler completes — do not keep a reference to it).
    /// </summary>
    /// <exception cref="EasyOcrSharpException">
    /// The input is empty, or the PDF cannot be opened/rendered (corrupt, not a PDF, or
    /// password-protected/encrypted). Exceptions thrown by <paramref name="handler"/> itself
    /// (e.g. OCR failures) are propagated unchanged.
    /// </exception>
    public static async Task ForEachPageAsync(
        byte[] pdfBytes,
        int dpi,
        Func<int, int, Image<Rgb24>, Task> handler,
        CancellationToken cancellationToken)
    {
        if (pdfBytes is null || pdfBytes.Length == 0)
            throw new EasyOcrSharpException("The PDF input is empty. Provide the bytes of a valid PDF document.");

        // PDF user space is 72 dpi; scale up to the requested rendering resolution.
        double scale = dpi / 72.0;

        // DocLib.Instance is a process-wide singleton — never dispose it here. Opening can fail on a
        // corrupt, truncated, non-PDF, or encrypted document; surface those as a typed, clear error
        // instead of leaking a PDFium/Docnet exception to the caller.
        IDocReader docReader;
        try
        {
            docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(scale));
        }
        catch (Exception ex) when (ex is DocnetException or ArgumentException)
        {
            throw new EasyOcrSharpException(
                "The PDF could not be opened. It may be corrupt, not a PDF, or password-protected/encrypted.", ex);
        }

        using (docReader)
        {
            int count;
            try
            {
                count = docReader.GetPageCount();
            }
            catch (DocnetException ex)
            {
                throw new EasyOcrSharpException("The PDF page count could not be read; the document may be corrupt.", ex);
            }

            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Render this page. Any failure here is a document/PDFium problem (kept separate from the
                // handler call below, so genuine OCR errors are never mislabeled as a PDF-rendering error).
                Image<Rgb24> image;
                try
                {
                    using var pageReader = docReader.GetPageReader(i);
                    int width = pageReader.GetPageWidth();
                    int height = pageReader.GetPageHeight();

                    // PDFium emits BGRA with a transparent background; flatten onto white so OCR sees a clean page.
                    byte[] bgra = pageReader.GetImage(new NaiveTransparencyRemover());
                    image = ConvertToRgb24(bgra, width, height);
                }
                catch (Exception ex) when (ex is DocnetException or ArgumentException or InvalidOperationException)
                {
                    throw new EasyOcrSharpException($"Page {i + 1} of the PDF could not be rendered; the document may be corrupt.", ex);
                }

                using (image)
                {
                    await handler(i, count, image).ConfigureAwait(false);
                }
            }
        }
    }

    private static Image<Rgb24> ConvertToRgb24(byte[] bgra, int width, int height)
    {
        using var bgraImage = Image.LoadPixelData<Bgra32>(bgra, width, height);
        return bgraImage.CloneAs<Rgb24>();
    }
}
