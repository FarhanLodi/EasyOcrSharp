using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace EasyOcrSharp.Tests;

public class PreprocessingTests
{
    [Fact]
    public void PreprocessingOptions_none_has_nothing_enabled()
    {
        Assert.False(PreprocessingOptions.None.IsAnyEnabled);
        var o = new PreprocessingOptions { Binarize = true };
        Assert.True(o.IsAnyEnabled);
    }

    [Fact]
    public void RotateRightAngle_swaps_dimensions_for_90_degrees()
    {
        using var img = new Image<Rgb24>(100, 50, new Rgb24(255, 255, 255));
        using var rotated = ImagePreprocessor.RotateRightAngle(img, 90);
        Assert.Equal(50, rotated.Width);
        Assert.Equal(100, rotated.Height);
    }

    [Fact]
    public void Apply_none_returns_equivalent_clone()
    {
        using var img = new Image<Rgb24>(40, 30, new Rgb24(200, 200, 200));
        using var result = ImagePreprocessor.Apply(img, PreprocessingOptions.None);
        Assert.Equal(img.Width, result.Width);
        Assert.Equal(img.Height, result.Height);
        Assert.NotSame(img, result); // always a fresh image
    }

    [Fact]
    public void Apply_binarize_produces_black_and_white_pixels()
    {
        using var img = new Image<Rgb24>(60, 40, new Rgb24(255, 255, 255));
        // Paint a darker rectangle directly (no Drawing package needed).
        img.ProcessPixelRows(acc =>
        {
            for (int y = 10; y < 30; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 10; x < 40; x++) row[x] = new Rgb24(90, 90, 90);
            }
        });

        using var result = ImagePreprocessor.Apply(img, new PreprocessingOptions { Binarize = true });

        bool allBinary = true;
        result.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    if (row[x].R is > 10 and < 245) { allBinary = false; return; }
            }
        });
        Assert.True(allBinary, "adaptive threshold should yield near-pure black/white pixels");
    }
}
