using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// CI-safe tests for streaming recognition (<c>ExtractTextStreamAsync</c>). They exercise the plumbing —
/// the ordered, bounded-concurrency pump that bridges recognition to <see cref="IAsyncEnumerable{T}"/>,
/// the reading-order pass over detected regions, and the interface's default implementation — with
/// synthetic work items, so no model is ever downloaded and no network is touched.
/// </summary>
public class StreamingTests
{
    // ---- ordered streaming pump ----

    [Fact]
    public async Task YieldsEachResultAsSoonAsItIsReadyRatherThanAtTheEnd()
    {
        var gates = Gates(3);
        var stream = OrderedStreamPump.RunOrderedAsync<int, int>(
            new[] { 0, 1, 2 },
            async (i, ct) => { await gates[i].Task.WaitAsync(ct); return new[] { i }; },
            maxConcurrency: 3);

        await using var enumerator = stream.GetAsyncEnumerator();

        var first = enumerator.MoveNextAsync();
        Assert.False(first.IsCompleted); // nothing recognized yet, so nothing has been emitted

        gates[0].SetResult();
        Assert.True(await first);
        Assert.Equal(0, enumerator.Current);

        // Items 1 and 2 are still in flight: the consumer already has a usable line. That is the point.
        gates[1].SetResult();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(1, enumerator.Current);

        gates[2].SetResult();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(2, enumerator.Current);

        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task PreservesInputOrderWhenItemsCompleteOutOfOrder()
    {
        var gates = Gates(4);
        var stream = OrderedStreamPump.RunOrderedAsync<int, int>(
            new[] { 0, 1, 2, 3 },
            async (i, ct) => { await gates[i].Task.WaitAsync(ct); return new[] { i }; },
            maxConcurrency: 4);

        await using var enumerator = stream.GetAsyncEnumerator();
        var first = enumerator.MoveNextAsync();

        // Finish everything except the first item; the stream must still hold the line.
        gates[3].SetResult();
        gates[2].SetResult();
        gates[1].SetResult();
        Assert.False(first.IsCompleted);

        gates[0].SetResult();
        Assert.True(await first);

        var seen = new List<int> { enumerator.Current };
        while (await enumerator.MoveNextAsync()) seen.Add(enumerator.Current);

        Assert.Equal(new[] { 0, 1, 2, 3 }, seen);
    }

    [Fact]
    public async Task NeverRunsMoreItemsConcurrentlyThanRequested()
    {
        const int concurrency = 3;
        int running = 0;
        int peak = 0;
        var peakLock = new object();

        var stream = OrderedStreamPump.RunOrderedAsync<int, int>(
            Enumerable.Range(0, 24).ToArray(),
            async (i, ct) =>
            {
                int now = Interlocked.Increment(ref running);
                lock (peakLock) peak = Math.Max(peak, now);
                await Task.Delay(5, ct);
                Interlocked.Decrement(ref running);
                return new[] { i };
            },
            concurrency);

        var seen = await Collect(stream);

        Assert.Equal(Enumerable.Range(0, 24), seen);
        Assert.InRange(peak, 1, concurrency);
    }

    [Fact]
    public async Task ExceptionSurfacesToTheConsumerAfterTheEarlierResults()
    {
        var stream = OrderedStreamPump.RunOrderedAsync<int, int>(
            new[] { 0, 1, 2, 3 },
            (i, ct) => i == 2
                ? throw new InvalidOperationException("boom")
                : Task.FromResult<IReadOnlyList<int>>(new[] { i }),
            maxConcurrency: 2);

        var seen = new List<int>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var value in stream) seen.Add(value);
        });

        Assert.Equal("boom", error.Message);       // unwrapped, not a ChannelClosedException
        Assert.Equal(new[] { 0, 1 }, seen);        // everything ahead of the failure was delivered
    }

    [Fact]
    public async Task CancellationStopsEnumerationPromptly()
    {
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var stream = OrderedStreamPump.RunOrderedAsync<int, int>(
            Enumerable.Range(0, 100).ToArray(),
            async (i, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new[] { i };
            },
            maxConcurrency: 2,
            cts.Token);

        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in stream) { }
        });

        await entered.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => consumer.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task CancellationFlowsThroughWithCancellation()
    {
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // No token passed to the pump: the one supplied by WithCancellation must reach it.
        var stream = OrderedStreamPump.RunOrderedAsync<int, int>(
            Enumerable.Range(0, 100).ToArray(),
            async (i, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new[] { i };
            },
            maxConcurrency: 1);

        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in stream.WithCancellation(cts.Token)) { }
        });

        await entered.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => consumer.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task AbandoningTheEnumerationDisposesCleanlyAndStopsProducing()
    {
        int started = 0;
        var stream = OrderedStreamPump.RunOrderedAsync<int, int>(
            Enumerable.Range(0, 64).ToArray(),
            async (i, ct) =>
            {
                Interlocked.Increment(ref started);
                await Task.Delay(10, ct);
                return new[] { i };
            },
            maxConcurrency: 2);

        var dispose = Task.Run(async () =>
        {
            await using var enumerator = stream.GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(0, enumerator.Current);
        });

        // Disposal must not hang waiting for the remaining 63 items.
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));

        int startedAtDisposal = Volatile.Read(ref started);
        Assert.True(startedAtDisposal < 64, $"the producer ran ahead to completion ({startedAtDisposal} items)");

        await Task.Delay(150);
        Assert.Equal(startedAtDisposal, Volatile.Read(ref started)); // no item started after disposal
    }

    [Fact]
    public async Task CompletesExactlyOnceAndCanBeEnumeratedAgain()
    {
        var work = Enumerable.Range(0, 25).ToArray();
        var stream = OrderedStreamPump.RunOrderedAsync<int, int>(
            work,
            (i, ct) => Task.FromResult<IReadOnlyList<int>>(new[] { i, -i }),
            maxConcurrency: 4);

        var expected = work.SelectMany(i => new[] { i, -i }).ToList();

        await using (var enumerator = stream.GetAsyncEnumerator())
        {
            var seen = new List<int>();
            while (await enumerator.MoveNextAsync()) seen.Add(enumerator.Current);
            Assert.Equal(expected, seen);

            // A second (and third) MoveNextAsync on the drained stream must stay false rather than blow
            // up — which is what a channel completed twice, or completed with an error, would do.
            Assert.False(await enumerator.MoveNextAsync());
            Assert.False(await enumerator.MoveNextAsync());
        }

        // Each enumeration is independent: it builds its own channel and producer.
        Assert.Equal(expected, await Collect(stream));
    }

    [Fact]
    public async Task EmptyWorkListYieldsNothing()
    {
        var stream = OrderedStreamPump.RunOrderedAsync<int, int>(
            Array.Empty<int>(),
            (i, ct) => throw new InvalidOperationException("must not be called"),
            maxConcurrency: 4);

        Assert.Empty(await Collect(stream));
    }

    [Fact]
    public async Task FlattensMultiResultItemsInOrder()
    {
        var stream = OrderedStreamPump.RunOrderedAsync<int, string>(
            new[] { 0, 1 },
            (i, ct) => Task.FromResult<IReadOnlyList<string>>(new[] { $"{i}a", $"{i}b" }),
            maxConcurrency: 2);

        Assert.Equal(new[] { "0a", "0b", "1a", "1b" }, await Collect(stream));
    }

    // ---- reading order over detected regions ----

    [Fact]
    public void OrdersDetectedRegionsIntoReadingOrder()
    {
        var regions = new[]
        {
            Region(10, 200, 100, 30),  // second row
            Region(10, 10, 100, 30),   // first row, left
            Region(120, 10, 100, 30),  // first row, right
        };

        var ordered = EasyOcrService.OrderRegionsForReading(regions);

        // Top row left-to-right, then the row below it.
        Assert.Equal(new[] { 10.0, 120.0, 10.0 }, ordered.Select(r => r.BoundingBox.MinX).ToArray());
        Assert.Equal(new[] { 10.0, 10.0, 200.0 }, ordered.Select(r => r.BoundingBox.MinY).ToArray());
    }

    [Fact]
    public void OrderingKeepsRegionsWithIdenticalGeometryDistinct()
    {
        // Value-equal regions must not collapse: every input has to come out exactly once.
        var regions = new[] { Region(10, 10, 50, 20), Region(10, 10, 50, 20), Region(10, 60, 50, 20) };

        var ordered = EasyOcrService.OrderRegionsForReading(regions);

        Assert.Equal(3, ordered.Count);
        Assert.Equal(2, ordered.Count(r => r.BoundingBox.MinY == 10));
    }

    [Fact]
    public void OrderingASingleRegionIsAPassThrough()
    {
        var regions = new[] { Region(5, 5, 10, 10) };
        Assert.Same(regions, EasyOcrService.OrderRegionsForReading(regions));
    }

    // ---- interface default implementation ----

    [Fact]
    public void CustomImplementationsGetAThrowingDefaultRatherThanABreakingChange()
    {
        IEasyOcrService service = new StubOcrService();
        using var image = new Image<Rgb24>(4, 4);
        var languages = new[] { "en" };

        Assert.Throws<NotSupportedException>(() => { _ = service.ExtractTextStreamAsync(image, languages); });
        Assert.Throws<NotSupportedException>(() => { _ = service.ExtractTextStreamAsync("sample.png", languages); });
        Assert.Throws<NotSupportedException>(() => { _ = service.ExtractTextStreamAsync(Stream.Null, languages); });
        Assert.Throws<NotSupportedException>(() => { _ = service.ExtractTextStreamAsync(new byte[] { 1, 2, 3 }, languages); });
    }

    [Fact]
    public async Task StubImplementationStillSatisfiesTheInterface()
    {
        // Guards the backward-compatibility promise: a hand-written implementation that predates
        // streaming compiles and runs untouched.
        IEasyOcrService service = new StubOcrService();
        var result = await service.ExtractTextFromImage("anything.png", new[] { "en" });

        Assert.Same(OcrResult.Empty, result);
        await service.DisposeAsync();
    }

    // ---- helpers ----

    private static TaskCompletionSource[] Gates(int count) =>
        Enumerable.Range(0, count)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream) items.Add(item);
        return items;
    }

    private static DetectedRegion Region(double x, double y, double width, double height)
    {
        var polygon = new[]
        {
            new OcrPoint(x, y), new OcrPoint(x + width, y),
            new OcrPoint(x + width, y + height), new OcrPoint(x, y + height),
        };
        return new DetectedRegion { BoundingPolygon = polygon, BoundingBox = OcrBoundingBox.FromPoints(polygon) };
    }

    /// <summary>
    /// A minimal <see cref="IEasyOcrService"/> written the way a caller's mock would have been before
    /// streaming existed — it implements only the members that never had a default implementation.
    /// </summary>
    private sealed class StubOcrService : IEasyOcrService
    {
        public Task<OcrResult> ExtractTextFromImage(string imagePath, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(OcrResult.Empty);

        public Task<OcrResult> ExtractTextFromImage(Stream imageStream, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(OcrResult.Empty);

        public Task<OcrResult> ExtractTextFromImage(byte[] imageBytes, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(OcrResult.Empty);

        public Task<OcrResult> ExtractTextFromImage(ReadOnlyMemory<byte> imageBytes, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(OcrResult.Empty);

        public Task<OcrResult> ExtractTextFromImage(Image<Rgb24> image, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(OcrResult.Empty);

        public Task<IReadOnlyList<string>> DetectLanguagesAsync(Image<Rgb24> image, IEnumerable<string>? candidates = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyList<string>> DetectLanguagesAsync(string imagePath, IEnumerable<string>? candidates = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
