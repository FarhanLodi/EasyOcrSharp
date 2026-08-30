using System.Diagnostics;
using EasyOcrSharp.Diagnostics;
using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf.Internal;
using EasyOcrSharp.Services;

namespace EasyOcrSharp.Pdf;

/// <summary>
/// PDF helpers for <see cref="IEasyOcrService"/>: OCR a scanned PDF page-by-page, or produce a
/// searchable PDF (the original page images with an invisible, selectable OCR text layer).
/// Pages are rasterized with PDFium and processed one at a time to keep memory low.
/// </summary>
/// <remarks>
/// Each entry point emits one <c>easyocr.operations</c> / <c>easyocr.duration</c> data point for the whole
/// document, plus <c>easyocr.pages</c> and <c>easyocr.lines</c>, tagged with the operation name and the
/// outcome — see <see cref="EasyOcrDiagnostics"/>. Until this existed a PDF-only deployment produced no
/// document-level counter at all and was invisible on a dashboard. The per-page recognition these methods
/// drive is still measured separately by the single-image API, so both the document rate and the page rate
/// can be watched.
/// <para>
/// They deliberately take <b>no</b> concurrency slot of their own: every page goes through the public
/// single-image API, which is already gated, so a second slot held across the whole document would
/// deadlock a service configured to allow one operation at a time — against itself, on page one.
/// </para>
/// </remarks>
public static class PdfOcrExtensions
{

    /// <summary>OCRs every page of a PDF file and returns per-page results.</summary>
    public static async Task<PdfOcrResult> ExtractTextFromPdfAsync(
        this IEasyOcrService service,
        string pdfPath,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        var bytes = await File.ReadAllBytesAsync(Path.GetFullPath(pdfPath), cancellationToken).ConfigureAwait(false);
        return await ExtractTextFromPdfAsync(service, bytes, languages, options, pdfOptions, cancellationToken).ConfigureAwait(false);
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

        // The activity is declared first so it outlives the recorder: the recorder writes its tags from its
        // own Dispose, and tags set on an already-stopped activity are dropped by every exporter.
        using var activity = EasyOcrDiagnostics.ActivitySource.StartActivity("EasyOcr.Pdf", ActivityKind.Internal);
        using var recorder = EasyOcrDiagnostics.Begin(EasyOcrDiagnostics.OperationNames.Pdf, ProviderOf(service))
            .WithLanguages(langs)
            .Annotate(activity);

        try
        {
            var pages = new List<PdfPageResult>();
            await PdfRasterizer.ForEachPageAsync(pdfBytes, pdfOptions.Dpi, pdfOptions.MaxPages, pdfOptions.MaxPagePixels, async (index, count, image) =>
            {
                var ocr = await service.ExtractTextFromImage(image, langs, options, cancellationToken).ConfigureAwait(false);
                pages.Add(new PdfPageResult
                {
                    PageNumber = index + 1,
                    Ocr = ocr,
                    PixelWidth = image.Width,
                    PixelHeight = image.Height,
                });
                // Counted per completed page, never from the document page count up front: a run that dies
                // on page 57 must report the 56 pages it actually OCRed. Counting ahead would credit a
                // failed job with a whole document of work, so the pages/sec panel would read highest
                // exactly when the service is broken.
                recorder.AddPages(1).AddLines(ocr.Lines.Count);
                pdfOptions.Progress?.Report(new PdfPageProgress(index + 1, count));
            }, cancellationToken).ConfigureAwait(false);

            recorder.Success();
            return new PdfOcrResult { Pages = pages };
        }
        catch (Exception ex)
        {
            recorder.Failure(ex);
            throw;
        }
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
        var bytes = await File.ReadAllBytesAsync(Path.GetFullPath(inputPdfPath), cancellationToken).ConfigureAwait(false);

        var (result, pdf) = await CreateSearchablePdfAsync(service, bytes, languages, options, pdfOptions, cancellationToken).ConfigureAwait(false);
        // Write via a temp file and rename. WriteAllBytesAsync truncates the destination first, so a
        // cancellation or a full disk part-way through would leave a truncated, unopenable PDF at the user's
        // path -- destroying any previous good version that was there.
        var finalPath = Path.GetFullPath(outputPdfPath);
        var tempPath = finalPath + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, pdf, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch (IOException) { /* best effort */ }
            throw;
        }
        return result;
    }

    /// <summary>
    /// OCRs an in-memory PDF and returns both the per-page results and the searchable PDF bytes.
    /// </summary>
    /// <remarks>
    /// The invisible text layer uses the base-14 Helvetica font while the recognized text is Latin-1, and
    /// switches to an embedded <c>/Identity-H</c> composite font when it is not, so non-Latin scripts stay
    /// searchable. See <see cref="PdfOcrOptions.TextLayerFont"/> and
    /// <see cref="PdfOcrOptions.TextLayerFontPath"/> for control over that, and
    /// <see cref="PdfOcrResult.TextLayerFontStatus"/> for what actually happened.
    /// </remarks>
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

        using var activity = EasyOcrDiagnostics.ActivitySource.StartActivity("EasyOcr.PdfSearchable", ActivityKind.Internal);
        using var recorder = EasyOcrDiagnostics.Begin(EasyOcrDiagnostics.OperationNames.PdfSearchable, ProviderOf(service))
            .WithLanguages(langs)
            .Annotate(activity);

        try
        {
            var builder = new SearchablePdfBuilder(pdfOptions);
            var pages = new List<PdfPageResult>();

            await PdfRasterizer.ForEachPageAsync(pdfBytes, pdfOptions.Dpi, pdfOptions.MaxPages, pdfOptions.MaxPagePixels, async (index, count, image) =>
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
                // Counted only once the page is both recognized and laid into the output document — see the
                // note on the plain-OCR overload above.
                recorder.AddPages(1).AddLines(ocr.Lines.Count);
                pdfOptions.Progress?.Report(new PdfPageProgress(index + 1, count));
            }, cancellationToken).ConfigureAwait(false);

            // Build() picks the text-layer font from the whole document's text, so it has to run first.
            var pdf = builder.Build();
            var result = new PdfOcrResult
            {
                Pages = pages,
                TextLayerFontStatus = builder.TextLayerFontStatus,
                TextLayerFontPath = builder.TextLayerFontPath,
            };

            recorder.Success();
            return (result, pdf);
        }
        catch (Exception ex)
        {
            recorder.Failure(ex);
            throw;
        }
    }

    /// <summary>
    /// The <see cref="EasyOcrDiagnostics.TagNames.Provider"/> value for a composite operation.
    /// <see cref="EasyOcrService"/> keeps the exact resolved provider private and these are extensions on
    /// the interface, so the tag is coarse here: "Cpu" matches the per-image points exactly, a GPU service
    /// reports "Gpu" without claiming to know whether it is CUDA, DirectML or CoreML, and a caller's own
    /// <see cref="IEasyOcrService"/> reports "unknown" rather than a guess that would make a dashboard's
    /// CPU/GPU split quietly wrong. The exact split is always available from the per-page
    /// <c>extract</c> points these methods generate.
    /// </summary>
    private static string ProviderOf(IEasyOcrService service)
        => service is EasyOcrService concrete ? concrete.ProviderName : "unknown";
}
