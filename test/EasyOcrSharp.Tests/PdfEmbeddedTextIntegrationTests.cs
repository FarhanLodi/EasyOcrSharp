using System.Text;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Pdf.Internal;
using EasyOcrSharp.Redaction;
using EasyOcrSharp.Services;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// The born-digital PDF path end to end through real PDFium, with a counting stub standing in for OCR so every
/// assertion is exact and offline: embedded text is read verbatim and in the OCR pixel space, a broken
/// <c>/ToUnicode</c> map and a scanned page still go to OCR, <see cref="PdfTextLayerMode.Auto"/> decides page by
/// page, auto DPI picks a resolution per page, the streaming and <see cref="Stream"/> overloads agree with the
/// buffered ones, and a searchable PDF built from embedded pages carries its text exactly once.
/// </summary>
/// <remarks>Model-free, but PDFium-backed, so it runs with the other PDF classes in the serialized collection.</remarks>
[Trait("Category", "Integration")]
[Collection(OcrIntegrationCollection.Name)]
public class PdfEmbeddedTextIntegrationTests
{
    private static readonly string[] English = { "en" };

    /// <summary>The known source text of the born-digital report page.</summary>
    internal static readonly string[] Report =
    {
        "Quarterly Report for the Zanzibar Trading Company",
        "Revenue grew 12.5% to 4,820,000 dollars in the third quarter.",
        "Operating costs fell by 3.1% after the warehouse consolidation.",
        "The board approved a dividend of 0.42 per share on 14 October.",
        "Customer satisfaction reached 91 points, up from 87 last year.",
        "Headcount remained stable at 312 employees across four sites.",
        "Capital expenditure is planned at 1.2 million for next year.",
        "Inventory turnover improved to 7.8 times from 6.9 times.",
        "All figures are unaudited and subject to final review.",
        "Prepared by the finance team, contact: finance@example.com",
    };

    internal static string ReportText => string.Join("\n", Report);

    private static byte[] ReportPdf(int pages = 1, bool matrixScaled = false)
    {
        var pdf = new BornDigitalPdf();
        for (int i = 0; i < pages; i++) pdf.AddTextPage(Report, matrixScaled: matrixScaled);
        return pdf.Build();
    }

    /// <summary>An image-only page — a scan with no text layer at all.</summary>
    private static Image<Rgb24> BlankScan(int width = 850, int height = 1100)
        => new(width, height, new Rgb24(250, 250, 250));

    // ---------------------------------------------------------------- reading embedded text

    [Fact]
    public async Task PreferEmbedded_reads_born_digital_text_exactly_without_running_ocr()
    {
        await using var ocr = new CountingOcrService();
        var doc = await ocr.ExtractTextFromPdfAsync(ReportPdf(), English,
            pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.PreferEmbedded });

        var page = Assert.Single(doc.Pages);
        Assert.Equal(PdfPageSource.EmbeddedText, page.Source);
        Assert.Equal(ReportText, page.Ocr.FullText);
        Assert.Equal(Report, page.Ocr.Lines.Select(l => l.Text));
        Assert.All(page.Ocr.Lines, l => Assert.Equal(1.0, l.Confidence));
        Assert.Equal(0, ocr.Calls);

