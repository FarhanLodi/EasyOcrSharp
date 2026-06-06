using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Complex production scenarios synthesized from the known-good <c>sample.png</c> (skewed, rotated 90°,
/// low-contrast, multi-block). These run automatically — no external files — and confirm the
/// preprocessing features recover text from hard inputs.
/// </summary>
[Trait("Category", "Integration")]
public class SyntheticComplexTests
{
    private static readonly Rgb24 White = new(255, 255, 255);

    private static string Sample()
    {
        var path = TestAssets.Image("sample.png");
        Assert.True(path is not null, "assets/sample.png not found — ensure it is copied to the test output.");
        return path!;
    }

    /// <summary>Rotates by an arbitrary angle, compositing over white so corners don't go black.</summary>
    private static Image<Rgb24> RotateOnWhite(Image<Rgb24> src, float degrees)
    {
        using var rgba = src.CloneAs<Rgba32>();
        rgba.Mutate(c => c.Rotate(degrees)); // transparent corners for an alpha format
        var canvas = new Image<Rgb24>(rgba.Width, rgba.Height, White);
        canvas.Mutate(c => c.DrawImage(rgba, 1f));
        return canvas;
    }

    [Fact]
    public async Task Skewed_page_is_recovered_with_deskew()
    {
        using var src = await Image.LoadAsync<Rgb24>(Sample());
        using var skewed = RotateOnWhite(src, 8f);

        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(skewed, new[] { "en" },
            new RecognitionOptions { Preprocessing = new PreprocessingOptions { Deskew = true } });

        Assert.Contains("HELLO", TestAssets.Normalize(result.FullText));
    }

    [Fact]
    public async Task Page_rotated_90_degrees_is_recovered_with_orientation_detection()
    {
        using var src = await Image.LoadAsync<Rgb24>(Sample());
        using var rotated = src.Clone(c => c.Rotate(RotateMode.Rotate90));

        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(rotated, new[] { "en" },
            new RecognitionOptions { Preprocessing = new PreprocessingOptions { DetectOrientation = true } });

        Assert.Contains("HELLO", TestAssets.Normalize(result.FullText));
    }

    [Fact]
    public async Task Low_contrast_page_is_recovered_with_binarize()
    {
        using var src = await Image.LoadAsync<Rgb24>(Sample());
        using var faint = src.Clone(c => c.Contrast(0.45f)); // wash the text toward gray

        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(faint, new[] { "en" },
            new RecognitionOptions { Preprocessing = new PreprocessingOptions { Binarize = true } });

        Assert.Contains("HELLO", TestAssets.Normalize(result.FullText));
    }

    [Fact]
    public async Task Multiple_text_blocks_on_one_canvas_are_all_read()
    {
        using var src = await Image.LoadAsync<Rgb24>(Sample());
        using var canvas = new Image<Rgb24>(src.Width, src.Height * 2 + 40, White);
        canvas.Mutate(c => c
            .DrawImage(src, new Point(0, 0), 1f)
            .DrawImage(src, new Point(0, src.Height + 40), 1f));

        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(canvas, new[] { "en" });

        var norm = TestAssets.Normalize(result.FullText);
        Assert.True(CountOccurrences(norm, "HELLO") >= 2,
            $"Expected the duplicated block's text twice. Got: {result.FullText}");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }
}
