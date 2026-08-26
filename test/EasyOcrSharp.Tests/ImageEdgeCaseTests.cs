using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Bmp;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.Formats.Qoi;
using EasyImageSharp.Formats.Tga;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Edge-case image inputs: degenerate sizes, blank pages, and non-PNG encodings. These prove the
/// pipeline never crashes on pathological input and that any format EasyImageSharp can decode is accepted
/// (the public API takes a decoded image/stream, so format support is whatever EasyImageSharp provides).
/// </summary>
[Trait("Category", "Integration")]
[Collection(OcrIntegrationCollection.Name)]
public class ImageEdgeCaseTests
{
    private static string Sample() => TestAssets.Image("sample.png")
        ?? throw new InvalidOperationException("assets/sample.png missing");

    [Fact]
    public async Task Blank_white_page_returns_empty_result_without_crashing()
    {
        await using var ocr = new EasyOcrService();
        using var white = new Image<Rgb24>(800, 600, new Rgb24(255, 255, 255));

        var r = await ocr.ExtractTextFromImage(white, new[] { "en" });

        Assert.Empty(r.Lines);
        Assert.Equal(string.Empty, r.FullText);
    }

    [Fact]
    public async Task Solid_black_page_does_not_crash()
    {
        await using var ocr = new EasyOcrService();
        using var black = new Image<Rgb24>(640, 480, new Rgb24(0, 0, 0));

        var r = await ocr.ExtractTextFromImage(black, new[] { "en" });

        Assert.NotNull(r);
        Assert.NotNull(r.FullText);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 64)]
    [InlineData(64, 1)]
    public async Task Degenerate_tiny_images_do_not_crash(int w, int h)
    {
        await using var ocr = new EasyOcrService();
        using var tiny = new Image<Rgb24>(w, h, new Rgb24(255, 255, 255));

        var r = await ocr.ExtractTextFromImage(tiny, new[] { "en" });

        Assert.NotNull(r);
        Assert.Empty(r.Lines);
    }

    [Fact]
    public async Task Large_upscaled_image_is_still_read()
    {
        await using var ocr = new EasyOcrService();
        using var src = await Image.LoadAsync<Rgb24>(Sample());
        using var big = src.Clone(c => c.Resize(src.Width * 4, src.Height * 4));

        var r = await ocr.ExtractTextFromImage(big, new[] { "en" });

        Assert.Contains("HELLO", TestAssets.Normalize(r.FullText));
    }

    public static IEnumerable<object[]> Formats()
    {
        yield return new object[] { "bmp", new BmpEncoder() };
        yield return new object[] { "tiff", new TiffEncoder() };
        yield return new object[] { "jpeg", new JpegEncoder { Quality = 95 } };
        // WebP is decode-only in EasyImageSharp, so there is no encoder to round-trip through here;
        // the two lossless codecs below keep the non-PNG decode path covered in its place.
        yield return new object[] { "qoi", new QoiEncoder() };
        yield return new object[] { "tga", new TgaEncoder() };
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Non_png_formats_are_decoded_and_read(string name, IImageEncoder encoder)
    {
        using var src = await Image.LoadAsync<Rgb24>(Sample());
        using var ms = new MemoryStream();
        await src.SaveAsync(ms, encoder);
        ms.Position = 0;

        await using var ocr = new EasyOcrService();
        var r = await ocr.ExtractTextFromImage(ms, new[] { "en" });

        Assert.Contains("HELLO", TestAssets.Normalize(r.FullText));
        _ = name; // label only
    }
}
