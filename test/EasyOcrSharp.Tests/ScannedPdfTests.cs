using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Pdf.Internal;
using EasyOcrSharp.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Scanned-image PDF tests — the most common real-world OCR input. A "scanned" PDF is one whose pages
/// are raster images with no text layer. We synthesize one in-process by embedding a raster image
/// (<c>sample.png</c>) as image-only PDF pages, then OCR it through the real pipeline. A skippable test
/// also covers a real scanned PDF you drop into <c>test/assets/pdf/</c>.
/// </summary>
[Trait("Category", "Integration")]
public class ScannedPdfTests
{
    private static string Sample()
    {
        var path = TestAssets.Image("sample.png");
        Assert.True(path is not null, "assets/sample.png not found — ensure it is copied to the test output.");
        return path!;
    }

    /// <summary>
    /// Builds an image-only PDF (pages are pure images, no text layer) — i.e. a synthetic scan.
    /// Uses the searchable-PDF writer with an empty OCR layer so only the page image is embedded.
    /// </summary>
    private static byte[] BuildScannedPdf(string imagePath, int pages, int dpi = 150)
    {
        using var img = Image.Load<Rgb24>(imagePath);
        var builder = new SearchablePdfBuilder();
        for (int i = 0; i < pages; i++)
        {
            builder.AddPage(img, OcrResult.Empty, dpi, jpegQuality: 85);
        }
        return builder.Build();
    }

    [Fact]
    public async Task Scanned_single_page_pdf_is_ocred()
    {
        var pdf = BuildScannedPdf(Sample(), pages: 1);

        await using var ocr = new EasyOcrService();
        var doc = await ocr.ExtractTextFromPdfAsync(pdf, new[] { "en" });

        Assert.Single(doc.Pages);
        Assert.Contains("HELLO", TestAssets.Normalize(doc.FullText));
        Assert.Contains("EASYOCRSHARP", TestAssets.Normalize(doc.FullText));
    }

    [Fact]
    public async Task Scanned_multipage_pdf_ocrs_every_page()
    {
        var pdf = BuildScannedPdf(Sample(), pages: 3);

        await using var ocr = new EasyOcrService();
        var doc = await ocr.ExtractTextFromPdfAsync(pdf, new[] { "en" });

        Assert.Equal(3, doc.Pages.Count);
        Assert.All(doc.Pages, p => Assert.Contains("HELLO", TestAssets.Normalize(p.Ocr.FullText)));
    }

    [Fact]
    public async Task Scanned_pdf_converted_to_searchable_pdf_roundtrips()
    {
        var scanned = BuildScannedPdf(Sample(), pages: 2);

        await using var ocr = new EasyOcrService();
        var (result, searchable) = await ocr.CreateSearchablePdfAsync(scanned, new[] { "en" });

        Assert.Contains("HELLO", TestAssets.Normalize(result.FullText));
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(searchable, 0, 4));

        // The generated searchable PDF still OCRs back to the same text.
        var reread = await ocr.ExtractTextFromPdfAsync(searchable, new[] { "en" });
        Assert.Contains("HELLO", TestAssets.Normalize(reread.FullText));
    }

    [SkippableFact]
    public async Task Real_multipage_scanned_document_ocrs_every_page_without_crashing()
    {
        // A real, noisy 8-page government scan (forms, stamps, handwriting). This previously crashed
        // ONNX Runtime on a thin detection box — see CrnnPreprocessTests for the regression guard.
        var pdf = TestAssets.PdfMatching("water", "mailing", "public");
        Skip.If(pdf is null, "Add a real scanned PDF (e.g. PublicWaterMassMailing.pdf) to test/assets/pdf/ — see FIXTURES.md.");

        await using var ocr = new EasyOcrService();
        var doc = await ocr.ExtractTextFromPdfAsync(pdf!, new[] { "en" });

        // Every page must process (no exception) and the document must yield real text.
        Assert.True(doc.Pages.Count >= 2, $"Expected a multi-page scan, got {doc.Pages.Count}.");
        var norm = TestAssets.Normalize(doc.FullText);
        Assert.Contains("SAMPLE", norm);
        Assert.Contains("PUBLIC", norm);
    }
}
