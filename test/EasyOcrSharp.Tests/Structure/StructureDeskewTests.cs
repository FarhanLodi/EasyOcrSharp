using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyOcrSharp.Structure.Engine;
using EasyOcrSharp.Structure.Engine.Models;
using Xunit;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;
using OcrWord = EasyOcrSharp.Models.OcrWord;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure tests for <see cref="PreprocessingOptions.Deskew"/>: the Hough-based estimator's sign, and the
/// mapping of coordinates found on the deskewed canvas back onto the caller's image.
/// </summary>
public class StructureDeskewTests
{
    /// <summary>An 800×600 white page with text-like dark bars in its top-left quadrant.</summary>
    private static Image<Rgb24> Page()
    {
        var page = new Image<Rgb24>(800, 600, new Rgb24(255, 255, 255));
        page.ProcessPixelRows(accessor =>
        {
            for (int line = 0; line < 10; line++)
            {
                int top = 40 + (line * 26);
                int x = 30;
                for (int word = 0; x < 390; word++)
                {
                    int width = 30 + ((word * 37 + line * 11) % 50);
                    for (int y = top; y < top + 11; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int xx = x; xx < Math.Min(x + width, 390); xx++) row[xx] = new Rgb24(0, 0, 0);
                    }
                    x += width + 12;
                }
            }
        });
        return page;
    }

    private static OcrPoint DarkCentroid(Image<Rgb24> image)
    {
        double sx = 0, sy = 0;
        long n = 0;
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    if (row[x].R < 128) { sx += x + 0.5; sy += y + 0.5; n++; }
                }
            }
        });
        return new OcrPoint(sx / n, sy / n);
    }

    [Theory]
    [InlineData(3f)]
    [InlineData(-2.5f)]
    public void Estimator_returns_the_correcting_rotation(float skew)
    {
        using var page = Page();
        using var skewed = ImagePreprocessor.RotateWithWhiteBackground(page, skew);

        float correction = ImagePreprocessor.EstimateDeskewRotation(skewed);

        Assert.InRange(correction, -skew - 0.5f, -skew + 0.5f);
    }

    [Fact]
    public void Point_mapping_inverts_the_canvas_rotation()
    {
        // A single dark dot rotated onto a canvas must map back to where it started.
        using var src = new Image<Rgb24>(300, 200, new Rgb24(255, 255, 255));
        for (int y = 40; y < 46; y++)
            for (int x = 220; x < 226; x++)
                src[x, y] = new Rgb24(0, 0, 0);
        var expected = DarkCentroid(src);

        using var canvas = ImagePreprocessor.RotateWithWhiteBackground(src, -7f);
        var found = DarkCentroid(canvas);
        var mapped = ImagePreprocessor.MapPointFromRotatedCanvas(found, -7f, canvas.Width, canvas.Height, src.Width, src.Height);

        Assert.InRange(mapped.X, expected.X - 1, expected.X + 1);
        Assert.InRange(mapped.Y, expected.Y - 1, expected.Y + 1);
    }

    [Fact]
    public void Deskewed_boxes_land_on_the_original_text()
    {
        using var page = Page();
        using var skewed = ImagePreprocessor.RotateWithWhiteBackground(page, 3f);
        var textCentre = DarkCentroid(skewed);

        using var working = ImagePreprocessor.Apply(skewed, new PreprocessingOptions { Deskew = true }, out float rotation);
        Assert.NotEqual(0f, rotation);
        Assert.True(working.Width > skewed.Width, "deskew works on an enlarged canvas");

        // Treat the text block on the working canvas as one detected box and map it back.
        var c = DarkCentroid(working);
        var poly = new[] { new OcrPoint(c.X - 5, c.Y - 5), new OcrPoint(c.X + 5, c.Y - 5), new OcrPoint(c.X + 5, c.Y + 5), new OcrPoint(c.X - 5, c.Y + 5) };
        var line = new OcrLine { Text = "block", BoundingPolygon = poly, BoundingBox = OcrBoundingBox.FromPoints(poly) };

        var mapped = ImagePreprocessor.MapFromRotatedCanvas(new[] { line }, rotation, working.Width, working.Height, skewed.Width, skewed.Height)[0];

        Assert.InRange(mapped.BoundingBox.CenterX, textCentre.X - 2, textCentre.X + 2);
        Assert.InRange(mapped.BoundingBox.CenterY, textCentre.Y - 2, textCentre.Y + 2);
    }

    [Fact]
    public void Word_boxes_are_mapped_with_their_line()
    {
        // Issue #9: word boxes were left in canvas coordinates, offset right and down from the ink.
        const float rotation = -1.2f;
        const int sw = 2550, sh = 3300;
        var (cw, ch) = (2620, 3354);
        OcrPoint[] Quad(double x, double y, double w, double h) =>
            new[] { new OcrPoint(x, y), new OcrPoint(x + w, y), new OcrPoint(x + w, y + h), new OcrPoint(x, y + h) };

        var linePoly = Quad(400, 900, 600, 40);
        var wordPoly = Quad(700, 900, 120, 40);
        var line = new OcrLine
        {
            Text = "part 12345",
            BoundingPolygon = linePoly,
            BoundingBox = OcrBoundingBox.FromPoints(linePoly),
            Words = new[] { new OcrWord { Text = "12345", BoundingPolygon = wordPoly, BoundingBox = OcrBoundingBox.FromPoints(wordPoly) } },
        };

        var mapped = ImagePreprocessor.MapFromRotatedCanvas(new[] { line }, rotation, cw, ch, sw, sh)[0];
        var word = Assert.Single(mapped.Words);

        for (int i = 0; i < 4; i++)
        {
            var expected = ImagePreprocessor.MapPointFromRotatedCanvas(wordPoly[i], rotation, cw, ch, sw, sh);
            Assert.Equal(expected.X, word.BoundingPolygon[i].X, 6);
            Assert.Equal(expected.Y, word.BoundingPolygon[i].Y, 6);
        }
        Assert.Equal(OcrBoundingBox.FromPoints(word.BoundingPolygon), word.BoundingBox);
        Assert.InRange(word.BoundingBox.CenterX, mapped.BoundingBox.MinX, mapped.BoundingBox.MaxX);
        Assert.InRange(word.BoundingBox.CenterY, mapped.BoundingBox.MinY, mapped.BoundingBox.MaxY);
    }

    [Theory]
    [InlineData(2.5f)]
    [InlineData(-0.4f)]
    public void Point_mapping_to_and_from_the_canvas_round_trips(float rotation)
    {
        var p = new OcrPoint(123.4, 2890.1);
        var there = ImagePreprocessor.MapPointToRotatedCanvas(p, rotation, 2700, 3400, 2550, 3300);
        var back = ImagePreprocessor.MapPointFromRotatedCanvas(there, rotation, 2700, 3400, 2550, 3300);
        Assert.Equal(p.X, back.X, 6);
        Assert.Equal(p.Y, back.Y, 6);
    }

    [Fact]
    public void Region_is_moved_onto_the_deskewed_canvas()
    {
        // The dark block sits inside the caller's region on the source image; on the canvas the mapped
        // region must still contain it.
        using var page = Page();
        using var skewed = ImagePreprocessor.RotateWithWhiteBackground(page, 3f);
        using var working = ImagePreprocessor.Apply(skewed, new PreprocessingOptions { Deskew = true }, out float rotation);
        Assert.NotEqual(0f, rotation);

        var region = OcrRegion.Pixels(0, 0, skewed.Width / 2.0, skewed.Height);
        var onCanvas = ImagePreprocessor.MapRegionToRotatedCanvas(region, rotation, working.Width, working.Height, skewed.Width, skewed.Height);
        var (x, y, w, h) = onCanvas.Resolve(working.Width, working.Height);
        var ink = DarkCentroid(working);

        Assert.False(onCanvas.Normalized);
        Assert.InRange(ink.X, x, x + w);
        Assert.InRange(ink.Y, y, y + h);
        Assert.InRange(w, skewed.Width / 2, working.Width);

        // Fractional regions resolve against the source, not the larger canvas.
        var fraction = ImagePreprocessor.MapRegionToRotatedCanvas(
            OcrRegion.Fraction(0, 0, 0.5, 1), rotation, working.Width, working.Height, skewed.Width, skewed.Height);
        Assert.Equal(onCanvas, fraction);
    }

    [Fact]
    public void No_deskew_reports_no_rotation()
    {
        using var page = Page();
        using var working = ImagePreprocessor.Apply(page, new PreprocessingOptions { Denoise = true }, out float rotation);
        Assert.Equal(0f, rotation);
        Assert.Equal(page.Width, working.Width);
    }
}
