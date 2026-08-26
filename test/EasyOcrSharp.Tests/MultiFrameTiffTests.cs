using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Multi-frame (multi-page TIFF / animated GIF) OCR. Every test builds a genuine multi-frame image in
/// memory with EasyImageSharp and drives the extensions against a stub <see cref="IEasyOcrService"/>, so the
/// suite needs no models, no network and no fixtures — it verifies frame walking, the guards, progress,
/// cancellation and ownership, not recognition quality.
/// </summary>
public class MultiFrameTiffTests
{
    private const int FrameWidth = 24;
    private const int FrameHeight = 16;
    private static readonly string[] English = { "en" };

    // ---- fixtures ----

    /// <summary>
    /// Builds a real multi-frame TIFF. Frame <c>i</c> is white except for a single black pixel at
    /// <c>(i, 0)</c>, which acts as a fingerprint: the stub reads it back to prove that the pixels it was
    /// handed are that frame's own, not frame 0's repeated.
    /// </summary>
    private static byte[] BuildMultiFrameTiff(int frameCount)
    {
        using var image = NewMarkedImage(0);
        for (int i = 1; i < frameCount; i++)
        {
            using var extra = NewMarkedImage(i);
            image.Frames.AddFrame(extra.Frames.RootFrame);
        }

        using var buffer = new MemoryStream();
        image.SaveAsTiff(buffer);
        return buffer.ToArray();
    }

    /// <summary>Builds an in-memory multi-frame image (no encoding round-trip).</summary>
    private static Image<Rgb24> BuildMultiFrameImage(int frameCount)
    {
        var image = NewMarkedImage(0);
        for (int i = 1; i < frameCount; i++)
        {
            using var extra = NewMarkedImage(i);
            image.Frames.AddFrame(extra.Frames.RootFrame);
        }
        return image;
    }

    private static Image<Rgb24> NewMarkedImage(int marker)
    {
        var image = new Image<Rgb24>(FrameWidth, FrameHeight, new Rgb24(255, 255, 255));
        image[marker, 0] = new Rgb24(0, 0, 0);
        return image;
    }

    private static byte[] SingleFramePng()
    {
        using var image = NewMarkedImage(0);
        using var buffer = new MemoryStream();
        image.SaveAsPng(buffer);
        return buffer.ToArray();
    }

    private static int MarkerOf(Image<Rgb24> image)
    {
        for (int x = 0; x < image.Width; x++)
        {
            if (image[x, 0].R == 0) return x;
        }
        return -1;
    }

    private static int MarkerOf(ImageFrame<Rgb24> frame)
    {
        for (int x = 0; x < frame.Width; x++)
        {
            if (frame[x, 0].R == 0) return x;
        }
        return -1;
    }

    // ---- frame walking ----

