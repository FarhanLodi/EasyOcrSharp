using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Model-free tests for <see cref="DetectionOptions.MinTextHeight"/> (the magnification the small-text rescue
/// re-detects at) and <see cref="DetectionOptions.EnhanceContrast"/> (the detector-only enhanced copy).
/// </summary>
public class SmallTextAndContrastTests
{
    private const int Canvas = 2560;

    [Fact]
    public void MedianShortSide_reads_the_text_height_of_horizontal_and_rotated_boxes()
    {
        var boxes = new[]
        {
            DetectionTilingTests.Rect(0, 0, 200, 10),
            DetectionTilingTests.Rect(0, 20, 50, 32),
            DetectionTilingTests.Rect(0, 40, 300, 54),
            // A 90°-rotated word: the short side is its width.
            DetectionTilingTests.Rect(400, 0, 416, 120),
        };

        Assert.Equal((12 + 14) / 2.0, DetectionTiling.MedianShortSide(boxes), 9);
        Assert.Equal(0, DetectionTiling.MedianShortSide(Array.Empty<OcrPoint[]>()));
    }

    [Fact]
    public void Rescue_magnifies_small_text_up_to_the_canvas()
    {
        // 1280 px page, 10 px text, threshold 16: wants 24 / 10 = 2.4×, the canvas allows 2560 / 1280 = 2×.
        Assert.Equal(2.0, DetectionTiling.RescueMagnification(10, 16, 1280, Canvas, 1.0), 9);

        // 640 px page: 2.4× fits.
        Assert.Equal(2.4, DetectionTiling.RescueMagnification(10, 16, 640, Canvas, 1.0), 9);

        // Tiny text is magnified at most 3×.
        Assert.Equal(3.0, DetectionTiling.RescueMagnification(4, 16, 640, Canvas, 1.0), 9);

        // A threshold above 24 becomes the target.
        Assert.Equal(3.0, DetectionTiling.RescueMagnification(12, 40, 600, Canvas, 1.0), 9);
        Assert.Equal(40.0 / 14, DetectionTiling.RescueMagnification(14, 40, 600, Canvas, 1.0), 9);
    }

    [Theory]
    [InlineData(20, 16, 1280, 1.0)]   // text already tall enough
    [InlineData(10, 0, 1280, 1.0)]    // rescue off
    [InlineData(10, 16, 2560, 1.0)]   // the canvas is already full: nothing to gain
    [InlineData(10, 16, 4000, 1.0)]   // page shrunk by the cap: magnifying is impossible
    [InlineData(9, 16, 1280, 2.0)]    // MagRatio 2 already shows the 9 px text at 18 px
    [InlineData(15.5, 16, 2500, 1.0)] // would only gain 2.4 %: not worth a second pass
    public void Rescue_declines_when_it_cannot_or_need_not_help(double median, int minTextHeight, int longSide, double mag)
    {
        Assert.Equal(0, DetectionTiling.RescueMagnification(median, minTextHeight, longSide, Canvas, mag));
    }

    [Fact]
    public void Rescue_measures_text_at_the_detector_input_scale()
    {
        // MagRatio 1.5 shows 10 px text at 15 px (< 16): aim for 24 px -> scale 1.5 × 1.6 = 2.4.
        Assert.Equal(2.4, DetectionTiling.RescueMagnification(10, 16, 800, Canvas, 1.5), 9);
    }

    [Theory]
    [InlineData(600, 2.0)]
    [InlineData(1000, 2.0)]
    [InlineData(1400, 2560.0 / 1400)]
    public void Empty_small_pages_are_retried_magnified(int longSide, double expected)
    {
        Assert.Equal(expected, DetectionTiling.EmptyRetryMagnification(longSide, Canvas, 1.0), 9);
    }

    [Theory]
    [InlineData(1500, 1.0)]
    [InlineData(2400, 1.0)]
    [InlineData(800, 2.0)]   // MagRatio 2 already puts the detector input at 1600 px
    public void Empty_large_pages_are_not_retried(int longSide, double mag)
    {
        Assert.Equal(0, DetectionTiling.EmptyRetryMagnification(longSide, Canvas, mag));
    }

    [Fact]
    public void Contrast_enhanced_copy_keeps_the_geometry_and_widens_a_faded_page()
    {
        // A faded page: background 200, "ink" 170.
        using var faded = new Image<Rgb24>(240, 160, new Rgb24(200, 200, 200));
        faded.ProcessPixelRows(rows =>
        {
            for (int y = 60; y < 100; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 40; x < 200; x += 3) row[x] = new Rgb24(170, 170, 170);
            }
        });

        using var enhanced = CraftDetector.CreateContrastEnhancedCopy(faded);

        Assert.Equal(faded.Width, enhanced.Width);
        Assert.Equal(faded.Height, enhanced.Height);
        Assert.True(Range(enhanced) > Range(faded), $"range {Range(faded)} -> {Range(enhanced)}");

        // The original is untouched — recognition still reads it.
        Assert.Equal(new Rgb24(200, 200, 200), faded[0, 0]);
    }

    private static int Range(Image<Rgb24> image)
    {
        int min = 255, max = 0;
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                foreach (var p in rows.GetRowSpan(y))
                {
                    min = Math.Min(min, p.R);
                    max = Math.Max(max, p.R);
                }
            }
        });
        return max - min;
    }
}
