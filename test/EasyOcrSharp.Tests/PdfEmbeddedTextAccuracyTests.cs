using System.Diagnostics;
using System.Text;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Pdf.Internal;
using EasyOcrSharp.Services;
using Xunit;
using Xunit.Abstractions;

namespace EasyOcrSharp.Tests;

/// <summary>
/// The born-digital PDF path against the real OCR models: a scanned PDF still goes through OCR under
/// <see cref="PdfTextLayerMode.Auto"/>, embedded text is exact where OCR is approximate, both report lines in
/// one pixel space, embedded pages cost milliseconds where OCR costs seconds (ms/page is written to the test
/// output), a mixed searchable PDF carries each page's text once, and on the real fixtures the text layer agrees
/// with what OCR sees on the page.
/// </summary>
[Trait("Category", "Integration")]
[Collection(OcrIntegrationCollection.Name)]
public class PdfEmbeddedTextAccuracyTests(ITestOutputHelper output)
{
    private static readonly string[] English = { "en" };

    private static string ReportText => PdfEmbeddedTextIntegrationTests.ReportText;

    private static byte[] ReportPdf(int pages = 1)
    {
        var pdf = new BornDigitalPdf();
        for (int i = 0; i < pages; i++) pdf.AddTextPage(PdfEmbeddedTextIntegrationTests.Report);
        return pdf.Build();
    }

    private static Image<Rgb24> Sample()
    {
        var path = TestAssets.Image("sample.png");
        Assert.True(path is not null, "assets/sample.png not found — ensure it is copied to the test output.");
        return Image.Load<Rgb24>(path!);
    }

    /// <summary>Collapses every whitespace run to one space, so CER compares text rather than line breaking.</summary>
    private static string Collapse(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public async Task A_scanned_pdf_still_goes_through_ocr_under_Auto()
    {
        using var sample = Sample();
        var builder = new SearchablePdfBuilder();
        builder.AddPage(sample, OcrResult.Empty, 150, 85); // image only: no text layer at all
        var scanned = builder.Build();

        await using var ocr = new EasyOcrService();
        var doc = await ocr.ExtractTextFromPdfAsync(scanned, English, pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.Auto });

        var page = Assert.Single(doc.Pages);
        Assert.Equal(PdfPageSource.Ocr, page.Source);
        Assert.Contains("HELLO", TestAssets.Normalize(page.Ocr.FullText));
        Assert.Contains("EASYOCRSHARP", TestAssets.Normalize(page.Ocr.FullText));
    }

    [Fact]
    public async Task Embedded_text_is_exact_where_ocr_of_the_same_page_is_approximate()
    {
        var pdf = ReportPdf();
        await using var ocr = new EasyOcrService();

        var embedded = await ocr.ExtractTextFromPdfAsync(pdf, English, pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.Auto });
        var recognized = await ocr.ExtractTextFromPdfAsync(pdf, English);

        Assert.Equal(PdfPageSource.EmbeddedText, embedded.Pages[0].Source);
        Assert.Equal(PdfPageSource.Ocr, recognized.Pages[0].Source);

        double embeddedCer = TextMetrics.CharacterErrorRate(Collapse(ReportText), Collapse(embedded.FullText));
        double ocrCer = TextMetrics.CharacterErrorRate(Collapse(ReportText), Collapse(recognized.FullText));
        output.WriteLine($"CER vs source text: embedded {embeddedCer:P2}, OCR at 200 DPI {ocrCer:P2}");

