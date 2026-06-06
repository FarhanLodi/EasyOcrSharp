using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Pdf.Internal;
using EasyOcrSharp.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// PDFs that mix images and text (the common report/brochure shape). Because the pipeline rasterizes
/// the whole page, text that sits <i>alongside</i> images, and text <i>baked into</i> images, are both
/// recognized. Proven here with a synthesized image+text page, plus a skippable real fixture.
/// </summary>
[Trait("Category", "Integration")]
public class PdfWithImagesTests
{
    private static readonly Rgb24 White = new(255, 255, 255);

    private static string Sample()
    {
        var path = TestAssets.Image("sample.png");
        Assert.True(path is not null, "assets/sample.png not found — ensure it is copied to the test output.");
        return path!;
    }

    /// <summary>A page that looks like a real document: a colourful "photo/figure" band on top, text below.</summary>
    private static Image<Rgb24> ComposeImageAndTextPage(Image<Rgb24> textImage)
    {
        int w = textImage.Width;
        int photoH = textImage.Height;
        var page = new Image<Rgb24>(w, photoH + textImage.Height + 60, White);

        // Simulated embedded image — a colour gradient (no text), like a photo or chart.
        for (int y = 0; y < photoH; y++)
        {
            for (int x = 0; x < w; x++)
            {
                page[x, y] = new Rgb24((byte)(x * 255 / Math.Max(1, w)), (byte)(y * 255 / Math.Max(1, photoH)), 128);
            }
        }

        // Real text underneath, on white.
        page.Mutate(c => c.DrawImage(textImage, new Point(0, photoH + 60), 1f));
        return page;
    }

    private static byte[] BuildImagePdf(Image<Rgb24> page, int dpi = 150)
    {
        var builder = new SearchablePdfBuilder();
        builder.AddPage(page, OcrResult.Empty, dpi, jpegQuality: 85);
        return builder.Build();
    }

    [Fact]
    public async Task Pdf_page_mixing_image_and_text_reads_the_text()
    {
        using var src = await Image.LoadAsync<Rgb24>(Sample());
        using var page = ComposeImageAndTextPage(src);
        var pdf = BuildImagePdf(page);

        await using var ocr = new EasyOcrService();
        var doc = await ocr.ExtractTextFromPdfAsync(pdf, new[] { "en" });

        Assert.Single(doc.Pages);
        Assert.Contains("HELLO", TestAssets.Normalize(doc.FullText));
        Assert.Contains("EASYOCRSHARP", TestAssets.Normalize(doc.FullText));
    }

    [SkippableFact]
    public async Task Real_pdf_with_embedded_images_extracts_text()
    {
        var pdf = TestAssets.PdfMatching("mixed_image", "monthly", "report", "figure");
        Skip.If(pdf is null, "Add a report PDF with an embedded image to test/assets/pdf/ (see prompt in FIXTURES.md).");

        await using var ocr = new EasyOcrService();
        var doc = await ocr.ExtractTextFromPdfAsync(pdf!, new[] { "en" });

        var norm = TestAssets.Normalize(doc.FullText);
        Assert.Contains("MONTHLY", norm); // heading marker
        Assert.Contains("FIGURE", norm);  // image caption marker (text next to an embedded image)
    }

    [SkippableFact]
    public async Task Real_catalogue_pdf_with_multiple_images_ocrs_all_pages()
    {
        // A multi-page product catalogue: every page mixes product photos with captions and prices.
        var pdf = TestAssets.PdfMatching("prince", "catalogue", "catalog");
        Skip.If(pdf is null, "Add a product-catalogue PDF (e.g. PrinceCatalogue.pdf) to test/assets/pdf/ — see FIXTURES.md.");

        await using var ocr = new EasyOcrService();
        var doc = await ocr.ExtractTextFromPdfAsync(pdf!, new[] { "en" });

        Assert.True(doc.Pages.Count >= 2, $"Expected a multi-page catalogue, got {doc.Pages.Count}.");
        var norm = TestAssets.Normalize(doc.FullText);
        Assert.Contains("FURNITURE", norm); // brand text on the page
        Assert.Contains("LOREM", norm);      // product description text beside the images
        Assert.Contains("299", norm);        // a product price ($299)
    }
}
