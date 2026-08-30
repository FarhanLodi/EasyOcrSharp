using System.Diagnostics.Metrics;
using System.Text;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyOcrSharp.Diagnostics;
using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Pdf.Internal;
using EasyOcrSharp.Services;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// The composite OCR paths — PDF, searchable PDF, multi-frame containers and batches — used to emit no
/// counter of their own, so a deployment that only ever OCRs PDFs registered zero operations and looked
/// idle on a dashboard whether it was healthy or on fire. These tests lock in that every one of them now
/// records an <c>easyocr.operations</c> data point with an honest outcome, and that
/// <c>easyocr.pages</c> counts pages actually processed rather than pages the input claimed to hold.
/// </summary>
/// <remarks>
/// Everything here runs offline. The failure cases fail inside PDFium before a model is ever needed, and
/// the success cases drive a stub <see cref="IEasyOcrService"/>, so page counting is verified without
/// downloading a recognizer. The class joins the model-backed collection anyway so it cannot run beside
/// the other PDF classes and see their measurements.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(OcrIntegrationCollection.Name)]
public class CompositeOperationMetricsTests
{
    private static readonly string[] English = { "en" };

    // ---- PDF: the failure path is instrumented ----

    [Fact]
    public async Task A_corrupt_pdf_records_a_failed_pdf_operation()
    {
        // Nothing recognizes anything here: PDFium rejects the document, so this proves the *failure* path
        // emits a data point. A recorder that only counted on the happy path would leave a service failing
        // every single request indistinguishable from one receiving no traffic at all.
        var garbage = Encoding.ASCII.GetBytes("%PDF-1.7\nthis is not a real pdf at all\n%%EOF");

        using var metrics = new MetricCollector();
        await using var ocr = new EasyOcrService();

        await Assert.ThrowsAsync<PdfProcessingException>(
            () => ocr.ExtractTextFromPdfAsync(garbage, English));

        var operation = Assert.Single(metrics.Points("easyocr.operations", "pdf"));
        Assert.Equal(1, operation.Value);
        Assert.Equal(EasyOcrDiagnostics.Outcomes.Error, operation.Outcome);
        Assert.Equal(typeof(PdfProcessingException).FullName, operation.ErrorType);

        // The duration must be tagged identically, or the latency panel cannot be sliced by outcome.
        var duration = Assert.Single(metrics.Points("easyocr.duration", "pdf"));
        Assert.Equal(EasyOcrDiagnostics.Outcomes.Error, duration.Outcome);

        // No page was rasterized, let alone OCRed, so nothing may be claimed as processed.
        Assert.Empty(metrics.Points("easyocr.pages", "pdf"));
    }

    [Fact]
    public async Task A_corrupt_pdf_records_a_failed_searchable_pdf_operation()
    {
        // Searchable-PDF generation is tagged separately from plain PDF OCR: it also encodes page images
        // and lays out a text layer, so averaging the two hides which half is slow.
        var garbage = Encoding.ASCII.GetBytes("%PDF-1.7 broken %%EOF");

        using var metrics = new MetricCollector();
        await using var ocr = new EasyOcrService();

        await Assert.ThrowsAsync<PdfProcessingException>(
            () => ocr.CreateSearchablePdfAsync(garbage, English));

        var operation = Assert.Single(metrics.Points("easyocr.operations", "pdf_searchable"));
        Assert.Equal(EasyOcrDiagnostics.Outcomes.Error, operation.Outcome);
        Assert.Equal(typeof(PdfProcessingException).FullName, operation.ErrorType);
        Assert.Empty(metrics.Points("easyocr.operations", "pdf"));
    }

    // ---- PDF: pages counted as they complete ----

    [Fact]
    public async Task A_successful_pdf_run_records_one_page_per_page()
    {
        using var metrics = new MetricCollector();
        await using var stub = new StubOcrService(linesPerImage: 3);

        var result = await stub.ExtractTextFromPdfAsync(BuildScannedPdf(pages: 4), English);

        Assert.Equal(4, result.Pages.Count);

        var operation = Assert.Single(metrics.Points("easyocr.operations", "pdf"));
        Assert.Equal(EasyOcrDiagnostics.Outcomes.Success, operation.Outcome);
        Assert.Null(operation.ErrorType);

        var pages = Assert.Single(metrics.Points("easyocr.pages", "pdf"));
        Assert.Equal(4, pages.Value);

        var lines = Assert.Single(metrics.Points("easyocr.lines", "pdf"));
        Assert.Equal(12, lines.Value);
    }

