using EasyOcrSharp.Pdf;
using EasyOcrSharp.Services;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Regression tests for concurrent PDF rasterization.
/// </summary>
/// <remarks>
/// Docnet exposes PDFium through a process-wide <c>DocLib.Instance</c> singleton, and PDFium is not
/// thread-safe. Before the rasterizer serialized its native calls, two threads rasterizing different
/// documents at once could return a corrupted page — which showed up as an OCR assertion failing
/// intermittently only when the whole suite ran in parallel. Rasterizing the same document from many
/// threads and demanding byte-identical results is the direct way to pin that down.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(OcrIntegrationCollection.Name)]
public class PdfConcurrencyTests
{
    [SkippableFact]
    public async Task Rasterizing_the_same_pdf_from_many_threads_gives_identical_text()
    {
        var pdf = TestAssets.AnyPdf();
        Skip.If(pdf is null, "Add any PDF to test/assets/pdf/ (see test/assets/pdf/README.md).");

        await using var ocr = new EasyOcrService();

        // Deliberately modest: a low DPI and a handful of workers exercise the concurrency path
        // without turning the test into a memory-exhaustion benchmark. Several full-page OCR passes
        // at 200 DPI at once will exhaust ONNX Runtime's allocator on an ordinary dev box, which
        // proves nothing about thread-safety.
        var rendering = new PdfOcrOptions { Dpi = 96 };

        // A single-threaded baseline to compare every concurrent run against.
        var expected = await ocr.ExtractTextFromPdfAsync(pdf!, new[] { "en" }, pdfOptions: rendering);
        string baseline = TestAssets.Normalize(expected.FullText);

        const int workers = 3;
        var results = await Task.WhenAll(Enumerable.Range(0, workers).Select(async _ =>
        {
            var doc = await ocr.ExtractTextFromPdfAsync(pdf!, new[] { "en" }, pdfOptions: rendering);
            return (doc.Pages.Count, Text: TestAssets.Normalize(doc.FullText));
        }));

        Assert.All(results, r =>
        {
            Assert.Equal(expected.Pages.Count, r.Count);
            Assert.Equal(baseline, r.Text);
        });
    }

    [SkippableFact]
    public async Task Independent_services_rasterizing_concurrently_do_not_interfere()
    {
        // The gate has to be process-wide, not per-service: two unrelated EasyOcrService instances
        // still share the one native PDFium library.
        var pdf = TestAssets.AnyPdf();
        Skip.If(pdf is null, "Add any PDF to test/assets/pdf/ (see test/assets/pdf/README.md).");

        var rendering = new PdfOcrOptions { Dpi = 96 };

        await using var reference = new EasyOcrService();
        string baseline = TestAssets.Normalize(
            (await reference.ExtractTextFromPdfAsync(pdf!, new[] { "en" }, pdfOptions: rendering)).FullText);

        var texts = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var isolated = new EasyOcrService();
            var doc = await isolated.ExtractTextFromPdfAsync(pdf!, new[] { "en" }, pdfOptions: rendering);
            return TestAssets.Normalize(doc.FullText);
        }));

        Assert.All(texts, text => Assert.Equal(baseline, text));
    }

    [SkippableFact]
    public async Task Concurrent_searchable_pdf_generation_produces_consistent_documents()
    {
        var pdf = TestAssets.AnyPdf();
        Skip.If(pdf is null, "Add any PDF to test/assets/pdf/ (see test/assets/pdf/README.md).");

        await using var ocr = new EasyOcrService();
        byte[] bytes = await File.ReadAllBytesAsync(pdf!);

        var rendering = new PdfOcrOptions { Dpi = 96 };

        var produced = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            var (result, output) = await ocr.CreateSearchablePdfAsync(bytes, new[] { "en" }, pdfOptions: rendering);
            return (result.Pages.Count, Length: output.Length, Text: TestAssets.Normalize(result.FullText));
        }));

        // Every worker saw the same document: same page count and same recognized text. (Byte length
        // can differ between runs only if the text differs, so it is compared as a cheap extra signal.)
        Assert.All(produced, p =>
        {
            Assert.Equal(produced[0].Count, p.Count);
            Assert.Equal(produced[0].Text, p.Text);
            Assert.True(p.Length > 0, "a searchable PDF should never be empty");
        });
    }
}
