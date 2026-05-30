using EasyOcrSharp.Models;
using Xunit;

namespace EasyOcrSharp.Tests;

public class OcrRegionTests
{
    [Fact]
    public void Pixels_resolve_unchanged_within_bounds()
    {
        var (x, y, w, h) = OcrRegion.Pixels(10, 20, 100, 50).Resolve(640, 480);
        Assert.Equal((10, 20, 100, 50), (x, y, w, h));
    }

    [Fact]
    public void Fraction_resolves_relative_to_image_size()
    {
        // Bottom half of a 200x100 image.
        var (x, y, w, h) = OcrRegion.Fraction(0, 0.5, 1, 0.5).Resolve(200, 100);
        Assert.Equal((0, 50, 200, 50), (x, y, w, h));
    }

    [Fact]
    public void Resolve_clamps_to_image_bounds()
    {
        // Region extends past the right/bottom edges -> clamped.
        var (x, y, w, h) = OcrRegion.Pixels(180, 90, 100, 100).Resolve(200, 100);
        Assert.Equal(180, x);
        Assert.Equal(90, y);
        Assert.Equal(20, w); // 200 - 180
        Assert.Equal(10, h); // 100 - 90
    }
}