    [Fact]
    public async Task Every_frame_of_a_multi_frame_tiff_is_recognized()
    {
        var tiff = BuildMultiFrameTiff(4);
        await using var stub = new StubOcrService();

        var result = await stub.ExtractTextFromFramesAsync(tiff, English);

        Assert.Equal(4, result.Frames.Count);
        Assert.Equal(4, result.FrameCount);
        Assert.Equal(new[] { 0, 1, 2, 3 }, result.Frames.Select(f => f.FrameIndex));
        // Each call saw that frame's own fingerprint pixel — frame 0 was not re-sent four times.
        Assert.Equal(new[] { 0, 1, 2, 3 }, stub.Markers);
        Assert.All(result.Frames, f =>
        {
            Assert.Equal(FrameWidth, f.PixelWidth);
            Assert.Equal(FrameHeight, f.PixelHeight);
        });
        Assert.Equal("frame-0\n\nframe-1\n\nframe-2\n\nframe-3", result.FullText);
        Assert.True(result.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Single_frame_image_flows_through_the_same_call_and_returns_one_result()
    {
        await using var stub = new StubOcrService();

        var result = await stub.ExtractTextFromFramesAsync(SingleFramePng(), English);

        Assert.Single(result.Frames);
        Assert.Equal(0, result.Frames[0].FrameIndex);
        Assert.Equal("frame-0", result.FullText);
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task Frames_are_read_from_a_stream_which_is_left_open()
    {
        using var stream = new MemoryStream(BuildMultiFrameTiff(3));
        await using var stub = new StubOcrService();

        var result = await stub.ExtractTextFromFramesAsync(stream, English);

        Assert.Equal(3, result.Frames.Count);
        Assert.True(stream.CanRead); // not disposed by the extension
    }

    [Fact]
    public async Task Frames_are_read_from_a_non_seekable_stream()
    {
        using var stream = new NonSeekableStream(BuildMultiFrameTiff(3));
        await using var stub = new StubOcrService();

        var result = await stub.ExtractTextFromFramesAsync(stream, English);

        Assert.Equal(3, result.Frames.Count);
        Assert.Equal(new[] { 0, 1, 2 }, stub.Markers);
    }

    // ---- streaming ----

    [Fact]
    public async Task Streaming_yields_each_frame_as_it_completes()
    {
        var tiff = BuildMultiFrameTiff(4);
        await using var stub = new StubOcrService();

        var seen = new List<int>();
        await foreach (var frame in stub.StreamTextFromFramesAsync(tiff, English))
        {
            // The service has been called exactly once per frame yielded so far — nothing is buffered ahead.
            seen.Add(frame.FrameIndex);
            Assert.Equal(seen.Count, stub.Calls);
        }

        Assert.Equal(new[] { 0, 1, 2, 3 }, seen);
    }

    [Fact]
    public async Task Abandoning_the_stream_stops_recognizing_further_frames()
    {
        var tiff = BuildMultiFrameTiff(6);
        await using var stub = new StubOcrService();

        await foreach (var frame in stub.StreamTextFromFramesAsync(tiff, English))
        {
            if (frame.FrameIndex == 1) break;
        }

        Assert.Equal(2, stub.Calls);
    }

    // ---- guards ----

    [Fact]
    public async Task MaxFrames_rejects_an_oversized_container_before_any_frame_is_recognized()
    {
        var tiff = BuildMultiFrameTiff(5);
        await using var stub = new StubOcrService();
        var frameOptions = new MultiFrameOcrOptions { MaxFrames = 2 };

        var ex = await Assert.ThrowsAsync<ImageTooLargeException>(
            () => stub.ExtractTextFromFramesAsync(tiff, English, frameOptions: frameOptions));

        Assert.Contains("MaxFrames", ex.Message);
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task MaxFrames_zero_means_unlimited()
    {
        var tiff = BuildMultiFrameTiff(4);
        await using var stub = new StubOcrService();

        var result = await stub.ExtractTextFromFramesAsync(tiff, English, frameOptions: new MultiFrameOcrOptions { MaxFrames = 0 });

        Assert.Equal(4, result.Frames.Count);
    }

    [Fact]
    public async Task Per_frame_pixel_flood_guard_is_applied_to_every_frame()
    {
        // 1.1 MP per frame, over the 1 MP budget below.
        using var big = new Image<Rgb24>(1100, 1000, new Rgb24(255, 255, 255));
        using (var second = new Image<Rgb24>(1100, 1000, new Rgb24(255, 255, 255)))
        {
            big.Frames.AddFrame(second.Frames.RootFrame);
        }
        await using var stub = new StubOcrService();

        var ex = await Assert.ThrowsAsync<ImageTooLargeException>(
            () => stub.ExtractTextFromFramesAsync(big, English, frameOptions: new MultiFrameOcrOptions { MaxFrameMegapixels = 1 }));

        Assert.Contains("MaxFrameMegapixels", ex.Message);
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public void Negative_bounds_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MultiFrameOcrOptions { MaxFrames = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MultiFrameOcrOptions { MaxFrameMegapixels = -1 }.Validate());
    }

    // ---- progress ----

    [Fact]
    public async Task Progress_is_reported_once_per_frame()
    {
        var tiff = BuildMultiFrameTiff(3);
        var progress = new ProgressCollector<MultiFrameProgress>();
        await using var stub = new StubOcrService();

        await stub.ExtractTextFromFramesAsync(tiff, English, frameOptions: new MultiFrameOcrOptions { Progress = progress });

        Assert.Equal(new[] { 1, 2, 3 }, progress.Reports.Select(p => p.FrameNumber));
        Assert.All(progress.Reports, p => Assert.Equal(3, p.FrameCount));
        Assert.Equal(1.0, progress.Reports[^1].Fraction, 6);
    }

    // ---- cancellation ----

    [Fact]
    public async Task Cancellation_between_frames_stops_the_run()
    {
        var tiff = BuildMultiFrameTiff(5);
        using var cts = new CancellationTokenSource();
        await using var stub = new StubOcrService((_, _) =>
        {
            cts.Cancel(); // cancel while the first frame is being "recognized"
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stub.ExtractTextFromFramesAsync(tiff, English, cancellationToken: cts.Token));

        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task An_already_cancelled_token_never_reaches_the_service()
    {
        var tiff = BuildMultiFrameTiff(3);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await using var stub = new StubOcrService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stub.ExtractTextFromFramesAsync(tiff, English, cancellationToken: cts.Token));

        Assert.Equal(0, stub.Calls);
    }

    // ---- ownership ----

    [Fact]
    public async Task The_callers_image_is_neither_disposed_nor_mutated()
    {
        using var image = BuildMultiFrameImage(3);
        var before = image.Frames.AsEnumerable().Select(f => MarkerOf(f)).ToArray();
        await using var stub = new StubOcrService();

        var result = await stub.ExtractTextFromFramesAsync(image, English);

        Assert.Equal(3, result.Frames.Count);
        // Still usable: a disposed EasyImageSharp image throws on any pixel/frame access.
        Assert.Equal(3, image.Frames.Count);
        Assert.Equal(FrameWidth, image.Width);
        Assert.Equal(new Rgb24(0, 0, 0), image[0, 0]);
        Assert.Equal(before, image.Frames.AsEnumerable().Select(f => MarkerOf(f)).ToArray());
        // Every frame of a multi-frame image was recognized from its own throw-away clone.
        Assert.All(stub.Instances, instance => Assert.NotSame(image, instance));
        Assert.Equal(new[] { 0, 1, 2 }, stub.Markers);
    }

    // ---- argument validation ----

    [Fact]
    public void Null_and_empty_arguments_are_rejected()
    {
        using var stub = new StubOcrService();

        // Validation is eager: it fires on the call, not on the first MoveNextAsync.
        Assert.Throws<ArgumentNullException>(() => { _ = stub.StreamTextFromFramesAsync((byte[])null!, English); });
        Assert.Throws<ArgumentNullException>(() => { _ = stub.StreamTextFromFramesAsync((Stream)null!, English); });
        Assert.Throws<ArgumentNullException>(() => { _ = stub.StreamTextFromFramesAsync((Image<Rgb24>)null!, English); });
        Assert.Throws<ArgumentNullException>(() => { _ = stub.StreamTextFromFramesAsync(SingleFramePng(), null!); });
        Assert.Throws<ArgumentException>(() => { _ = stub.StreamTextFromFramesAsync(" ", English); });
        Assert.Throws<ArgumentException>(() => { _ = stub.StreamTextFromFramesAsync(Array.Empty<byte>(), English); });
    }

    [Fact]
    public async Task Missing_file_reports_a_file_not_found()
    {
        await using var stub = new StubOcrService();
        var missing = Path.Combine(Path.GetTempPath(), $"easyocrsharp-missing-{Guid.NewGuid():N}.tif");

        await Assert.ThrowsAsync<FileNotFoundException>(() => stub.ExtractTextFromFramesAsync(missing, English));
    }

    [Fact]
    public async Task Frames_are_read_from_a_file_on_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easyocrsharp-frames-{Guid.NewGuid():N}.tif");
        await File.WriteAllBytesAsync(path, BuildMultiFrameTiff(3));
        try
        {
            await using var stub = new StubOcrService();

            var result = await stub.ExtractTextFromFramesAsync(path, English);

            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(new[] { 0, 1, 2 }, stub.Markers);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- test doubles ----

    /// <summary>
    /// Minimal <see cref="IEasyOcrService"/> that records what it was handed. Only the image overload is
    /// exercised by the multi-frame extensions; the rest exist to satisfy the interface.
    /// </summary>
    private sealed class StubOcrService(Func<Image<Rgb24>, CancellationToken, Task>? onRecognize = null) : IEasyOcrService
    {
        /// <summary>The fingerprint pixel found on each image passed in, in call order.</summary>
        public List<int> Markers { get; } = new();

        /// <summary>The image instances passed in, for reference comparisons only (they may be disposed).</summary>
        public List<object> Instances { get; } = new();

        public int Calls => Markers.Count;

        public async Task<OcrResult> ExtractTextFromImage(
            Image<Rgb24> image, IEnumerable<string> languages, RecognitionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var langs = languages.ToArray();
            int marker = MarkerOf(image);
            Markers.Add(marker);
            Instances.Add(image);

            if (onRecognize is not null)
            {
                await onRecognize(image, cancellationToken).ConfigureAwait(false);
            }

            return new OcrResult
            {
                FullText = $"frame-{marker}",
                Lines = Array.Empty<OcrLine>(),
                Languages = langs,
                SourceWidth = image.Width,
                SourceHeight = image.Height,
            };
        }

        public Task<OcrResult> ExtractTextFromImage(string imagePath, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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

    /// <summary>Synchronous progress sink — <see cref="Progress{T}"/> would post through the sync context.</summary>
    private sealed class ProgressCollector<T> : IProgress<T>
    {
        public List<T> Reports { get; } = new();

        public void Report(T value) => Reports.Add(value);
    }

    /// <summary>A forward-only stream, to exercise the buffered (non-seekable) load path.</summary>
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
