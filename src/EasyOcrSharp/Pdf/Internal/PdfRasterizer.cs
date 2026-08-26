using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Exceptions;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Pdf.Internal;

/// <summary>
/// Rasterizes PDF pages to images via PDFium (Docnet.Core). Pages are rendered and handed off one at
/// a time so peak memory stays at roughly a single page regardless of document length.
/// </summary>
internal static class PdfRasterizer
{
    /// <summary>
    /// Serializes every call into PDFium. Docnet exposes it through the process-wide
    /// <see cref="DocLib.Instance"/> singleton, and PDFium itself is <b>not</b> thread-safe: two
    /// threads rasterizing at once can return a corrupted page or tear down the native library. Two
    /// concurrent OCR calls on different PDFs is an entirely ordinary thing for a caller to do (a
    /// batch, or a web request per document), so the guard belongs here rather than in the caller.
    /// </summary>
    /// <remarks>
    /// The gate is held only across the native calls. The OCR handler — by far the expensive part —
    /// runs outside it, so documents still overlap and throughput is barely affected.
    /// </remarks>
    private static readonly Lock PdfiumGate = new();

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
        int maxPages,
        long maxPagePixels,
        Func<int, int, Image<Rgb24>, Task> handler,
        CancellationToken cancellationToken)
    {
        if (pdfBytes is null || pdfBytes.Length == 0)
            throw new PdfProcessingException("The PDF input is empty. Provide the bytes of a valid PDF document.");

        // PDF user space is 72 dpi; scale up to the requested rendering resolution.
        double scale = dpi / 72.0;

        // DocLib.Instance is a process-wide singleton — never dispose it here. Opening can fail on a
        // corrupt, truncated, non-PDF, or encrypted document; surface those as a typed, clear error
        // instead of leaking a PDFium/Docnet exception to the caller.
        IDocReader docReader;
        try
        {
            lock (PdfiumGate)
            {
                docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(scale));
            }
        }
        catch (Exception ex) when (ex is DocnetException or ArgumentException)
        {
            throw new PdfProcessingException(
                "The PDF could not be opened. It may be corrupt, not a PDF, or password-protected/encrypted.", ex);
        }

        try
        {
            int count;
            try
            {
                lock (PdfiumGate)
                {
                    count = docReader.GetPageCount();
                }
            }
            catch (DocnetException ex)
            {
                throw new PdfProcessingException("The PDF page count could not be read; the document may be corrupt.", ex);
            }

            if (maxPages > 0 && count > maxPages)
            {
                throw new PdfProcessingException(
                    $"The PDF has {count} pages, exceeding the limit of {maxPages} (PdfOcrOptions.MaxPages). " +
                    "Raise the limit to process it, or split the document.");
            }

            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Render this page. Any failure here is a document/PDFium problem (kept separate from the
                // handler call below, so genuine OCR errors are never mislabeled as a PDF-rendering error).
                Image<Rgb24> image;
                try
                {
                    byte[] bgra;
                    int width, height;

                    // Only the native work is serialized; the BGRA->RGB conversion below is pure
                    // managed code and stays outside the gate to keep the critical section short.
                    lock (PdfiumGate)
                    {
                        using var pageReader = docReader.GetPageReader(i);
                        width = pageReader.GetPageWidth();
                        height = pageReader.GetPageHeight();

                        if (maxPagePixels > 0 && (long)width * height > maxPagePixels)
                        {
                            throw new PdfProcessingException(
                                $"Page {i + 1} renders to {width}x{height} ({(long)width * height:N0} px) at {dpi} DPI, " +
                                $"exceeding the per-page limit of {maxPagePixels:N0} px (PdfOcrOptions.MaxPageMegapixels). " +
                                "Lower the DPI or raise the limit.");
                        }

                        // PDFium emits BGRA with a transparent background; flatten onto white so OCR sees a clean page.
                        bgra = pageReader.GetImage(new NaiveTransparencyRemover());
                    }

                    image = ConvertToRgb24(bgra, width, height);
                }
                catch (Exception ex) when (ex is DocnetException or ArgumentException or InvalidOperationException)
                {
                    throw new PdfProcessingException($"Page {i + 1} of the PDF could not be rendered; the document may be corrupt.", ex);
                }

                using (image)
                {
                    await handler(i, count, image).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Tearing the document down is a native call too, so it takes the gate like the rest.
            lock (PdfiumGate)
            {
                docReader.Dispose();
            }
        }
    }

    private static Image<Rgb24> ConvertToRgb24(byte[] bgra, int width, int height)
    {
        using var bgraImage = Image.LoadPixelData<Bgra32>(bgra, width, height);
        return bgraImage.CloneAs<Rgb24>();
    }
}
