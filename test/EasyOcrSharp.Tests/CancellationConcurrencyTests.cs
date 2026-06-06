using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Pdf.Internal;
using EasyOcrSharp.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Cancellation is honored on long-running calls, and the service is safe to share across concurrent
/// callers (the documented contract: ONNX sessions are reused and thread-safe). A real engine is used;
/// models are downloaded once and cached.
/// </summary>
[Trait("Category", "Integration")]
public class CancellationConcurrencyTests
{
    private static string Sample() => TestAssets.Image("sample.png")
        ?? throw new InvalidOperationException("assets/sample.png missing");

    private static byte[] BuildScannedPdf(int pages)
    {
        using var img = Image.Load<Rgb24>(Sample());
        var builder = new SearchablePdfBuilder();
        for (int i = 0; i < pages; i++) builder.AddPage(img, OcrResult.Empty, 150, 85);
        return builder.Build();
    }

    /// <summary>A synchronous IProgress that records pages and can cancel a token on the Nth page.</summary>
    private sealed class CancelOnPage : IProgress<PdfPageProgress>
    {
        private readonly CancellationTokenSource _cts;
        private readonly int _cancelAt;
        public int Reports { get; private set; }
        public CancelOnPage(CancellationTokenSource cts, int cancelAt) { _cts = cts; _cancelAt = cancelAt; }
        public void Report(PdfPageProgress value)
        {
            Reports++;
            if (value.PageNumber >= _cancelAt) _cts.Cancel();
        }
    }

    [Fact]
    public async Task Pre_canceled_token_stops_image_ocr()
    {
        await using var ocr = new EasyOcrService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ocr.ExtractTextFromImage(Sample(), new[] { "en" }, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Pre_canceled_token_stops_pdf_ocr()
    {
        var pdf = BuildScannedPdf(2);
        await using var ocr = new EasyOcrService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ocr.ExtractTextFromPdfAsync(pdf, new[] { "en" }, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Cancellation_midway_stops_a_multipage_pdf_early()
    {
        var pdf = BuildScannedPdf(3);
        await using var ocr = new EasyOcrService();
        using var cts = new CancellationTokenSource();
        var progress = new CancelOnPage(cts, cancelAt: 1);
        var pdfOptions = new PdfOcrOptions { Progress = progress };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ocr.ExtractTextFromPdfAsync(pdf, new[] { "en" }, pdfOptions: pdfOptions, cancellationToken: cts.Token));

        Assert.True(progress.Reports < 3, $"Expected to stop before the last page, processed {progress.Reports}.");
    }

    [Fact]
    public async Task Concurrent_image_ocr_on_one_service_is_correct()
    {
        await using var ocr = new EasyOcrService();
        var bytes = await File.ReadAllBytesAsync(Sample());

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => ocr.ExtractTextFromImage(bytes, new[] { "en" }))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Contains("HELLO", TestAssets.Normalize(r.FullText)));
    }

    [Fact]
    public async Task Concurrent_pdf_ocr_on_one_service_is_correct()
    {
        var pdf = BuildScannedPdf(1);
        await using var ocr = new EasyOcrService();

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => ocr.ExtractTextFromPdfAsync(pdf, new[] { "en" }))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Contains("HELLO", TestAssets.Normalize(r.FullText)));
    }
}
