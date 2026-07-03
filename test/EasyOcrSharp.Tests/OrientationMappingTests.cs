using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Model-free tests for the DetectOrientation coordinate fix: after the orientation sweep picks a
/// rotated frame, boxes must be mapped back into the original image's coordinate space so they stay
/// anchored to the image the caller passed (and agree with the reported SourceWidth/SourceHeight).
/// </summary>
public class OrientationMappingTests
{
    // Original (un-rotated) page used for every case.
    private const int W = 100;
    private const int H = 200;

    // Forward transform matching ImageSharp's clockwise Rotate(degrees): where a point in the ORIGINAL
    // frame lands in the rotated copy. MapLinesToOriginalOrientation must invert exactly this.
    private static OcrPoint Forward(OcrPoint p, int degrees) => degrees switch
    {
        90 => new OcrPoint(H - p.Y, p.X),
        180 => new OcrPoint(W - p.X, H - p.Y),
        270 => new OcrPoint(p.Y, W - p.X),
        _ => p,
    };

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void MapBack_is_exact_inverse_of_the_rotation(int degrees)
    {
        // A quad in the original frame.
        var original = new[]
        {
            new OcrPoint(10, 20), new OcrPoint(60, 20),
            new OcrPoint(60, 120), new OcrPoint(10, 120),
        };

        // Simulate what detection returns: the same quad expressed in the rotated copy's frame.
        var rotatedFrame = original.Select(p => Forward(p, degrees)).ToArray();
        var line = new OcrLine
        {
            Text = "hello",
            Confidence = 0.9,
            BoundingPolygon = rotatedFrame,
            BoundingBox = OcrBoundingBox.FromPoints(rotatedFrame),
        };

        var mapped = EasyOcrService.MapLinesToOriginalOrientation(new[] { line }, degrees, W, H);

        var back = mapped[0].BoundingPolygon;
        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(original[i].X, back[i].X, precision: 6);
            Assert.Equal(original[i].Y, back[i].Y, precision: 6);
        }

        // The axis-aligned box is recomputed from the mapped polygon and matches the original.
        var box = mapped[0].BoundingBox;
        Assert.Equal(10, box.MinX, precision: 6);
        Assert.Equal(20, box.MinY, precision: 6);
        Assert.Equal(60, box.MaxX, precision: 6);
        Assert.Equal(120, box.MaxY, precision: 6);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Mapped_points_stay_within_the_original_bounds(int degrees)
    {
        // Corners of the rotated frame (its own extents), which must map back inside [0,W]x[0,H].
        int rw = degrees == 180 ? W : H;
        int rh = degrees == 180 ? H : W;
        var corners = new[]
        {
            new OcrPoint(0, 0), new OcrPoint(rw, 0),
            new OcrPoint(rw, rh), new OcrPoint(0, rh),
        };
        var line = new OcrLine { Text = "x", BoundingPolygon = corners, BoundingBox = OcrBoundingBox.FromPoints(corners) };

        var mapped = EasyOcrService.MapLinesToOriginalOrientation(new[] { line }, degrees, W, H);

        foreach (var p in mapped[0].BoundingPolygon)
        {
            Assert.InRange(p.X, 0, W);
            Assert.InRange(p.Y, 0, H);
        }
    }

    [Fact]
    public void Zero_degrees_returns_the_same_instance_untouched()
    {
        var line = new OcrLine { Text = "x", BoundingPolygon = new[] { new OcrPoint(1, 2) } };
        var input = new[] { line };
        var mapped = EasyOcrService.MapLinesToOriginalOrientation(input, 0, W, H);
        Assert.Same(input, mapped); // no-op: no allocation, boxes already in the source frame
    }
}
