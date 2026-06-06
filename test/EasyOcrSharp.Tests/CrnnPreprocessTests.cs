using EasyOcrSharp.Internal;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Regression tests for recognizer preprocessing. A noisy scan produces ultra-thin detection boxes;
/// resized to 64px height their width used to collapse to 1–2px and crash ONNX Runtime
/// ("Invalid input shape"). Narrow crops must now be edge-padded to a safe minimum width.
/// </summary>
public class CrnnPreprocessTests
{
    [Theory]
    [InlineData(1, 64)]   // 1px-wide sliver — the case that crashed the whole page
    [InlineData(2, 50)]
    [InlineData(4, 60)]
    public void PreprocessForCrnn_pads_narrow_crops_to_safe_minimum_width(int w, int h)
    {
        using var thin = new Image<Rgb24>(w, h, new Rgb24(20, 20, 20));

        var tensor = ImageProcessing.PreprocessForCrnn(
            thin, targetHeight: 64, maxWidth: 4096, adjustContrast: false, out int width);

        Assert.True(width >= 16, $"Expected a minimum input width >= 16, got {width}.");
        Assert.Equal(64 * width, tensor.Length);
    }

    [Fact]
    public void PreprocessForCrnn_keeps_wide_crops_at_natural_width()
    {
        using var wide = new Image<Rgb24>(200, 64, new Rgb24(255, 255, 255));

        ImageProcessing.PreprocessForCrnn(wide, 64, 4096, false, out int width);

        Assert.True(width >= 100, $"A wide crop should keep its natural width, got {width}.");
    }
}