    [Fact]
    public async Task A_pdf_that_fails_part_way_records_only_the_pages_it_finished()
    {
        // The whole point of counting per completed page: a document that dies on page three must report
        // the two pages it really OCRed. Counting the page count up front would credit a failed job with a
        // full document of work, so the pages/sec panel would read highest exactly when OCR is broken.
        using var metrics = new MetricCollector();
        await using var stub = new StubOcrService(linesPerImage: 3, failOnCall: 3);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => stub.ExtractTextFromPdfAsync(BuildScannedPdf(pages: 6), English));

        var operation = Assert.Single(metrics.Points("easyocr.operations", "pdf"));
        Assert.Equal(EasyOcrDiagnostics.Outcomes.Error, operation.Outcome);
        Assert.Equal(typeof(InvalidOperationException).FullName, operation.ErrorType);

        var pages = Assert.Single(metrics.Points("easyocr.pages", "pdf"));
        Assert.Equal(2, pages.Value);
    }

    [SkippableFact]
    public async Task A_real_fixture_pdf_records_a_page_per_page()
    {
        var path = TestAssets.AnyPdf();
        Skip.If(path is null, "Add any PDF to test/assets/pdf/ — see FIXTURES.md.");

        // Driven by the stub, so this exercises real PDFium rasterization of a genuine document without
        // needing a recognizer model or the network.
        using var metrics = new MetricCollector();
        await using var stub = new StubOcrService(linesPerImage: 1);

        var result = await stub.ExtractTextFromPdfAsync(await File.ReadAllBytesAsync(path!), English);

        var pages = Assert.Single(metrics.Points("easyocr.pages", "pdf"));
        Assert.Equal(result.Pages.Count, pages.Value);
        Assert.Equal(EasyOcrDiagnostics.Outcomes.Success, pages.Outcome);
    }

    // ---- batch ----

    [Fact]
    public async Task A_batch_containing_a_missing_file_still_records_one_successful_batch()
    {
        // A per-image failure is reported through OcrBatchResult.Error rather than thrown — that is the
        // documented contract — so the batch itself succeeded. The missing file never reached OCR, so it
        // must not be counted as a page processed.
        using var metrics = new MetricCollector();
        await using var ocr = new EasyOcrService();

        var missing = Path.Combine(Path.GetTempPath(), $"easyocrsharp-absent-{Guid.NewGuid():N}.png");
        var results = new List<OcrBatchResult>();
        await foreach (var item in ocr.ExtractTextFromImagesAsync(new[] { missing }, English))
        {
            results.Add(item);
        }

        var failure = Assert.Single(results);
        Assert.False(failure.Succeeded);
        Assert.IsType<FileNotFoundException>(failure.Error);

        var operation = Assert.Single(metrics.Points("easyocr.operations", "batch"));
        Assert.Equal(EasyOcrDiagnostics.Outcomes.Success, operation.Outcome);
        Assert.Empty(metrics.Points("easyocr.pages", "batch"));
    }

    [Fact]
    public async Task A_batch_records_one_operation_for_the_whole_run_not_one_per_image()
    {
        using var metrics = new MetricCollector();
        await using var stub = new StubOcrService(linesPerImage: 2);

        var paths = new[] { "a.png", "b.png", "c.png", "d.png", "e.png" };
        var results = new List<OcrBatchResult>();
        await foreach (var item in stub.ExtractTextFromImagesAsync(paths, English, maxConcurrency: 2))
        {
            results.Add(item);
        }

        Assert.Equal(5, results.Count);

        // One data point for the batch — the five images are counted by the single-image API instead.
        var operation = Assert.Single(metrics.Points("easyocr.operations", "batch"));
        Assert.Equal(EasyOcrDiagnostics.Outcomes.Success, operation.Outcome);

        var pages = Assert.Single(metrics.Points("easyocr.pages", "batch"));
        Assert.Equal(5, pages.Value);

        var lines = Assert.Single(metrics.Points("easyocr.lines", "batch"));
        Assert.Equal(10, lines.Value);
    }

    [Fact]
    public async Task A_batch_abandoned_by_its_consumer_is_canceled_not_an_error()
    {
        // Walking away after the first result is a caller's choice, not a fault. Recording it as an error
        // would let one such caller poison the error rate for the whole service.
        using var metrics = new MetricCollector();
        await using var stub = new StubOcrService(linesPerImage: 1);

        var paths = new[] { "a.png", "b.png", "c.png", "d.png" };
        await foreach (var item in stub.ExtractTextFromImagesAsync(paths, English, maxConcurrency: 1))
        {
            Assert.NotNull(item);
            break;
        }

        var operation = Assert.Single(metrics.Points("easyocr.operations", "batch"));
        Assert.Equal(EasyOcrDiagnostics.Outcomes.Canceled, operation.Outcome);
    }

    [Fact]
    public async Task A_cancelled_batch_still_records_a_data_point_and_still_throws()
    {
        // The recording lives in a finally that also awaits the pump, so this guards both halves: the
        // caller must still see the cancellation, and the batch must not vanish from the metrics.
        using var metrics = new MetricCollector();
        await using var stub = new StubOcrService(linesPerImage: 1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in stub.ExtractTextFromImagesAsync(
                new[] { "a.png", "b.png" }, English, cancellationToken: cts.Token))
            {
                Assert.NotNull(item);
            }
        });

        var operation = Assert.Single(metrics.Points("easyocr.operations", "batch"));
        Assert.Equal(EasyOcrDiagnostics.Outcomes.Canceled, operation.Outcome);
    }

    // ---- multi-frame ----

    [Fact]
    public async Task A_multi_frame_run_records_one_operation_with_a_page_per_frame()
    {
        using var metrics = new MetricCollector();
        await using var stub = new StubOcrService(linesPerImage: 2);
        using var image = BuildMultiFrameImage(frameCount: 5);

        var result = await stub.ExtractTextFromFramesAsync(image, English);

        Assert.Equal(5, result.Frames.Count);

        // MultiFrameTiffTests is not in this collection and can run alongside, so match on the exact tag
        // set and value rather than assuming this listener saw nothing else.
        Assert.Contains(metrics.Points("easyocr.pages", "multi_frame"),
            p => p.Value == 5 && p.Outcome == EasyOcrDiagnostics.Outcomes.Success);
        Assert.Contains(metrics.Points("easyocr.lines", "multi_frame"),
            p => p.Value == 10 && p.Outcome == EasyOcrDiagnostics.Outcomes.Success);
    }

    [Fact]
    public async Task Abandoning_a_frame_stream_is_canceled_and_keeps_the_frames_it_produced()
    {
        using var metrics = new MetricCollector();
        await using var stub = new StubOcrService(linesPerImage: 2);
        using var image = BuildMultiFrameImage(frameCount: 6);

        int seen = 0;
        await foreach (var frame in stub.StreamTextFromFramesAsync(image, English))
        {
            Assert.NotNull(frame);
            if (++seen == 2) break;
        }

        Assert.Contains(metrics.Points("easyocr.operations", "multi_frame"),
            p => p.Outcome == EasyOcrDiagnostics.Outcomes.Canceled);
        Assert.Contains(metrics.Points("easyocr.pages", "multi_frame"),
            p => p.Value == 2 && p.Outcome == EasyOcrDiagnostics.Outcomes.Canceled);
    }

    [Fact]
    public async Task A_frame_that_fails_leaves_the_earlier_frames_counted_and_the_outcome_an_error()
    {
        using var metrics = new MetricCollector();
        await using var stub = new StubOcrService(linesPerImage: 2, failOnCall: 4);
        using var image = BuildMultiFrameImage(frameCount: 6);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => stub.ExtractTextFromFramesAsync(image, English));

        Assert.Contains(metrics.Points("easyocr.operations", "multi_frame"),
            p => p.Outcome == EasyOcrDiagnostics.Outcomes.Error
                 && p.ErrorType == typeof(InvalidOperationException).FullName);
        Assert.Contains(metrics.Points("easyocr.pages", "multi_frame"),
            p => p.Value == 3 && p.Outcome == EasyOcrDiagnostics.Outcomes.Error);
    }

    // ---- fixtures ----

    /// <summary>Builds a real, openable multi-page PDF of blank scanned pages — no fixture file needed.</summary>
    private static byte[] BuildScannedPdf(int pages)
    {
        using var page = new Image<Rgb24>(200, 80, new Rgb24(255, 255, 255));
        var builder = new SearchablePdfBuilder();
        for (int i = 0; i < pages; i++) builder.AddPage(page, OcrResult.Empty, 150, 85);
        return builder.Build();
    }

    private static Image<Rgb24> BuildMultiFrameImage(int frameCount)
    {
        var image = new Image<Rgb24>(24, 16, new Rgb24(255, 255, 255));
        for (int i = 1; i < frameCount; i++)
        {
            using var extra = new Image<Rgb24>(24, 16, new Rgb24(255, 255, 255));
            image.Frames.AddFrame(extra.Frames.RootFrame);
        }
        return image;
    }

    /// <summary>
    /// Minimal <see cref="IEasyOcrService"/> returning a fixed number of lines per image, optionally
    /// throwing on the n-th call so a mid-document failure can be driven deterministically. Using a stub
    /// keeps every page-counting assertion offline and free of recognizer variance.
    /// </summary>
    private sealed class StubOcrService(int linesPerImage = 1, int failOnCall = 0) : IEasyOcrService
    {
        private int _calls;

        private OcrResult Recognize(int width, int height)
        {
            if (failOnCall > 0 && Interlocked.Increment(ref _calls) == failOnCall)
                throw new InvalidOperationException("stub failure");

            var lines = Enumerable.Range(0, linesPerImage)
                .Select(i => new OcrLine { Text = $"line-{i}", Confidence = 1.0 })
                .ToArray();

            return new OcrResult
            {
                FullText = string.Join("\n", lines.Select(l => l.Text)),
                Lines = lines,
                Languages = English,
                SourceWidth = width,
                SourceHeight = height,
            };
        }

        public Task<OcrResult> ExtractTextFromImage(
            Image<Rgb24> image, IEnumerable<string> languages, RecognitionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Recognize(image.Width, image.Height));
        }

        public Task<OcrResult> ExtractTextFromImage(
            string imagePath, IEnumerable<string> languages, RecognitionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Recognize(64, 64));
        }

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

    /// <summary>One recorded measurement, flattened to the tags these tests assert on.</summary>
    private sealed record MetricPoint(string Instrument, double Value, string? Operation, string? Outcome, string? ErrorType, string? Provider);

    /// <summary>
    /// Captures every measurement published on the library's public meter for the lifetime of the test.
    /// Subscribing the same way a real deployment does — by <see cref="EasyOcrDiagnostics.MeterName"/> —
    /// is the point: it proves the instruments are reachable from outside the assembly, not merely called.
    /// </summary>
    private sealed class MetricCollector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<MetricPoint> _points = new();
        private readonly Lock _gate = new();

        public MetricCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == EasyOcrDiagnostics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            // easyocr.duration is a Histogram<double>; the counters are long. Both callbacks are needed or
            // the histogram measurements are silently dropped.
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
            _listener.Start();
        }

        private void Record(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? operation = null, outcome = null, errorType = null, provider = null;
            foreach (var tag in tags)
            {
                var text = tag.Value?.ToString();
                switch (tag.Key)
                {
                    case EasyOcrDiagnostics.TagNames.Operation: operation = text; break;
                    case EasyOcrDiagnostics.TagNames.Outcome: outcome = text; break;
                    case EasyOcrDiagnostics.TagNames.ErrorType: errorType = text; break;
                    case EasyOcrDiagnostics.TagNames.Provider: provider = text; break;
                }
            }

            // Measurement callbacks fire on whichever thread did the work, and the batch path completes its
            // images on pool threads, so the list needs the lock.
            lock (_gate)
            {
                _points.Add(new MetricPoint(instrument, value, operation, outcome, errorType, provider));
            }
        }

        /// <summary>Every measurement seen so far for one instrument and one operation tag.</summary>
        public IReadOnlyList<MetricPoint> Points(string instrument, string operation)
        {
            lock (_gate)
            {
                return _points
                    .Where(p => p.Instrument == instrument && p.Operation == operation)
                    .ToArray();
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
