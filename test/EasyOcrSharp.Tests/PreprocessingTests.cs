using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
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

    /// <summary>
    /// Deskew is delegated to the imaging library's projection-profile estimator instead of the
    /// hand-rolled search this used to run. Equivalence is asserted on the outcome that matters: a page
    /// of text lines tilted by a known angle comes back with its lines horizontal again. "Horizontal" is
    /// measured without reference to any angle estimator — in a straight page a row inside a text line is
    /// ink all the way across, whereas in a tilted page almost every row is part ink, part paper.
    /// </summary>
    [Theory]
    [InlineData(4f)]
    [InlineData(-7f)]
    public void Apply_deskew_straightens_a_tilted_page(float skewDegrees)
    {
        using var straight = TextLinePage(420, 320);
        using var tilted = straight.Clone(c => c.Rotate(skewDegrees, KnownResamplers.Bicubic, Color.White));

        double before = MixedRowFraction(tilted);
        Assert.True(before > 0.8, $"the fixture should be visibly tilted, mixed-row fraction was {before:F2}");

        using var corrected = ImagePreprocessor.Apply(tilted, new PreprocessingOptions { Deskew = true });

        double after = MixedRowFraction(corrected);
        Assert.True(after < 0.4, $"deskew should restore horizontal text lines, mixed-row fraction was {after:F2}");
        Assert.True(after < before / 2, $"deskew should improve on the tilted page ({after:F2} vs {before:F2})");
    }

    /// <summary>An already-straight page is left alone rather than resampled by a spurious micro-rotation.</summary>
    [Fact]
    public void Apply_deskew_leaves_a_straight_page_alone()
    {
        using var straight = TextLinePage(420, 320);
        using var corrected = ImagePreprocessor.Apply(straight, new PreprocessingOptions { Deskew = true });

        Assert.Equal(straight.Width, corrected.Width);
        Assert.Equal(straight.Height, corrected.Height);
        Assert.True(MixedRowFraction(corrected) < 0.4);
    }

    /// <summary>A white page with evenly spaced full-width black bars standing in for lines of text.</summary>
    private static Image<Rgb24> TextLinePage(int width, int height)
    {
        var image = new Image<Rgb24>(width, height, new Rgb24(255, 255, 255));
        image.ProcessPixelRows(rows =>
        {
            for (int line = 0; line < 6; line++)
            {
                int top = 30 + (line * 45);
                for (int y = top; y < top + 10; y++)
                {
                    var row = rows.GetRowSpan(y);
                    for (int x = 40; x < width - 40; x++) row[x] = new Rgb24(0, 0, 0);
                }
            }
        });
        return image;
    }

    /// <summary>
    /// Of the rows carrying any ink at all, the fraction that are only partly inked across the middle half
    /// of the page. Near 1 when the lines run diagonally, small when they run horizontally. The middle half
    /// is used so the blank triangles a rotation leaves in the corners cannot skew the count.
    /// </summary>
    private static double MixedRowFraction(Image<Rgb24> image)
    {
        int from = image.Width / 4;
        int to = image.Width * 3 / 4;
        int span = to - from;
        int inked = 0, mixed = 0;

        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                int dark = 0;
                for (int x = from; x < to; x++)
                {
                    if (row[x].R < 128) dark++;
                }

                double fraction = (double)dark / span;
                if (fraction <= 0.02) continue;
                inked++;
                if (fraction < 0.9) mixed++;
            }
        });

        return inked == 0 ? 1 : (double)mixed / inked;
    }
}
