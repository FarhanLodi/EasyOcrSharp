using EasyOcrSharp.Structure.Engine.Geometry;
using EasyOcrSharp.Structure.Engine.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

using OcrPoint = EasyOcrSharp.Models.OcrPoint;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for <see cref="PerspectiveWarp.Rectify"/>. Adapted
/// from EasyOcrSharp's PerspectiveWarpTests: an axis-aligned quad takes the cheap crop fast path and yields
/// the exact crop dimensions; a rotated quad is warped into an upright rectangle wider than it is tall; a
/// degenerate (sub-2px) quad returns null.
/// </summary>
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

    // ---- rotateVertical (Python's post-warp rot90 heuristic) --------------------------------------

    [Fact]
    public void Rectify_rotateVertical_rotates_a_tall_crop_90_ccw()
    {
        using var src = new Image<Rgb24>(100, 100);
        // Mark the pixel that lands at the crop's top-right corner; after a 90° CCW rotation
        // (np.rot90) the top-right pixel becomes the top-left one.
        src[29, 20] = new Rgb24(255, 0, 0);

        // 20 wide × 60 tall — h/w = 3.0 ≥ 1.5, so the vertical-line heuristic fires.
        var quad = new[]
        {
            new OcrPoint(10, 20), new OcrPoint(30, 20),
            new OcrPoint(30, 80), new OcrPoint(10, 80),
        };

        using var crop = PerspectiveWarp.Rectify(src, quad, rotateVertical: true);

        Assert.NotNull(crop);
        Assert.Equal(60, crop!.Width);
        Assert.Equal(20, crop.Height);
        Assert.Equal(new Rgb24(255, 0, 0), crop[0, 0]);
    }

    [Fact]
    public void Rectify_rotateVertical_leaves_a_just_below_threshold_crop_unrotated()
    {
        using var src = new Image<Rgb24>(100, 100);
        // 20 wide × 29 tall — h/w = 1.45 < 1.5, just below the rot90 threshold.
        var quad = new[]
        {
            new OcrPoint(10, 20), new OcrPoint(30, 20),
            new OcrPoint(30, 49), new OcrPoint(10, 49),
        };

        using var crop = PerspectiveWarp.Rectify(src, quad, rotateVertical: true);

        Assert.NotNull(crop);
        Assert.Equal(20, crop!.Width);
        Assert.Equal(29, crop.Height);
    }

    [Fact]
    public void Rectify_without_rotateVertical_keeps_a_tall_crop_tall()
    {
        using var src = new Image<Rgb24>(100, 100);
        var quad = new[]
        {
            new OcrPoint(10, 20), new OcrPoint(30, 20),
            new OcrPoint(30, 80), new OcrPoint(10, 80),
        };

        using var crop = PerspectiveWarp.Rectify(src, quad);

        Assert.NotNull(crop);
        Assert.Equal(20, crop!.Width);
        Assert.Equal(60, crop.Height);
    }
}
