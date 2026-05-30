using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

public class PerspectiveWarpTests
{
    [Fact]
    public void Rectify_axis_aligned_quad_returns_expected_crop_size()
    {
        using var src = new Image<Rgb24>(100, 100);
        var quad = new[]
        {
            new OcrPoint(10, 20), new OcrPoint(70, 20),
            new OcrPoint(70, 50), new OcrPoint(10, 50),
        };

        using var crop = PerspectiveWarp.Rectify(src, quad);

        Assert.NotNull(crop);
        Assert.Equal(60, crop!.Width);
        Assert.Equal(30, crop.Height);
    }

    [Fact]
    public void Rectify_rotated_quad_produces_upright_rectangle()
    {
        using var src = new Image<Rgb24>(200, 200);
        // A quad rotated ~20 degrees.
        var quad = new[]
        {
            new OcrPoint(50, 60), new OcrPoint(150, 95),
            new OcrPoint(140, 130), new OcrPoint(40, 95),
        };

        using var crop = PerspectiveWarp.Rectify(src, quad);

        Assert.NotNull(crop);
        // Output should be roughly as wide as the long edge and short in height.
        Assert.True(crop!.Width > crop.Height);
        Assert.True(crop.Width > 90);
    }

    [Fact]
    public void Rectify_degenerate_quad_returns_null()
    {
        using var src = new Image<Rgb24>(50, 50);
        var quad = new[]
        {
            new OcrPoint(10, 10), new OcrPoint(11, 10),
            new OcrPoint(11, 11), new OcrPoint(10, 11),
        };
        using var crop = PerspectiveWarp.Rectify(src, quad);
        Assert.Null(crop);
    }
}
