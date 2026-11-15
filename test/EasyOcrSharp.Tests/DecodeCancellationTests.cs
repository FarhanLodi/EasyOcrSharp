using EasyImageSharp;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;
using EasyOcrSharp.Internal;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// The request's <see cref="CancellationToken"/> reaches EasyImageSharp's decoder through
/// <see cref="DecoderOptions.ExecutionOptions"/> on every <see cref="DecodeLimits"/> entry point — encoded
/// bytes, path, stream and the all-frames decode — while the pixel budget, the lifted
/// <see cref="DecoderOptions.MaxTotalPixels"/>, EXIF orientation and transparency flattening stay exactly
/// as they were. A canceled decode must surface as <see cref="OperationCanceledException"/>, never as the
/// <see cref="ImageTooLargeException"/> the budget restates.
/// </summary>
public class DecodeCancellationTests
{
    private static byte[] EncodePng(Image<Rgba32> image)
    {
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static byte[] SmallPng()
    {
        using var source = new Image<Rgba32>(16, 12, new Rgba32(255, 255, 255, 255));
        source[3, 4] = new Rgba32(0, 0, 0, 255);
        return EncodePng(source);
    }

    private static CancellationToken Canceled() => new(canceled: true);

    // ------------------------------------------------------------------ option mapping

    [Fact]
    public void A_token_that_cannot_cancel_leaves_the_decoder_options_unchanged()
    {
        // Byte-identical default path: no execution scope is introduced for CancellationToken.None.
        var unlimited = DecodeLimits.For(0);
        Assert.Same(unlimited, DecodeLimits.For(0, CancellationToken.None));
        Assert.Null(unlimited.ExecutionOptions);
        Assert.Equal(long.MaxValue, unlimited.MaxPixels);
        Assert.Equal(long.MaxValue, unlimited.MaxTotalPixels);

        var budget = DecodeLimits.For(1234, CancellationToken.None);
        Assert.Null(budget.ExecutionOptions);
        Assert.Equal(1234, budget.MaxPixels);
        Assert.Equal(long.MaxValue, budget.MaxTotalPixels);

        Assert.Same(DecoderOptions.Default, DecodeLimits.ForHeader(CancellationToken.None));
    }

    [Fact]
    public void A_cancelable_token_travels_with_the_limits_without_changing_them()
    {
        using var cts = new CancellationTokenSource();

        var budget = DecodeLimits.For(1234, cts.Token);
        Assert.Equal(1234, budget.MaxPixels);
        Assert.Equal(long.MaxValue, budget.MaxTotalPixels);           // still lifted (multi-page TIFFs)
        Assert.Equal(cts.Token, budget.ExecutionOptions?.CancellationToken);

        var unlimited = DecodeLimits.For(0, cts.Token);
        Assert.Equal(long.MaxValue, unlimited.MaxPixels);
        Assert.Equal(long.MaxValue, unlimited.MaxTotalPixels);
        Assert.Equal(cts.Token, unlimited.ExecutionOptions?.CancellationToken);
        Assert.NotSame(DecodeLimits.For(0), unlimited);                // the shared instance is never mutated

        var header = DecodeLimits.ForHeader(cts.Token);
        Assert.Equal(cts.Token, header.ExecutionOptions?.CancellationToken);
        Assert.Equal(DecoderOptions.Default.MaxPixels, header.MaxPixels);
        Assert.Equal(DecoderOptions.Default.MaxEncodedBytes, header.MaxEncodedBytes);

        // Each call builds its own options, so one request's token never leaks into another's decode.
        using var other = new CancellationTokenSource();
        Assert.Equal(other.Token, DecodeLimits.For(1234, other.Token).ExecutionOptions?.CancellationToken);
        Assert.Equal(cts.Token, budget.ExecutionOptions?.CancellationToken);
    }

    // ------------------------------------------------------------------ cancellation surfaces as OCE

    [Fact]
    public void Canceled_bytes_decode_throws_OperationCanceled()
    {
        var bytes = SmallPng();
        Assert.ThrowsAny<OperationCanceledException>(() => DecodeLimits.Load(bytes, 0, "test", Canceled()));
        Assert.ThrowsAny<OperationCanceledException>(() => DecodeLimits.Load(bytes, 100_000, "test", Canceled()));
    }

    [Fact]
    public void Canceled_header_read_throws_OperationCanceled()
    {
        var bytes = SmallPng();
        Assert.ThrowsAny<OperationCanceledException>(() => DecodeLimits.Identify(bytes, Canceled()));
    }

    [Fact]
    public void Canceled_decode_is_not_reported_as_an_oversized_image()
    {
        // The image exceeds the 10-pixel budget, so an uncanceled decode is rejected by the decoder and
        // restated as ImageTooLargeException. A canceled one must stay a cancellation.
        var bytes = SmallPng();
        Assert.Throws<ImageTooLargeException>(() => DecodeLimits.Load(bytes, 10, "test"));

        var ex = Record.Exception(() => DecodeLimits.Load(bytes, 10, "test", Canceled()));
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    [Fact]
    public async Task Canceled_stream_decode_throws_OperationCanceled()
    {
        using var stream = new MemoryStream(SmallPng());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DecodeLimits.LoadAsync(stream, 0, "test", Canceled()));
    }

    [Fact]
    public async Task Canceled_path_decode_throws_OperationCanceled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easyocr-decode-cancel-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, SmallPng());
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => DecodeLimits.LoadAsync(path, 100_000, "test", Canceled()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Canceled_all_frames_decode_throws_OperationCanceled()
    {
        using var source = new Image<Rgba32>(8, 8, new Rgba32(255, 255, 255, 255));
        source.Frames.CreateFrame(8, 8);
        source.Frames.CreateFrame(8, 8);
        using var ms = new MemoryStream();
        source.Save(ms, new EasyImageSharp.Formats.Tiff.TiffEncoder());
        var tiff = ms.ToArray();

        Assert.ThrowsAny<OperationCanceledException>(() => DecodeLimits.Identify(tiff, Canceled()));
        Assert.ThrowsAny<OperationCanceledException>(() => DecodeLimits.Load(tiff, 0, "test", Canceled()));
    }

    [Fact]
    public async Task A_stream_canceled_while_it_is_read_stops_before_decoding()
    {
        // The token is canceled by the stream itself once the last byte has been handed over, i.e. after
        // reading but before the decoder runs: the decode must not complete.
        using var cts = new CancellationTokenSource();
        using var stream = new CancelAtEndStream(SmallPng(), cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DecodeLimits.LoadAsync(stream, 0, "test", cts.Token));
    }

    // ------------------------------------------------------------------ output unchanged by a live token

    [Fact]
    public async Task A_live_token_changes_no_pixel_transparency_or_orientation()
    {
        // Transparent background + half-transparent ink + EXIF orientation 6: every normalization step the
        // decode performs. A cancelable-but-uncanceled token must produce exactly the same pixels.
        using var source = new Image<Rgba32>(20, 10, new Rgba32(0, 0, 0, 0));
        source[2, 3] = new Rgba32(0, 0, 0, 255);
        source[7, 5] = new Rgba32(10, 200, 30, 128);
        source.Metadata.ExifProfile = new ExifProfile();
        source.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        var bytes = EncodePng(source);

        using var cts = new CancellationTokenSource();
        using var plain = DecodeLimits.Load(bytes, 0, "test");
        using var withToken = DecodeLimits.Load(bytes, 0, "test", cts.Token);
        using var withBudget = DecodeLimits.Load(bytes, 1_000_000, "test", cts.Token);
        using var stream = new MemoryStream(bytes);
        using var viaStream = await DecodeLimits.LoadAsync(stream, 1_000_000, "test", cts.Token);

        Assert.Equal(10, plain.Width);    // orientation applied
        Assert.Equal(20, plain.Height);
        AssertSamePixels(plain, withToken);
        AssertSamePixels(plain, withBudget);
        AssertSamePixels(plain, viaStream);
    }

    [Fact]
    public void A_live_token_keeps_every_frame()
    {
        using var source = new Image<Rgba32>(8, 8, new Rgba32(255, 255, 255, 255));
        source.Frames.CreateFrame(8, 8);
        source.Frames.CreateFrame(8, 8);
        using var ms = new MemoryStream();
        source.Save(ms, new EasyImageSharp.Formats.Tiff.TiffEncoder());

        using var cts = new CancellationTokenSource();
        using var decoded = DecodeLimits.Load(ms.ToArray(), 0, "test", cts.Token);
        Assert.Equal(3, decoded.Frames.Count);
        Assert.Equal(3, DecodeLimits.Identify(ms.ToArray(), cts.Token).FrameCount);
    }

    private static void AssertSamePixels(Image<Rgb24> expected, Image<Rgb24> actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Assert.Equal(expected[x, y], actual[x, y]);
            }
        }
    }

    /// <summary>A read-only stream that cancels <paramref name="cts"/> when it reports end-of-stream.</summary>
    private sealed class CancelAtEndStream(byte[] data, CancellationTokenSource cts) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            if (read == 0) cts.Cancel();
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = _inner.Read(buffer.Span);
            if (read == 0) cts.Cancel();
            return ValueTask.FromResult(read);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
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
