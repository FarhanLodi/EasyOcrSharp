using System.Diagnostics;
using System.Runtime.CompilerServices;
using EasyOcrSharp.Diagnostics;
using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf.Internal;
using EasyOcrSharp.Services;

namespace EasyOcrSharp.Pdf;

/// <summary>
/// PDF helpers for <see cref="IEasyOcrService"/>: OCR a scanned PDF page-by-page (optionally reading a
/// born-digital page's embedded text instead — see <see cref="PdfOcrOptions.TextLayer"/>), stream the pages as
/// they finish, or produce a searchable PDF (the original page images with an invisible, selectable OCR text
/// layer). Pages are rasterized with PDFium and processed one at a time to keep memory low.
/// </summary>
/// <remarks>
/// Each entry point emits one <c>easyocr.operations</c> / <c>easyocr.duration</c> data point for the whole
/// document, plus <c>easyocr.pages</c> and <c>easyocr.lines</c>, tagged with the operation name and the
/// outcome — see <see cref="EasyOcrDiagnostics"/>. Until this existed a PDF-only deployment produced no
/// document-level counter at all and was invisible on a dashboard. The per-page recognition these methods
/// drive is still measured separately by the single-image API, so both the document rate and the page rate
/// can be watched. Pages read from their embedded text layer count towards <c>easyocr.pages</c> but, running
/// no model, produce no per-page <c>extract</c> point.
/// <para>
/// They deliberately take <b>no</b> concurrency slot of their own: every page goes through the public
/// single-image API, which is already gated, so a second slot held across the whole document would
/// deadlock a service configured to allow one operation at a time — against itself, on page one.
/// </para>
/// </remarks>
public static class PdfOcrExtensions
{
    // ---------------------------------------------------------------- ExtractTextFromPdfAsync

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

