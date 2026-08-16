using System.Net;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Services;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Tests for the byte-level progress the download manager reports. These use the
/// <see cref="ModelDownloadOptions.HttpClientFactory"/> seam to serve a payload from memory, so nothing
/// touches the network.
/// </summary>
public sealed class DownloadProgressTests : IDisposable
{
    private readonly string _cache;

    public DownloadProgressTests()
    {
        _cache = Path.Combine(Path.GetTempPath(), "easyocr-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cache);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Serves a fixed body, optionally trickling it so the transfer spans more than one reporting tick
    /// (the manager reports at most once a second while reading).
    /// </summary>
    private sealed class StubHandler(byte[] body, TimeSpan chunkDelay, int chunkSize) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new TrickleStream(body, chunkDelay, chunkSize)),
            };
            response.Content.Headers.ContentLength = body.Length;
            return Task.FromResult(response);
        }
    }

    /// <summary>A stream that hands out <paramref name="chunkSize"/> bytes at a time, pausing between chunks.</summary>
    private sealed class TrickleStream(byte[] body, TimeSpan delay, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => body.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= body.Length) return 0;
            if (_position > 0) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            int n = Math.Min(Math.Min(chunkSize, buffer.Length), body.Length - _position);
            body.AsSpan(_position, n).CopyTo(buffer.Span);
            _position += n;
            return n;
        }
    }

    private ModelDownloadOptions OptionsServing(byte[] body, TimeSpan chunkDelay, int chunkSize, IProgress<ModelDownloadProgress> progress)
        => new()
        {
            // The stub serves whatever bytes we ask for, so checksum verification has nothing to check.
            AllowUnverifiedModels = true,
            MaxRetries = 0,
            Progress = progress,
            HttpClientFactory = () => new HttpClient(new StubHandler(body, chunkDelay, chunkSize)),
        };

    [Fact]
    public async Task A_completed_download_is_reported_exactly_once()
    {
        // Regression: the in-loop reporter fires at most once a second, and the chunk that finishes the
        // file can land on one of those ticks — after which the unconditional final report announced the
        // same completed state a second time. A consumer summing progress then double-counted the tail.
        var body = new byte[400_000];
        Random.Shared.NextBytes(body);

        var reports = new List<ModelDownloadProgress>();
        var progress = new Progress<ModelDownloadProgress>(p => { lock (reports) reports.Add(p); });

        // Trickle in chunks with a pause long enough to guarantee several reporting ticks, including one
        // on the very last chunk.
        var options = OptionsServing(body, TimeSpan.FromMilliseconds(350), chunkSize: 100_000, progress);
        var asset = new ModelAsset("progress-once.onnx", "https://example.invalid/progress-once.onnx", null);

        var path = await ModelDownloadManager.EnsureModelAsync(asset, _cache, options, null, CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(body.Length, new FileInfo(path).Length);

        // Progress<T> posts asynchronously; give the callbacks a moment to drain.
        await Task.Delay(200);

        List<ModelDownloadProgress> snapshot;
        lock (reports) snapshot = [.. reports];

        int completed = snapshot.Count(r => r.TotalBytes > 0 && r.BytesDownloaded >= r.TotalBytes);
        Assert.True(completed == 1, $"expected exactly one completion report, got {completed} of {snapshot.Count}: " +
            string.Join(", ", snapshot.Select(r => $"{r.BytesDownloaded}/{r.TotalBytes}")));
    }

    [Fact]
    public async Task Progress_is_monotonic_and_ends_at_the_total()
    {
        var body = new byte[250_000];
        Random.Shared.NextBytes(body);

        var reports = new List<ModelDownloadProgress>();
        var progress = new Progress<ModelDownloadProgress>(p => { lock (reports) reports.Add(p); });

        var options = OptionsServing(body, TimeSpan.FromMilliseconds(300), chunkSize: 50_000, progress);
        var asset = new ModelAsset("progress-monotonic.onnx", "https://example.invalid/progress-monotonic.onnx", null);

        await ModelDownloadManager.EnsureModelAsync(asset, _cache, options, null, CancellationToken.None);
        await Task.Delay(200);

        List<ModelDownloadProgress> snapshot;
        lock (reports) snapshot = [.. reports];

        Assert.NotEmpty(snapshot);
        Assert.All(snapshot, r => Assert.Equal(body.Length, r.TotalBytes));
        Assert.Equal(body.Length, snapshot[^1].BytesDownloaded);

        for (int i = 1; i < snapshot.Count; i++)
        {
            Assert.True(snapshot[i].BytesDownloaded > snapshot[i - 1].BytesDownloaded,
                "byte counts must strictly increase — a repeated value is a duplicate report");
        }
    }

    [Fact]
    public async Task A_cached_file_is_returned_without_downloading_or_reporting_again()
    {
        // This is why the duplicate report was only cosmetic: a second request for a cached asset never
        // reaches the network, so no bandwidth was ever wasted.
        var body = new byte[120_000];
        Random.Shared.NextBytes(body);

        var reports = new List<ModelDownloadProgress>();
        var progress = new Progress<ModelDownloadProgress>(p => { lock (reports) reports.Add(p); });
        var options = OptionsServing(body, TimeSpan.Zero, chunkSize: 60_000, progress);
        var asset = new ModelAsset("progress-cached.onnx", "https://example.invalid/progress-cached.onnx", null);

        var first = await ModelDownloadManager.EnsureModelAsync(asset, _cache, options, null, CancellationToken.None);
        await Task.Delay(150);
        int afterFirst;
        lock (reports) afterFirst = reports.Count;

        var second = await ModelDownloadManager.EnsureModelAsync(asset, _cache, options, null, CancellationToken.None);
        await Task.Delay(150);
        int afterSecond;
        lock (reports) afterSecond = reports.Count;

        Assert.Equal(first, second);
        Assert.Equal(afterFirst, afterSecond);
    }
}
