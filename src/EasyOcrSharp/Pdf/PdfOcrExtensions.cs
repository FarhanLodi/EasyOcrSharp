using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf.Internal;
using EasyOcrSharp.Services;

namespace EasyOcrSharp.Pdf;

/// <summary>
/// PDF helpers for <see cref="IEasyOcrService"/>: OCR a scanned PDF page-by-page, or produce a
/// searchable PDF (the original page images with an invisible, selectable OCR text layer).
/// Pages are rasterized with PDFium and processed one at a time to keep memory low.
/// </summary>
public static class PdfOcrExtensions
{
    /// <summary>OCRs every page of a PDF file and returns per-page results.</summary>
    public static Task<PdfOcrResult> ExtractTextFromPdfAsync(
        this IEasyOcrService service,
        string pdfPath,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        var bytes = File.ReadAllBytes(Path.GetFullPath(pdfPath));
        return ExtractTextFromPdfAsync(service, bytes, languages, options, pdfOptions, cancellationToken);
    }

    /// <summary>OCRs every page of an in-memory PDF and returns per-page results.</summary>
    public static async Task<PdfOcrResult> ExtractTextFromPdfAsync(
        this IEasyOcrService service,
        byte[] pdfBytes,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(languages);
        pdfOptions ??= new PdfOcrOptions();
        pdfOptions.Validate();
        var langs = languages as string[] ?? languages.ToArray();

        var pages = new List<PdfPageResult>();
        await PdfRasterizer.ForEachPageAsync(pdfBytes, pdfOptions.Dpi, async (index, count, image) =>
        {
            var ocr = await service.ExtractTextFromImage(image, langs, options, cancellationToken).ConfigureAwait(false);
            pages.Add(new PdfPageResult
            {
                PageNumber = index + 1,
                Ocr = ocr,
                PixelWidth = image.Width,
                PixelHeight = image.Height,
            });
            pdfOptions.Progress?.Report(new PdfPageProgress(index + 1, count));
        }, cancellationToken).ConfigureAwait(false);

        return new PdfOcrResult { Pages = pages };
    }

    /// <summary>
    /// OCRs a PDF and writes a searchable PDF (page images + invisible selectable text) to
    /// <paramref name="outputPdfPath"/>. Returns the per-page OCR results.
    /// </summary>
    public static async Task<PdfOcrResult> CreateSearchablePdfAsync(
        this IEasyOcrService service,
        string inputPdfPath,
        string outputPdfPath,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPdfPath);
        var bytes = File.ReadAllBytes(Path.GetFullPath(inputPdfPath));

        var (result, pdf) = await CreateSearchablePdfAsync(service, bytes, languages, options, pdfOptions, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.GetFullPath(outputPdfPath), pdf, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// OCRs an in-memory PDF and returns both the per-page results and the searchable PDF bytes.
    /// </summary>
    public static async Task<(PdfOcrResult Result, byte[] Pdf)> CreateSearchablePdfAsync(
        this IEasyOcrService service,
        byte[] pdfBytes,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(languages);
        pdfOptions ??= new PdfOcrOptions();
        pdfOptions.Validate();
        var langs = languages as string[] ?? languages.ToArray();

        var builder = new SearchablePdfBuilder();
        var pages = new List<PdfPageResult>();

        await PdfRasterizer.ForEachPageAsync(pdfBytes, pdfOptions.Dpi, async (index, count, image) =>
        {
            var ocr = await service.ExtractTextFromImage(image, langs, options, cancellationToken).ConfigureAwait(false);
            builder.AddPage(image, ocr, pdfOptions.Dpi, pdfOptions.JpegQuality);
            pages.Add(new PdfPageResult
            {
                PageNumber = index + 1,
                Ocr = ocr,
                PixelWidth = image.Width,
                PixelHeight = image.Height,
            });
            pdfOptions.Progress?.Report(new PdfPageProgress(index + 1, count));
        }, cancellationToken).ConfigureAwait(false);

        return (new PdfOcrResult { Pages = pages }, builder.Build());
    }
}