        Assert.Equal(0, embeddedCer);
        // Clean 11 pt Helvetica: OCR is close but not exact — that gap is what the text layer recovers.
        Assert.True(ocrCer < 0.15, $"OCR CER {ocrCer:P2} on a clean born-digital page; the comparison page itself may be broken.");
        Assert.Contains("ZANZIBAR", TestAssets.Normalize(recognized.FullText));
    }

    [Fact]
    public async Task Embedded_and_ocr_lines_share_one_pixel_space()
    {
        var pdf = ReportPdf();
        await using var ocr = new EasyOcrService();

        var embedded = (await ocr.ExtractTextFromPdfAsync(pdf, English, pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.PreferEmbedded })).Pages[0];
        var recognized = (await ocr.ExtractTextFromPdfAsync(pdf, English)).Pages[0];

        Assert.Equal(recognized.PixelWidth, embedded.PixelWidth);
        Assert.Equal(recognized.PixelHeight, embedded.PixelHeight);

        // Every embedded line should have an OCR line at the same height that overlaps it horizontally. OCR
        // boxes carry a margin and may split a line, so the match is on vertical centre and horizontal overlap,
        // not on identical boxes.
        int matched = 0;
        foreach (var line in embedded.Ocr.Lines)
        {
            var box = line.BoundingBox;
            bool found = recognized.Ocr.Lines.Any(o =>
                Math.Abs(o.BoundingBox.CenterY - box.CenterY) <= box.Height / 2
                && Math.Min(o.BoundingBox.MaxX, box.MaxX) > Math.Max(o.BoundingBox.MinX, box.MinX));
            if (found) matched++;
        }

        output.WriteLine($"{matched}/{embedded.Ocr.Lines.Count} embedded lines coincide with an OCR line.");
        Assert.True(matched >= 0.8 * embedded.Ocr.Lines.Count,
            $"Only {matched} of {embedded.Ocr.Lines.Count} embedded lines coincide with an OCR line — the two paths disagree on coordinates.");
    }

    [Fact]
    public async Task Embedded_pages_cost_milliseconds_where_ocr_costs_seconds()
    {
        const int Pages = 4;
        var pdf = ReportPdf(Pages);
        await using var ocr = new EasyOcrService();

        // Warm up: model download/session creation must not be billed to the OCR measurement.
        await ocr.ExtractTextFromPdfAsync(ReportPdf(), English);

        var sw = Stopwatch.StartNew();
        var recognized = await ocr.ExtractTextFromPdfAsync(pdf, English);
        double ocrMsPerPage = sw.Elapsed.TotalMilliseconds / Pages;

        // Best of three for the fast path: it is short enough for scheduler noise to matter.
        double embeddedMsPerPage = double.MaxValue;
        PdfOcrResult? embedded = null;
        for (int run = 0; run < 3; run++)
        {
            sw.Restart();
            embedded = await ocr.ExtractTextFromPdfAsync(pdf, English, pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.Auto });
            embeddedMsPerPage = Math.Min(embeddedMsPerPage, sw.Elapsed.TotalMilliseconds / Pages);
        }

        // And the searchable-PDF case, where embedded pages still render for the page image.
        sw.Restart();
        await ocr.CreateSearchablePdfAsync(pdf, English, pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.Auto });
        double searchableMsPerPage = sw.Elapsed.TotalMilliseconds / Pages;

        output.WriteLine($"OCR (200 DPI):                 {ocrMsPerPage,9:F1} ms/page");
        output.WriteLine($"Embedded text (Auto):          {embeddedMsPerPage,9:F1} ms/page  ({ocrMsPerPage / embeddedMsPerPage:F0}x faster)");
        output.WriteLine($"Searchable PDF, embedded pages:{searchableMsPerPage,9:F1} ms/page");

        Assert.All(embedded!.Pages, p => Assert.Equal(PdfPageSource.EmbeddedText, p.Source));
        Assert.All(recognized.Pages, p => Assert.Equal(PdfPageSource.Ocr, p.Source));
        Assert.True(embeddedMsPerPage * 5 < ocrMsPerPage,
            $"Embedded text took {embeddedMsPerPage:F1} ms/page against {ocrMsPerPage:F1} ms/page for OCR.");
    }

    [Fact]
    public async Task A_mixed_searchable_pdf_carries_each_pages_text_once()
    {
        using var sample = Sample();
        // The scan page takes the image's own proportions (as a 150 DPI scan), so nothing is stretched.
        var source = new BornDigitalPdf()
            .AddTextPage(PdfEmbeddedTextIntegrationTests.Report)
            .AddPage(sample.Width * 72.0 / 150, sample.Height * 72.0 / 150, Array.Empty<BornDigitalPdf.TextRun>(), sample)
            .Build();

        await using var ocr = new EasyOcrService();
        var (result, searchable) = await ocr.CreateSearchablePdfAsync(source, English,
            pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.Auto });

        Assert.Equal(new[] { PdfPageSource.EmbeddedText, PdfPageSource.Ocr }, result.Pages.Select(p => p.Source));
        Assert.Contains("HELLO", TestAssets.Normalize(result.Pages[1].Ocr.FullText));

        var reread = await ocr.ExtractTextFromPdfAsync(searchable, English,
            pdfOptions: new PdfOcrOptions { TextLayer = PdfTextLayerMode.PreferEmbedded });
        Assert.Equal(ReportText, reread.Pages[0].Ocr.FullText);
        Assert.Equal(1, Occurrences(TestAssets.Normalize(reread.FullText), "ZANZIBAR"));
        Assert.Contains("HELLO", TestAssets.Normalize(reread.Pages[1].Ocr.FullText));
    }

    [SkippableFact]
    public async Task Real_fixture_pages_read_from_their_text_layer_agree_with_ocr_of_the_page()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "assets", "pdf");
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.pdf") : Array.Empty<string>();
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        Skip.If(files.Length == 0, "No PDF fixtures in test/assets/pdf/.");

        await using var ocr = new EasyOcrService();
        int compared = 0;
        foreach (var file in files)
        {
            // The first two pages are plenty, and breaking out of the stream stops the rest from rendering.
            var embedded = await FirstPagesAsync(ocr, file, PdfTextLayerMode.Auto, 2);
            if (embedded.All(p => p.Source != PdfPageSource.EmbeddedText))
            {
                output.WriteLine($"{Path.GetFileName(file)}: no born-digital page among the first two (OCR'd).");
                continue;
            }

            // The reference reading is PP-OCRv5: the default EasyOCR pipeline merges three body lines of
            // Monthly_Report_With_Image.pdf into one unreadable box (a known line-grouping weakness, unchanged
            // since 3.1.1), which would make a correct text layer look hidden.
            var recognized = await FirstPagesAsync(ocr, file, PdfTextLayerMode.Ignore, 2,
                new RecognitionOptions { Engine = RecognitionEngine.PpOcrV5 });
            foreach (var page in embedded.Where(p => p.Source == PdfPageSource.EmbeddedText))
            {
                var ocrPage = recognized.Single(p => p.PageNumber == page.PageNumber);
                double recall = TokenRecall(page.Ocr.FullText, ocrPage.Ocr.FullText, out int tokens);
                output.WriteLine($"{Path.GetFileName(file)} p{page.PageNumber}: {tokens} text-layer words, {recall:P0} also found by OCR " +
                                 $"({page.Ocr.Duration.TotalMilliseconds:F1} ms vs {ocrPage.Ocr.Duration.TotalMilliseconds:F0} ms).");
                if (tokens < 10) continue;

                compared++;
                Assert.True(recall >= 0.5,
                    $"{Path.GetFileName(file)} page {page.PageNumber}: only {recall:P0} of the text layer's words are visible to OCR — " +
                    "the layer may be hidden, stale or mis-ordered, and should not have passed for the page's content.");
            }
        }

        Skip.If(compared == 0, "No fixture page was read from its embedded text layer.");
    }

    private static async Task<List<PdfPageResult>> FirstPagesAsync(
        IEasyOcrService ocr, string file, PdfTextLayerMode mode, int count, RecognitionOptions? options = null)
    {
        var pages = new List<PdfPageResult>();
        await foreach (var page in ocr.ExtractTextFromPdfPagesAsync(file, English, options, new PdfOcrOptions { TextLayer = mode }))
        {
            pages.Add(page);
            if (pages.Count == count) break;
        }
        return pages;
    }

    /// <summary>
    /// Share of the text layer's distinct ASCII words (4+ letters/digits, upper-cased) that OCR also produced.
    /// ASCII only, so a second script that English OCR cannot read does not count against the layer.
    /// </summary>
    private static double TokenRecall(string layer, string recognized, out int tokens)
    {
        static HashSet<string> Words(string text)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (char ch in text + " ")
            {
                if (ch < 128 && char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToUpperInvariant(ch));
                    continue;
                }
                if (sb.Length >= 4) set.Add(sb.ToString());
                sb.Clear();
            }
            return set;
        }

        var expected = Words(layer);
        var seen = Words(recognized);
        tokens = expected.Count;
        return expected.Count == 0 ? 1.0 : (double)expected.Count(seen.Contains) / expected.Count;
    }

    private static int Occurrences(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