        // Same page geometry the OCR path reports at the default 200 DPI.
        Assert.Equal(200, page.Dpi);
        Assert.InRange(page.PixelWidth, 1699, 1700);
        Assert.InRange(page.PixelHeight, 2199, 2200);
        Assert.Equal(page.PixelWidth, page.Ocr.SourceWidth);
        Assert.All(page.Ocr.Lines, l =>
        {
            Assert.InRange(l.BoundingBox.MinX, 0, page.PixelWidth);
            Assert.InRange(l.BoundingBox.MaxY, 0, page.PixelHeight);
        });
    }

    [Theory]
    [InlineData(200)]
    [InlineData(300)]
    public async Task Embedded_line_boxes_land_where_the_text_is_drawn_in_rendered_pixels(int dpi)
    {
        await using var ocr = new CountingOcrService();
        var doc = await ocr.ExtractTextFromPdfAsync(ReportPdf(), English,
            pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.PreferEmbedded, Dpi = dpi });

        double px = dpi / 72.0;
        var runs = BornDigitalPdf.LinesAt(Report).ToList();
        var lines = doc.Pages[0].Ocr.Lines;
        Assert.Equal(runs.Count, lines.Count);
        for (int i = 0; i < runs.Count; i++)
        {
            // The run starts 72 pt from the left; the first glyph's ink starts within a side bearing of it.
            double left = runs[i].X * px;
            Assert.InRange(lines[i].BoundingBox.MinX, left - 1, left + 2 * px);

            // The baseline is (792 - y) pt from the top. The box bottom is the baseline or a descender below it
            // (at most ~0.25 em = 2.75 pt); the top is the cap/ascender height above it (~0.7-0.75 em).
            double baseline = (BornDigitalPdf.LetterHeight - runs[i].Baseline) * px;
            Assert.InRange(lines[i].BoundingBox.MaxY, baseline - 1, baseline + 3.5 * px);
            Assert.InRange(lines[i].BoundingBox.MinY, baseline - 11 * px, baseline - 6 * px);
        }
    }

    [Fact]
    public async Task Matrix_scaled_text_with_an_unscaled_font_size_still_reads_as_whole_words()
    {
        // "/F1 1 Tf" + an 11x text matrix: PDFium reports a 1 pt font. Thresholds sized from it would split
        // every word at its letter gaps.
        await using var ocr = new CountingOcrService();
        var doc = await ocr.ExtractTextFromPdfAsync(ReportPdf(matrixScaled: true), English,
            pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.PreferEmbedded });

        Assert.Equal(PdfPageSource.EmbeddedText, doc.Pages[0].Source);
        Assert.Equal(ReportText, doc.Pages[0].Ocr.FullText);
    }

    [Fact]
    public async Task Default_options_still_ocr_every_page_exactly_as_before()
    {
        await using var ocr = new CountingOcrService();
        var doc = await ocr.ExtractTextFromPdfAsync(ReportPdf(pages: 2), English);

        Assert.Equal(2, ocr.Calls);
        Assert.All(doc.Pages, p =>
        {
            Assert.Equal(PdfPageSource.Ocr, p.Source);
            Assert.Equal(200, p.Dpi);
            Assert.Equal(CountingOcrService.Text, p.Ocr.FullText);
        });
        Assert.All(ocr.ImageSizes, s => Assert.InRange(s.Width, 1699, 1700));
    }

    [Fact]
    public async Task A_broken_ToUnicode_map_fails_the_quality_gate_and_falls_back_to_ocr()
    {
        // Renders perfectly, extracts as private-use code points: exactly the case the gate exists for.
        var pdf = new BornDigitalPdf { BrokenToUnicode = true }.AddTextPage(Report).Build();

        await using var ocr = new CountingOcrService();
        foreach (var mode in new[] { PdfTextLayerMode.PreferEmbedded, PdfTextLayerMode.Auto })
        {
            var doc = await ocr.ExtractTextFromPdfAsync(pdf, English, pdfOptions: new PdfOcrOptions { TextLayer = mode });
            Assert.Equal(PdfPageSource.Ocr, doc.Pages[0].Source);
            Assert.Equal(CountingOcrService.Text, doc.Pages[0].Ocr.FullText);
        }
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public async Task Auto_ocrs_a_scan_carrying_only_a_small_digital_header_that_PreferEmbedded_would_read()
    {
        using var scan = BlankScan();
        var header = new BornDigitalPdf.TextRun("CONFIDENTIAL - Bates number ACME-000123 - page 1 of 9", 72, 760, 8);
        var pdf = new BornDigitalPdf().AddPage(BornDigitalPdf.LetterWidth, BornDigitalPdf.LetterHeight, new[] { header }, scan).Build();

        await using var ocr = new CountingOcrService();
        var preferred = await ocr.ExtractTextFromPdfAsync(pdf, English, pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.PreferEmbedded });
        Assert.Equal(PdfPageSource.EmbeddedText, preferred.Pages[0].Source);
        Assert.Equal(header.Text, preferred.Pages[0].Ocr.FullText);
        Assert.Equal(0, ocr.Calls);

        // The header covers well under 1% of the page, so Auto sends the page — and its scanned content — to OCR.
        var auto = await ocr.ExtractTextFromPdfAsync(pdf, English, pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.Auto });
        Assert.Equal(PdfPageSource.Ocr, auto.Pages[0].Source);
        Assert.Equal(1, ocr.Calls);
    }

    [Fact]
    public async Task Auto_decides_page_by_page_in_a_mixed_document()
    {
        using var scan = BlankScan();
        var pdf = new BornDigitalPdf()
            .AddTextPage(Report)
            .AddPage(BornDigitalPdf.LetterWidth, BornDigitalPdf.LetterHeight, Array.Empty<BornDigitalPdf.TextRun>(), scan)
            .AddTextPage(Report)
            .Build();

        await using var ocr = new CountingOcrService();
        var doc = await ocr.ExtractTextFromPdfAsync(pdf, English, pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.Auto });

        Assert.Equal(new[] { PdfPageSource.EmbeddedText, PdfPageSource.Ocr, PdfPageSource.EmbeddedText }, doc.Pages.Select(p => p.Source));
        Assert.Equal(new[] { 1, 2, 3 }, doc.Pages.Select(p => p.PageNumber));
        Assert.Equal(1, ocr.Calls);
        Assert.Equal($"{ReportText}\n\n{CountingOcrService.Text}\n\n{ReportText}", doc.FullText);
    }

    [Fact]
    public async Task Grouping_word_detail_and_region_apply_to_embedded_pages()
    {
        var pdf = ReportPdf();
        await using var ocr = new CountingOcrService();
        var embedded = new PdfOcrOptions { TextLayer = PdfTextLayerMode.PreferEmbedded };

        var words = await ocr.ExtractTextFromPdfAsync(pdf, English, new RecognitionOptions { WordLevelDetail = WordLevelDetail.Words }, embedded);
        Assert.All(words.Pages[0].Ocr.Lines, l => Assert.Equal(l.Text.Split(' '), l.Words.Select(w => w.Text)));

        // 18 pt line pitch for 11 pt text: one paragraph, lines in order.
        var paragraphs = await ocr.ExtractTextFromPdfAsync(pdf, English, new RecognitionOptions { Grouping = TextGrouping.Paragraph }, embedded);
        Assert.Equal(ReportText, Assert.Single(paragraphs.Pages[0].Ocr.Lines).Text);

        var perWord = await ocr.ExtractTextFromPdfAsync(pdf, English, new RecognitionOptions { Grouping = TextGrouping.Word }, embedded);
        Assert.Equal(Report.SelectMany(l => l.Split(' ')), perWord.Pages[0].Ocr.Lines.Select(l => l.Text));

        // The top 15% of the page holds only the first three lines (baselines 720, 702 and 684 pt).
        var region = await ocr.ExtractTextFromPdfAsync(pdf, English, new RecognitionOptions { Region = OcrRegion.Fraction(0, 0, 1, 0.15) }, embedded);
        Assert.Equal(Report.Take(3), region.Pages[0].Ocr.Lines.Select(l => l.Text));
        Assert.Equal(0, ocr.Calls);
    }

    // ---------------------------------------------------------------- auto DPI

    [Fact]
    public async Task Auto_dpi_picks_each_pages_resolution_from_its_size()
    {
        using var scan = BlankScan(400, 400);
        var pdf = new BornDigitalPdf()
            .AddPage(BornDigitalPdf.LetterWidth, BornDigitalPdf.LetterHeight, Array.Empty<BornDigitalPdf.TextRun>(), scan)
            .AddPage(BornDigitalPdf.A4Width, BornDigitalPdf.A4Height, Array.Empty<BornDigitalPdf.TextRun>(), scan)
            .AddPage(252, 144, Array.Empty<BornDigitalPdf.TextRun>(), scan) // business card
            .AddPage(BornDigitalPdf.LetterWidth, BornDigitalPdf.LetterHeight, Array.Empty<BornDigitalPdf.TextRun>(), scan)
            .Build();

        await using var ocr = new CountingOcrService();
        var doc = await ocr.ExtractTextFromPdfAsync(pdf, English, pdfOptions: new PdfOcrOptions { Dpi = PdfOcrOptions.AutoDpi });

        Assert.Equal(new[] { 232, 218, 400, 232 }, doc.Pages.Select(p => p.Dpi));
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            // The image handed to OCR is the page at that resolution, and the result reports the same size.
            Assert.Equal(doc.Pages[i].PixelWidth, ocr.ImageSizes[i].Width);
            Assert.Equal(doc.Pages[i].PixelHeight, ocr.ImageSizes[i].Height);
        }

        // Letter and A4 fill, without exceeding, the detector's default 2560 px canvas.
        Assert.InRange(doc.Pages[0].PixelHeight, 2540, 2560);
        Assert.InRange(doc.Pages[1].PixelHeight, 2540, 2560);
    }

    [Fact]
    public async Task Auto_dpi_follows_the_callers_detector_canvas()
    {
        using var scan = BlankScan(400, 400);
        var pdf = new BornDigitalPdf().AddPage(BornDigitalPdf.LetterWidth, BornDigitalPdf.LetterHeight, Array.Empty<BornDigitalPdf.TextRun>(), scan).Build();

        await using var ocr = new CountingOcrService();
        var options = new RecognitionOptions { Detection = new DetectionOptions { CanvasSize = 4000 } };
        var doc = await ocr.ExtractTextFromPdfAsync(pdf, English, options, new PdfOcrOptions { Dpi = PdfOcrOptions.AutoDpi });

        Assert.Equal(363, doc.Pages[0].Dpi);
    }

    [Fact]
    public async Task Auto_dpi_also_sets_the_pixel_space_of_embedded_pages()
    {
        await using var ocr = new CountingOcrService();
        var doc = await ocr.ExtractTextFromPdfAsync(ReportPdf(), English,
            pdfOptions: new PdfOcrOptions { Dpi = PdfOcrOptions.AutoDpi, TextLayer = PdfTextLayerMode.Auto });

        var page = doc.Pages[0];
        Assert.Equal(PdfPageSource.EmbeddedText, page.Source);
        Assert.Equal(232, page.Dpi);
        Assert.InRange(page.Ocr.Lines[0].BoundingBox.MinX, 232 - 1, 232 + 2 * 232 / 72.0);
    }

    [Fact]
    public async Task Redaction_renders_auto_dpi_at_the_default_resolution_it_lays_pages_out_with()
    {
        // The redaction pipeline reports no per-page resolution, so AutoDpi falls back to 200 there — and the
        // rebuilt document keeps the source page size instead of collapsing to an infinite scale.
        using var scan = BlankScan();
        var source = new BornDigitalPdf().AddPage(BornDigitalPdf.LetterWidth, BornDigitalPdf.LetterHeight, Array.Empty<BornDigitalPdf.TextRun>(), scan).Build();

        await using var ocr = new CountingOcrService();
        var redacted = await ocr.RedactPdfAsync(source, English, pdfOptions: new PdfOcrOptions { Dpi = PdfOcrOptions.AutoDpi });
        Assert.InRange(redacted.Pages[0].PixelWidth, 1699, 1700);

        var reread = await ocr.ExtractTextFromPdfAsync(redacted.Pdf, English);
        Assert.InRange(reread.Pages[0].PixelWidth, 1699, 1700);
        Assert.InRange(reread.Pages[0].PixelHeight, 2199, 2200);
    }

    // ---------------------------------------------------------------- streaming and Stream overloads

    [Fact]
    public async Task Streaming_yields_pages_in_order_and_breaking_out_stops_the_work()
    {
        using var scan = BlankScan();
        var builder = new BornDigitalPdf();
        for (int i = 0; i < 5; i++) builder.AddPage(BornDigitalPdf.LetterWidth, BornDigitalPdf.LetterHeight, Array.Empty<BornDigitalPdf.TextRun>(), scan);
        var pdf = builder.Build();

        var progress = new List<int>();
        var options = new PdfOcrOptions { Progress = new SyncProgress(p => progress.Add(p.PageNumber)) };

        await using var ocr = new CountingOcrService();
        var seen = new List<int>();
        await foreach (var page in ocr.ExtractTextFromPdfPagesAsync(pdf, English, pdfOptions: options))
        {
            seen.Add(page.PageNumber);
            if (seen.Count == 2) break;
        }

        Assert.Equal(new[] { 1, 2 }, seen);
        // Pull-based: page 3 is never rendered, let alone recognized, once the consumer has walked away.
        Assert.Equal(2, ocr.Calls);
        Assert.Equal(new[] { 1, 2 }, progress);
    }

    [Fact]
    public async Task Streaming_produces_the_same_pages_as_the_buffered_call()
    {
        using var scan = BlankScan();
        var pdf = new BornDigitalPdf()
            .AddTextPage(Report)
            .AddPage(BornDigitalPdf.LetterWidth, BornDigitalPdf.LetterHeight, Array.Empty<BornDigitalPdf.TextRun>(), scan)
            .Build();
        var options = new PdfOcrOptions { TextLayer = PdfTextLayerMode.Auto };

        await using var ocr = new CountingOcrService();
        var buffered = await ocr.ExtractTextFromPdfAsync(pdf, English, pdfOptions: options);

        var streamed = new List<PdfPageResult>();
        await foreach (var page in ocr.ExtractTextFromPdfPagesAsync(pdf, English, pdfOptions: options))
            streamed.Add(page);

        Assert.Equal(buffered.Pages.Select(p => (p.PageNumber, p.Source, p.Dpi, p.Ocr.FullText)),
                     streamed.Select(p => (p.PageNumber, p.Source, p.Dpi, p.Ocr.FullText)));
    }

    [Fact]
    public async Task Streaming_from_a_path_and_a_stream_matches_the_bytes()
    {
        var pdf = ReportPdf(pages: 2);
        var path = Path.Combine(Path.GetTempPath(), $"easyocr-born-digital-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, pdf);
        try
        {
            var options = new PdfOcrOptions { TextLayer = PdfTextLayerMode.PreferEmbedded };
            await using var ocr = new CountingOcrService();

            var fromPath = new List<string>();
            await foreach (var page in ocr.ExtractTextFromPdfPagesAsync(path, English, pdfOptions: options))
                fromPath.Add(page.Ocr.FullText);

            var fromStream = new List<string>();
            await using (var input = new NonSeekableStream(new MemoryStream(pdf)))
            {
                await foreach (var page in ocr.ExtractTextFromPdfPagesAsync(input, English, pdfOptions: options))
                    fromStream.Add(page.Ocr.FullText);
            }

            Assert.Equal(new[] { ReportText, ReportText }, fromPath);
            Assert.Equal(fromPath, fromStream);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Stream_overloads_produce_what_the_byte_array_overloads_produce()
    {
        var pdf = ReportPdf();
        var options = new PdfOcrOptions { TextLayer = PdfTextLayerMode.PreferEmbedded };
        await using var ocr = new CountingOcrService();

        // Extraction from a stream that cannot seek.
        var fromBytes = await ocr.ExtractTextFromPdfAsync(pdf, English, pdfOptions: options);
        PdfOcrResult fromStream;
        await using (var input = new NonSeekableStream(new MemoryStream(pdf)))
            fromStream = await ocr.ExtractTextFromPdfAsync(input, English, pdfOptions: options);
        Assert.Equal(fromBytes.FullText, fromStream.FullText);

        // Searchable PDF written to a stream that cannot seek: byte-identical to the in-memory overload.
        var (expectedResult, expectedPdf) = await ocr.CreateSearchablePdfAsync(pdf, English, pdfOptions: options);
        using var sink = new MemoryStream();
        PdfOcrResult written;
        await using (var input = new NonSeekableStream(new MemoryStream(pdf)))
        await using (var output = new NonSeekableStream(sink, leaveOpen: true))
        {
            written = await ocr.CreateSearchablePdfAsync(input, output, English, pdfOptions: options);
        }
        Assert.Equal(expectedPdf, sink.ToArray());
        Assert.Equal(expectedResult.FullText, written.FullText);
    }

    [Fact]
    public async Task Unreadable_input_and_unwritable_output_streams_are_rejected()
    {
        await using var ocr = new CountingOcrService();
        var pdf = ReportPdf();

        await using var writeOnly = new NonSeekableStream(new MemoryStream(), canRead: false);
        await Assert.ThrowsAsync<ArgumentException>(() => ocr.ExtractTextFromPdfAsync(writeOnly, English));
        Assert.Throws<ArgumentException>(() => ocr.ExtractTextFromPdfPagesAsync(writeOnly, English));

        await using var input = new MemoryStream(pdf);
        await using var readOnly = new MemoryStream(pdf, writable: false);
        await Assert.ThrowsAsync<ArgumentException>(() => ocr.CreateSearchablePdfAsync(input, readOnly, English));
    }

    // ---------------------------------------------------------------- searchable PDF

    [Fact]
    public async Task A_searchable_pdf_built_from_embedded_pages_carries_the_text_exactly_once()
    {
        var source = ReportPdf(pages: 2);
        var options = new PdfOcrOptions { TextLayer = PdfTextLayerMode.PreferEmbedded };

        await using var ocr = new CountingOcrService();
        var (result, searchable) = await ocr.CreateSearchablePdfAsync(source, English, pdfOptions: options);

        Assert.Equal(0, ocr.Calls);
        Assert.All(result.Pages, p => Assert.Equal(PdfPageSource.EmbeddedText, p.Source));
        Assert.Equal(PdfTextLayerFontStatus.Standard, result.TextLayerFontStatus);

        // One invisible (render mode 3) text object per page: the source's visible text objects are not carried
        // over — each page is its rendered image plus the single layer. (Only the 8-byte operator sequence is
        // counted; a shorter token could occur by chance inside the JPEG data.)
        var raw = Encoding.Latin1.GetString(searchable);
        Assert.Equal(2, CountOf(raw, "BT\n3 Tr\n"));

        // Read back, the document holds the source text verbatim — every line once, nothing doubled.
        var reread = await ocr.ExtractTextFromPdfAsync(searchable, English, pdfOptions: options);
        Assert.All(reread.Pages, p =>
        {
            Assert.Equal(PdfPageSource.EmbeddedText, p.Source);
            Assert.Equal(ReportText, p.Ocr.FullText);
            Assert.Equal(1, CountOf(p.Ocr.FullText, "Zanzibar"));
        });
        Assert.Equal(0, ocr.Calls);
    }

    [Fact]
    public async Task A_mixed_document_gets_an_embedded_layer_on_digital_pages_and_an_ocr_layer_on_scans()
    {
        using var scan = BlankScan();
        var source = new BornDigitalPdf()
            .AddTextPage(Report)
            .AddPage(BornDigitalPdf.LetterWidth, BornDigitalPdf.LetterHeight, Array.Empty<BornDigitalPdf.TextRun>(), scan)
            .Build();

        await using var ocr = new CountingOcrService();
        var (result, searchable) = await ocr.CreateSearchablePdfAsync(source, English,
            pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.Auto });

        Assert.Equal(new[] { PdfPageSource.EmbeddedText, PdfPageSource.Ocr }, result.Pages.Select(p => p.Source));
        Assert.Equal(1, ocr.Calls);

        var reread = await ocr.ExtractTextFromPdfAsync(searchable, English,
            pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.PreferEmbedded });
        Assert.Equal(ReportText, reread.Pages[0].Ocr.FullText);
        // The stub's one short line fails the gate on re-read (under 20 characters) and is OCR'd — by the stub.
        Assert.Equal(CountingOcrService.Text, reread.Pages[1].Ocr.FullText);
    }

    // ---------------------------------------------------------------- helpers

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>A synchronous <see cref="IProgress{T}"/>: <see cref="Progress{T}"/> posts asynchronously.</summary>
    private sealed class SyncProgress(Action<PdfPageProgress> report) : IProgress<PdfPageProgress>
    {
        public void Report(PdfPageProgress value) => report(value);
    }

    /// <summary>A stream that refuses to seek (a network body, a pipe) — optionally refusing reads too.</summary>
    internal sealed class NonSeekableStream(Stream inner, bool leaveOpen = false, bool canRead = true) : Stream
    {
        public override bool CanRead => canRead && inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => canRead ? inner.Read(buffer, offset, count) : throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// An <see cref="IEasyOcrService"/> that recognizes every image as one fixed short line, counting calls and
    /// recording the size of each image it was handed — so a test can tell exactly which pages went to OCR and
    /// at what resolution.
    /// </summary>
    internal sealed class CountingOcrService : IEasyOcrService
    {
        public const string Text = "stub-ocr-text";

        private int _calls;
        private readonly List<(int Width, int Height)> _sizes = new();

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<(int Width, int Height)> ImageSizes
        {
            get { lock (_sizes) return _sizes.ToArray(); }
        }

        public Task<OcrResult> ExtractTextFromImage(
            Image<Rgb24> image, IEnumerable<string> languages, RecognitionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            lock (_sizes) _sizes.Add((image.Width, image.Height));

            var poly = new OcrPoint[] { new(10, 10), new(200, 10), new(200, 40), new(10, 40) };
            var line = new OcrLine { Text = Text, Confidence = 0.9, BoundingPolygon = poly, BoundingBox = OcrBoundingBox.FromPoints(poly) };
            return Task.FromResult(new OcrResult
            {
                FullText = Text,
                Lines = new[] { line },
                Languages = languages.ToArray(),
                SourceWidth = image.Width,
                SourceHeight = image.Height,
            });
        }

        public Task<OcrResult> ExtractTextFromImage(string imagePath, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OcrResult> ExtractTextFromImage(Stream imageStream, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OcrResult> ExtractTextFromImage(byte[] imageBytes, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OcrResult> ExtractTextFromImage(ReadOnlyMemory<byte> imageBytes, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> DetectLanguagesAsync(Image<Rgb24> image, IEnumerable<string>? candidates = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> DetectLanguagesAsync(string imagePath, IEnumerable<string>? candidates = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
