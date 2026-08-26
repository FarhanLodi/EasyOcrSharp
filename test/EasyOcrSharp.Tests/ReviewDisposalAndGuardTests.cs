using EasyOcrSharp.Internal;
using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// CI-safe tests for disposal semantics and the image decompression-bomb guard. These construct the
/// service (which initializes ONNX Runtime session options) but never download models — every assertion
/// fires before any model load.
/// </summary>
public class ReviewDisposalAndGuardTests
{
    private static byte[] TinyPng(int w, int h)
    {
        using var img = new Image<Rgb24>(w, h);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task RejectsImageExceedingMaxPixels()
    {
        await using var svc = new EasyOcrService(new EasyOcrServiceOptions { MaxImagePixels = 1 });
        var png = TinyPng(2, 2); // 4 px > 1

        await Assert.ThrowsAsync<ImageTooLargeException>(() => svc.ExtractTextFromImage(png, new[] { "en" }));
    }

    /// <summary>
    /// "Set to 0 to disable" has to reach the decoder as well, not just the header check: the decoder
    /// applies a ceiling of its own when it is given no limits, which would silently cap a caller who
    /// turned the guard off (or raised it above that default).
    /// </summary>
    [Fact]
    public void A_disabled_pixel_budget_lifts_the_decoder_ceiling_too()
    {
        Assert.Equal(long.MaxValue, DecodeLimits.For(0).MaxPixels);
        Assert.Equal(long.MaxValue, DecodeLimits.For(-1).MaxPixels);
        Assert.Equal(100_000_000, DecodeLimits.For(100_000_000).MaxPixels);

        // And an ordinary image still decodes with the guard off.
        using var decoded = DecodeLimits.Load(TinyPng(4, 4), 0, "test");
        Assert.Equal(4, decoded.Width);
    }

    /// <summary>
    /// A rejection raised by the decoder itself — the case the header check cannot see, because the
    /// header under-reported — must still surface as this library's own exception with the name of the
    /// option to raise, not as a decoder-specific type.
    /// </summary>
    [Fact]
    public void A_decoder_level_rejection_is_restated_as_ImageTooLarge()
    {
        var png = TinyPng(8, 8); // 64 px

        var ex = Assert.Throws<ImageTooLargeException>(
            () => DecodeLimits.Load(png, 10, "EasyOcrServiceOptions.MaxImagePixels"));
        Assert.Contains("EasyOcrServiceOptions.MaxImagePixels", ex.Message);
    }

    [Fact]
    public async Task UseAfterDisposeThrowsObjectDisposed()
    {
        var svc = new EasyOcrService();
        await svc.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => svc.ExtractTextFromImage(new byte[] { 1, 2, 3 }, new[] { "en" }));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => svc.WarmUp(new[] { "en" }));
    }

    [Fact]
    public async Task DoubleDisposeIsSafe()
    {
        var svc = new EasyOcrService();
        await svc.DisposeAsync();
        await svc.DisposeAsync(); // no throw
        svc.Dispose();            // no throw
    }
}
