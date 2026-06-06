using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Services;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Complex, production-shaped PDF documents you generate (see test/assets/pdf/README.md for the exact
/// generation prompts and marker tokens). Each test is <see cref="SkippableFactAttribute"/> so it
/// skips cleanly until the fixture is dropped into <c>test/assets/pdf/</c>.
/// </summary>
[Trait("Category", "Integration")]
public class ComplexDocumentTests
{
    // ---- The converted README: a real, long, multi-section document ----

    [SkippableFact]
    public async Task Readme_pdf_extracts_substantial_document_text()
    {
        // Any README exported to PDF works (main project README or the fixtures guide); the point is to
        // prove a long, real, multi-section document OCRs into a large amount of correct text.
        var pdf = TestAssets.PdfMatching("readme");
        Skip.If(pdf is null, "Add a readme PDF to test/assets/pdf/ (export a README.md to PDF).");

        await using var ocr = new EasyOcrService();
        var doc = await ocr.ExtractTextFromPdfAsync(pdf!, new[] { "en" });

        Assert.True(doc.Pages.Count >= 1);
        var norm = TestAssets.Normalize(doc.FullText);
        Assert.True(norm.Length > 300, $"Expected substantial extracted text, got {norm.Length} chars.");
        // These docs are about OCR/PDF — at least one of these distinctive words must survive.
        Assert.True(
            norm.Contains("PDF") || norm.Contains("OCR") || norm.Contains("EASYOCRSHARP"),
            "Expected a recognizable keyword from the document.");
    }

    // ---- Invoice: table layout + digit-only field extraction ----

    [SkippableFact]
    public async Task Invoice_pdf_extracts_heading_number_and_total()
    {
        var pdf = TestAssets.PdfMatching("invoice");
        Skip.If(pdf is null, "Add an invoice PDF to test/assets/pdf/ (see prompt in FIXTURES.md).");

        await using var ocr = new EasyOcrService();

        var full = await ocr.ExtractTextFromPdfAsync(pdf!, new[] { "en" });
        var norm = TestAssets.Normalize(full.FullText);
        Assert.Contains("INVOICE", norm);
        Assert.Contains("778899", norm); // invoice number marker

        // Digit allowlist should isolate the numeric fields (e.g. the grand total 4821).
        var digits = await ocr.ExtractTextFromPdfAsync(pdf!, new[] { "en" },
            new RecognitionOptions { Allowlist = "0123456789" });
        Assert.Contains("4821", TestAssets.Normalize(digits.FullText));
    }

    // ---- Two-column article: reading order / grouping under a complex layout ----

    [SkippableFact]
    public async Task Two_column_pdf_reads_both_columns()
    {
        var pdf = TestAssets.PdfMatching("two_column", "column", "quantum", "harvest");
        Skip.If(pdf is null, "Add a two-column PDF to test/assets/pdf/ (see prompt in FIXTURES.md).");

        await using var ocr = new EasyOcrService();
        var doc = await ocr.ExtractTextFromPdfAsync(pdf!, new[] { "en" });

        // Multi-column CONTENT is extracted (the spanning title + body text are read).
        // NOTE: side-by-side columns sharing a horizontal band are read left→right (interleaved),
        // not column-by-column — a known reading-order limitation for multi-column layouts.
        var norm = TestAssets.Normalize(doc.FullText);
        Assert.Contains("QUANTUM", norm);
        Assert.Contains("HARVEST", norm);
        Assert.True(norm.Length > 150, $"Expected the article body to be extracted, got {norm.Length} chars.");
    }

    // ---- Mixed-language document: multiple script packs in one page ----

    [SkippableFact]
    public async Task Multilang_pdf_reads_english_and_russian()
    {
        var pdf = TestAssets.PdfMatching("multilang", "bilingual", "welcome");
        Skip.If(pdf is null, "Add a bilingual PDF to test/assets/pdf/ (see prompt in FIXTURES.md).");

        await using var ocr = new EasyOcrService();
        var doc = await ocr.ExtractTextFromPdfAsync(pdf!, new[] { "en", "ru" });

        // Both scripts are recognized. The English reads cleanly; the Cyrillic is recognized but with
        // Latin/Cyrillic homoglyph confusion (О↔0, Р↔₽) that is inherent when both packs are active —
        // so we assert distinct Cyrillic letters that have no Latin look-alike (Б, Ж).
        var norm = TestAssets.Normalize(doc.FullText);
        Assert.Contains("WELCOME", norm);
        Assert.Contains("GUESTS", norm);
        Assert.Contains("Б", norm);
        Assert.Contains("Ж", norm);
    }
}
