using System.Text;
using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Pdf.Internal;
using EasyOcrSharp.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Negative/robustness tests for the PDF input path: hostile or broken inputs must fail with a clear,
/// typed <see cref="EasyOcrSharpException"/> — never a raw PDFium/Docnet exception leaking to the caller.
/// These need no models (they fail before OCR), so they run fast and offline.
/// </summary>
[Trait("Category", "Integration")]
public class PdfRobustnessTests
{
    private static EasyOcrService NewService() => new();

    private static byte[] BuildValidScannedPdf(int pages = 2)
    {
        using var img = new Image<Rgb24>(200, 80, new Rgb24(255, 255, 255));
        var builder = new SearchablePdfBuilder();
        for (int i = 0; i < pages; i++) builder.AddPage(img, OcrResult.Empty, 150, 85);
        return builder.Build();
    }

    [Fact]
    public async Task Corrupt_pdf_throws_typed_exception_not_raw_docnet()
    {
        var garbage = Encoding.ASCII.GetBytes("%PDF-1.7\nthis is not a real pdf at all\n%%EOF");
        await using var ocr = NewService();

        var ex = await Assert.ThrowsAsync<EasyOcrSharpException>(
            () => ocr.ExtractTextFromPdfAsync(garbage, new[] { "en" }));

        Assert.Contains("PDF", ex.Message);
        Assert.NotNull(ex.InnerException); // the underlying PDFium/Docnet cause is preserved
    }

    [Fact]
    public async Task Not_a_pdf_at_all_throws_typed_exception()
    {
        var notPdf = Encoding.UTF8.GetBytes("just some plain text, definitely not a PDF document");
        await using var ocr = NewService();

        await Assert.ThrowsAsync<EasyOcrSharpException>(
            () => ocr.ExtractTextFromPdfAsync(notPdf, new[] { "en" }));
    }

    [Fact]
    public async Task Truncated_pdf_throws_typed_exception()
    {
        // A real, valid PDF cut in half mid-stream — the classic "partial upload" failure.
        var valid = BuildValidScannedPdf();
        var truncated = valid.AsSpan(0, valid.Length / 2).ToArray();
        await using var ocr = NewService();

        await Assert.ThrowsAsync<EasyOcrSharpException>(
            () => ocr.ExtractTextFromPdfAsync(truncated, new[] { "en" }));
    }

    [Fact]
    public async Task Empty_pdf_bytes_throws_clear_exception()
    {
        await using var ocr = NewService();

        var ex = await Assert.ThrowsAsync<EasyOcrSharpException>(
            () => ocr.ExtractTextFromPdfAsync(Array.Empty<byte>(), new[] { "en" }));

        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Null_pdf_bytes_throws_argument_null()
    {
        await using var ocr = NewService();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ocr.ExtractTextFromPdfAsync((byte[])null!, new[] { "en" }));
    }

    [Fact]
    public async Task Corrupt_pdf_also_fails_cleanly_on_the_searchable_path()
    {
        var garbage = Encoding.ASCII.GetBytes("%PDF-1.7 broken %%EOF");
        await using var ocr = NewService();

        await Assert.ThrowsAsync<EasyOcrSharpException>(
            () => ocr.CreateSearchablePdfAsync(garbage, new[] { "en" }));
    }

    [SkippableFact]
    public async Task Encrypted_pdf_fixture_fails_cleanly()
    {
        // Drop a password-protected PDF named e.g. "encrypted_secret.pdf" into test/assets/pdf/ to exercise
        // the encrypted path. Without one we skip — the corrupt-PDF tests already prove the same catch.
        var pdf = TestAssets.PdfMatching("encrypted", "password", "protected");
        Skip.If(pdf is null, "Add an encrypted/password-protected PDF to test/assets/pdf/ to run this.");

        await using var ocr = NewService();
        await Assert.ThrowsAsync<EasyOcrSharpException>(
            () => ocr.ExtractTextFromPdfAsync(pdf!, new[] { "en" }));
    }
}
