using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace EasyOcrSharp.Tests;

public class ImageProcessingTests
{
    [Fact]
    public void PreprocessForCrnn_produces_aspect_preserving_width_and_normalized_range()
    {
        using var crop = new Image<Rgb24>(120, 30); // ratio 4:1
        crop.Mutate(c => c.BackgroundColor(Color.Gray));

        var tensor = ImageProcessing.PreprocessForCrnn(crop, targetHeight: 64, maxWidth: 4096, adjustContrast: false, out int width);

        // width = ceil(64 * 120/30) = 256
        Assert.Equal(256, width);
        Assert.Equal(64 * 256, tensor.Length);
        Assert.All(tensor, v => Assert.InRange(v, -1.0f, 1.0f));
    }

    [Fact]
    public void PreprocessForCrnn_caps_width_at_maxWidth()
    {
        using var crop = new Image<Rgb24>(2000, 10); // ratio 200:1
        var _ = ImageProcessing.PreprocessForCrnn(crop, 64, maxWidth: 512, adjustContrast: false, out int width);
        Assert.Equal(512, width);
    }

    [Fact]
    public void PreprocessForCrnn_handles_degenerate_crop()
    {
        using var crop = new Image<Rgb24>(1, 1);
        var tensor = ImageProcessing.PreprocessForCrnn(crop, 64, 4096, false, out int width);
        Assert.True(width >= 1);
        Assert.Equal(64 * width, tensor.Length);
    }
}
