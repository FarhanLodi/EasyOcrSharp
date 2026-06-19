using EasyOcrSharp.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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