    /// <summary>
    /// OCRs every page of a PDF read from <paramref name="pdfStream"/> and returns per-page results. The stream is
    /// read to its end before the first page is processed and is not disposed; it does not need to be seekable.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="pdfStream"/> is not readable.</exception>
    public static async Task<PdfOcrResult> ExtractTextFromPdfAsync(
        this IEasyOcrService service,
        Stream pdfStream,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(pdfStream);
        ArgumentNullException.ThrowIfNull(languages);
        var bytes = await ReadAllBytesAsync(pdfStream, nameof(pdfStream), cancellationToken).ConfigureAwait(false);
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
            await foreach (var page in ProcessPagesAsync(service, pdfBytes, langs, options, pdfOptions, builder: null, cancellationToken).ConfigureAwait(false))
            {
                pages.Add(page);
                // Counted per completed page, never from the document page count up front: a run that dies
                // on page 57 must report the 56 pages it actually OCRed. Counting ahead would credit a
                // failed job with a whole document of work, so the pages/sec panel would read highest
                // exactly when the service is broken.
                recorder.AddPages(1).AddLines(page.Ocr.Lines.Count);
            }

            recorder.Success();
            return new PdfOcrResult { Pages = pages };
        }
        catch (Exception ex)
        {
            recorder.Failure(ex);
            throw;
        }
    }

    // ---------------------------------------------------------------- ExtractTextFromPdfPagesAsync

    /// <summary>
    /// OCRs a PDF file and yields each page's result as soon as it is finished, instead of collecting the whole
    /// document. The file is read when enumeration starts.
    /// </summary>
    /// <remarks>
    /// Pages are produced on demand: the next page is not rendered until the consumer asks for it, so a consumer
    /// that stops early (<c>break</c>) stops the work, and memory stays at about one page however long the
    /// document is. Cancel with <paramref name="cancellationToken"/> or <c>WithCancellation</c>. The whole
    /// enumeration is recorded as one <c>pdf</c> operation; walking away part-way is recorded as cancelled.
    /// </remarks>
    public static IAsyncEnumerable<PdfPageResult> ExtractTextFromPdfPagesAsync(
        this IEasyOcrService service,
        string pdfPath,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentNullException.ThrowIfNull(languages);
        pdfOptions ??= new PdfOcrOptions();
        pdfOptions.Validate();

        var fullPath = Path.GetFullPath(pdfPath);
        return StreamPagesAsync(
            service,
            token => new ValueTask<byte[]>(File.ReadAllBytesAsync(fullPath, token)),
            ToArray(languages),
            options,
            pdfOptions,
            cancellationToken);
    }

    /// <summary>
    /// OCRs a PDF read from <paramref name="pdfStream"/> and yields each page's result as soon as it is finished.
    /// The stream is read to its end when enumeration starts and is not disposed.
    /// </summary>
    /// <remarks>See <see cref="ExtractTextFromPdfPagesAsync(IEasyOcrService, string, IEnumerable{string}, RecognitionOptions, PdfOcrOptions, CancellationToken)"/>.</remarks>
    /// <exception cref="ArgumentException"><paramref name="pdfStream"/> is not readable.</exception>
    public static IAsyncEnumerable<PdfPageResult> ExtractTextFromPdfPagesAsync(
        this IEasyOcrService service,
        Stream pdfStream,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(pdfStream);
        ArgumentNullException.ThrowIfNull(languages);
        if (!pdfStream.CanRead)
            throw new ArgumentException("The PDF stream must be readable.", nameof(pdfStream));
        pdfOptions ??= new PdfOcrOptions();
        pdfOptions.Validate();

        return StreamPagesAsync(
            service,
            token => ReadAllBytesAsync(pdfStream, nameof(pdfStream), token),
            ToArray(languages),
            options,
            pdfOptions,
            cancellationToken);
    }

    /// <summary>
    /// OCRs an in-memory PDF and yields each page's result as soon as it is finished.
    /// </summary>
    /// <remarks>See <see cref="ExtractTextFromPdfPagesAsync(IEasyOcrService, string, IEnumerable{string}, RecognitionOptions, PdfOcrOptions, CancellationToken)"/>.</remarks>
    public static IAsyncEnumerable<PdfPageResult> ExtractTextFromPdfPagesAsync(
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

        return StreamPagesAsync(
            service,
            _ => ValueTask.FromResult(pdfBytes),
            ToArray(languages),
            options,
            pdfOptions,
            cancellationToken);
    }

    // ---------------------------------------------------------------- CreateSearchablePdfAsync

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
    /// OCRs a PDF read from <paramref name="inputPdf"/> and writes the searchable PDF to
    /// <paramref name="outputPdf"/>. Returns the per-page OCR results.
    /// </summary>
    /// <remarks>
    /// The input is read to its end first. The output is written once the whole document has been built — the
    /// text-layer font is chosen from every page's text — and then flushed, so a failure part-way through
    /// writes nothing at all. Neither stream is disposed, and neither needs to be seekable.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="inputPdf"/> is not readable, or <paramref name="outputPdf"/> is not writable.</exception>
    public static async Task<PdfOcrResult> CreateSearchablePdfAsync(
        this IEasyOcrService service,
        Stream inputPdf,
        Stream outputPdf,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(inputPdf);
        ArgumentNullException.ThrowIfNull(outputPdf);
        ArgumentNullException.ThrowIfNull(languages);
        if (!outputPdf.CanWrite)
            throw new ArgumentException("The output stream must be writable.", nameof(outputPdf));

        var bytes = await ReadAllBytesAsync(inputPdf, nameof(inputPdf), cancellationToken).ConfigureAwait(false);
        var (result, pdf) = await CreateSearchablePdfAsync(service, bytes, languages, options, pdfOptions, cancellationToken).ConfigureAwait(false);
        await outputPdf.WriteAsync(pdf, cancellationToken).ConfigureAwait(false);
        await outputPdf.FlushAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// OCRs an in-memory PDF and returns both the per-page results and the searchable PDF bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The invisible text layer uses the base-14 Helvetica font while the recognized text is Latin-1, and
    /// switches to an embedded <c>/Identity-H</c> composite font when it is not, so non-Latin scripts stay
    /// searchable. See <see cref="PdfOcrOptions.TextLayerFont"/> and
    /// <see cref="PdfOcrOptions.TextLayerFontPath"/> for control over that, and
    /// <see cref="PdfOcrResult.TextLayerFontStatus"/> for what actually happened.
    /// </para>
    /// <para>
    /// A page whose embedded text is accepted under <see cref="PdfOcrOptions.TextLayer"/> is still rendered for
    /// its page image, and its invisible layer is written from that embedded text — exact, and placed word by
    /// word — instead of from OCR. The output is rebuilt from page images, so none of the source document's
    /// own text objects are carried over: every page ends up with exactly one text layer, never the original
    /// text with an OCR copy on top.
    /// </para>
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

            await foreach (var page in ProcessPagesAsync(service, pdfBytes, langs, options, pdfOptions, builder, cancellationToken).ConfigureAwait(false))
            {
                pages.Add(page);
                // Counted only once the page is both recognized and laid into the output document — see the
                // note on the plain-OCR overload above.
                recorder.AddPages(1).AddLines(page.Ocr.Lines.Count);
            }

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

    // ---------------------------------------------------------------- core

    /// <summary>
    /// The shared page pipeline: take each page from <see cref="PdfRasterizer.ReadPages"/>, OCR it — or shape its
    /// accepted embedded text — and lay it into <paramref name="builder"/> when producing a searchable PDF.
    /// </summary>
    /// <remarks>
    /// The page and its image are disposed before the result is yielded, so nothing the consumer holds keeps a
    /// bitmap alive. With the defaults (<see cref="PdfOcrOptions.TextLayer"/> =
    /// <see cref="PdfTextLayerMode.Ignore"/>, fixed DPI) every page is rendered and OCR'd exactly as before.
    /// </remarks>
    private static async IAsyncEnumerable<PdfPageResult> ProcessPagesAsync(
        IEasyOcrService service,
        byte[] pdfBytes,
        string[] languages,
        RecognitionOptions? options,
        PdfOcrOptions pdfOptions,
        SearchablePdfBuilder? builder,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = PdfReadSettings.From(pdfOptions, options, imagesRequired: builder is not null);
        foreach (var page in PdfRasterizer.ReadPages(pdfBytes, settings, cancellationToken))
        {
            PdfPageResult result;
            using (page)
            {
                OcrResult ocr;
                PdfPageSource source;
                if (page.EmbeddedLines is { } lines)
                {
                    long started = Stopwatch.GetTimestamp();
                    ocr = EmbeddedTextLayer.CreateResult(lines, options, languages, page.PixelWidth, page.PixelHeight, page.EmbeddedTextDuration);
                    ocr = ocr with { Duration = page.EmbeddedTextDuration + Stopwatch.GetElapsedTime(started) };
                    source = PdfPageSource.EmbeddedText;

                    // The hidden layer is laid out line by line with word geometry whatever grouping the caller
                    // asked the result for: a paragraph or word grouping would only coarsen the placement, and
                    // the words are exact here, unlike on an OCR page where they cost a request.
                    builder?.AddPage(page.Image!, TextLayerFor(lines, options, languages, page), page.Dpi, pdfOptions.JpegQuality);
                }
                else
                {
                    ocr = await service.ExtractTextFromImage(page.Image!, languages, options, cancellationToken).ConfigureAwait(false);
                    source = PdfPageSource.Ocr;
                    builder?.AddPage(page.Image!, ocr, page.Dpi, pdfOptions.JpegQuality);
                }

                result = new PdfPageResult
                {
                    PageNumber = page.PageNumber,
                    Ocr = ocr,
                    PixelWidth = page.PixelWidth,
                    PixelHeight = page.PixelHeight,
                    Dpi = page.Dpi,
                    Source = source,
                };
            }

            pdfOptions.Progress?.Report(new PdfPageProgress(result.PageNumber, page.PageCount));
            yield return result;
        }
    }

    /// <summary>
    /// The streaming entry points' shared body: load the bytes, then forward <see cref="ProcessPagesAsync"/> under
    /// one <c>pdf</c> operation recording. Spelled out with an explicit enumerator because an iterator cannot
    /// yield from inside a <c>catch</c>.
    /// </summary>
    private static async IAsyncEnumerable<PdfPageResult> StreamPagesAsync(
        IEasyOcrService service,
        Func<CancellationToken, ValueTask<byte[]>> load,
        string[] languages,
        RecognitionOptions? options,
        PdfOcrOptions pdfOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pdfBytes = await load(cancellationToken).ConfigureAwait(false);

        // Declaration order is disposal order reversed: the recorder stamps its outcome on the span, so the span
        // must outlive it.
        using var activity = EasyOcrDiagnostics.ActivitySource.StartActivity("EasyOcr.Pdf", ActivityKind.Internal);
        using var recorder = EasyOcrDiagnostics.Begin(EasyOcrDiagnostics.OperationNames.Pdf, ProviderOf(service))
            .WithLanguages(languages)
            .Annotate(activity);

        bool completed = false;
        bool faulted = false;
        try
        {
            await using var pages = ProcessPagesAsync(service, pdfBytes, languages, options, pdfOptions, builder: null, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                PdfPageResult current;
                try
                {
                    if (!await pages.MoveNextAsync().ConfigureAwait(false)) break;
                    current = pages.Current;
                }
                catch (Exception ex)
                {
                    faulted = true;
                    recorder.Failure(ex);
                    throw;
                }

                recorder.AddPages(1).AddLines(current.Ocr.Lines.Count);
                yield return current;
            }
            completed = true;
        }
        finally
        {
            // A consumer that walks away mid-document (a `break`, or an exception of its own) made a choice
            // rather than hitting a fault, so it is recorded as a cancellation — otherwise the recorder's
            // deliberate "error" default would brand every partially consumed document as a failure.
            if (completed) recorder.Success();
            else if (!faulted) recorder.Canceled();
        }
    }

    /// <summary>The hidden-layer text of an embedded page: its lines with word geometry, within the caller's region.</summary>
    private static OcrResult TextLayerFor(IReadOnlyList<EmbeddedLine> lines, RecognitionOptions? options, string[] languages, RenderedPage page)
    {
        var layout = (options ?? RecognitionOptions.Default) with
        {
            Grouping = TextGrouping.Line,
            WordLevelDetail = WordLevelDetail.Words,
        };
        return EmbeddedTextLayer.CreateResult(lines, layout, languages, page.PixelWidth, page.PixelHeight, TimeSpan.Zero);
    }

    private static string[] ToArray(IEnumerable<string> languages) => languages as string[] ?? languages.ToArray();

    /// <summary>Reads a caller's stream to its end without disposing it; seekability is not required.</summary>
    private static async ValueTask<byte[]> ReadAllBytesAsync(Stream stream, string paramName, CancellationToken cancellationToken)
    {
        if (!stream.CanRead)
            throw new ArgumentException("The PDF stream must be readable.", paramName);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
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
