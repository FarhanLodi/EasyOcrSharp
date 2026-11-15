using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Model-free tests for the <see cref="PreprocessingOptions.Deskew"/> coordinate fix. Detection runs on the
/// rotated, enlarged canvas the deskew produces; before the fix, line (and word) boxes were returned in that
/// canvas's coordinates — right of and below their ink on the caller's image. These pin the recovered rotation
/// to the canvas the imaging library really produced, and the canvas→image map to real pixels: a coloured marker
/// is found on the deskewed canvas and must map back onto where it sits on the input page.
/// </summary>
public class DeskewCoordinateTests
{
    [Theory]
    [InlineData(4f)]
    [InlineData(-7f)]
    [InlineData(2.5f)]
    public void Apply_reports_the_rotation_it_applied(float skewDegrees)
    {
        using var tilted = TiltedPage(skewDegrees, markerAt: null);

        using var corrected = ImagePreprocessor.Apply(tilted, new PreprocessingOptions { Deskew = true }, out float rotation);

        // The correction undoes the tilt (to the estimator's 0.1° grid and sampling)...
        Assert.InRange(Math.Abs(rotation), Math.Abs(skewDegrees) - 0.5f, Math.Abs(skewDegrees) + 0.5f);
        // ...and it is exactly the rotation behind the canvas the library produced.
        Assert.Equal((corrected.Width, corrected.Height), DeskewRotation.PredictCanvasSize(tilted.Width, tilted.Height, rotation));
        // The estimator port agrees with the library's own estimate on these pixels.
        double estimate = DeskewRotation.EstimateProjectionSkew(tilted, 15f, out bool significant);
        Assert.True(significant);
        Assert.Equal((float)(-estimate), rotation);
    }

    [Fact]
    public void A_straight_page_reports_no_rotation()
    {
        using var straight = TextLinePage(420, 320);
        using var corrected = ImagePreprocessor.Apply(straight, new PreprocessingOptions { Deskew = true }, out float rotation);

        Assert.Equal(0f, rotation);
        Assert.Equal(straight.Width, corrected.Width);
        Assert.Equal(straight.Height, corrected.Height);
    }

    [Fact]
    public void Apply_without_deskew_reports_no_rotation()
    {
        using var tilted = TiltedPage(4f, markerAt: null);
        using var processed = ImagePreprocessor.Apply(tilted, new PreprocessingOptions { Denoise = true }, out float rotation);
        Assert.Equal(0f, rotation);
        Assert.Equal(tilted.Width, processed.Width);
    }

    /// <summary>
    /// The pixel-level check: a red marker's centroid on the deskewed canvas, mapped through
    /// <see cref="DeskewCanvas.ToSource"/>, lands on the marker's centroid on the tilted input page.
    /// </summary>
    [Theory]
    // Markers sit in the gaps between the bars, so their edges blend with white only.
    [InlineData(4f, 300, 50)]
    [InlineData(-7f, 90, 230)]
    [InlineData(3f, 400, 140)]
    public void Canvas_coordinates_map_back_onto_the_input_page(float skewDegrees, int markerX, int markerY)
    {
        using var tilted = TiltedPage(skewDegrees, markerAt: (markerX, markerY));
        var onPage = RedCentroid(tilted);

        using var corrected = ImagePreprocessor.Apply(tilted, new PreprocessingOptions { Deskew = true }, out float rotation);
        Assert.NotEqual(0f, rotation);
        var onCanvas = RedCentroid(corrected);

        var canvas = new DeskewCanvas(rotation, corrected.Width, corrected.Height, tilted.Width, tilted.Height);
        var mapped = canvas.ToSource(onCanvas);

        Assert.True(Math.Abs(mapped.X - onPage.X) < 1.0 && Math.Abs(mapped.Y - onPage.Y) < 1.0,
            $"marker at {onPage} on the page, {onCanvas} on the canvas, mapped back to {mapped}");
    }

    [Theory]
    [InlineData(4f)]
    [InlineData(-12.3f)]
    [InlineData(0.4f)]
    public void ToCanvas_and_ToSource_are_inverses(float rotation)
    {
        var (cw, ch) = DeskewRotation.PredictCanvasSize(1000, 700, rotation);
        var canvas = new DeskewCanvas(rotation, cw, ch, 1000, 700);

        foreach (var p in new[] { new OcrPoint(0, 0), new OcrPoint(1000, 700), new OcrPoint(512.25, 33.5), new OcrPoint(999, 1) })
        {
            var back = canvas.ToSourceUnclamped(canvas.ToCanvas(p));
            Assert.Equal(p.X, back.X, 6);
            Assert.Equal(p.Y, back.Y, 6);
        }

        // The page's corners land on the canvas, touching its edges (the canvas is their bounding box).
        var corners = new[] { new OcrPoint(0, 0), new OcrPoint(1000, 0), new OcrPoint(1000, 700), new OcrPoint(0, 700) }
            .Select(canvas.ToCanvas).ToArray();
        var bounds = OcrBoundingBox.FromPoints(corners);
        Assert.InRange(bounds.MinX, -1e-6, 1.0);
        Assert.InRange(bounds.MinY, -1e-6, 1.0);
        Assert.InRange(cw - bounds.MaxX, -1e-6, 1.0);
        Assert.InRange(ch - bounds.MaxY, -1e-6, 1.0);
    }

    [Fact]
    public void ToSource_clamps_into_the_page()
    {
        var (cw, ch) = DeskewRotation.PredictCanvasSize(800, 600, 6f);
        var canvas = new DeskewCanvas(6f, cw, ch, 800, 600);

        foreach (var p in new[] { new OcrPoint(0, 0), new OcrPoint(cw, 0), new OcrPoint(cw, ch), new OcrPoint(0, ch) })
        {
            var s = canvas.ToSource(p);
            Assert.InRange(s.X, 0, 800);
            Assert.InRange(s.Y, 0, 600);
        }
    }

    [Fact]
    public void Region_on_the_canvas_covers_the_caller_region()
    {
        var (cw, ch) = DeskewRotation.PredictCanvasSize(1000, 800, -5f);
        var canvas = new DeskewCanvas(-5f, cw, ch, 1000, 800);
        var region = OcrRegion.Fraction(0, 0.5, 1, 0.5);

        var onCanvas = canvas.RegionToCanvas(region);
        Assert.False(onCanvas.Normalized);

        // Every corner of the caller's region is inside the canvas region.
        foreach (var p in new[] { new OcrPoint(0, 400), new OcrPoint(1000, 400), new OcrPoint(1000, 800), new OcrPoint(0, 800) })
        {
            var c = canvas.ToCanvas(p);
            Assert.InRange(c.X, onCanvas.X - 1e-6, onCanvas.X + onCanvas.Width + 1e-6);
            Assert.InRange(c.Y, onCanvas.Y - 1e-6, onCanvas.Y + onCanvas.Height + 1e-6);
        }

        Assert.Equal(0, canvas.RegionToCanvas(OcrRegion.Pixels(2000, 2000, 10, 10)).Width);
    }

    [Fact]
    public void ResolveApplied_trusts_the_canvas_over_a_disagreeing_estimate()
    {
        var (w, h) = DeskewRotation.PredictCanvasSize(1200, 900, -4f);

        Assert.Equal(0f, DeskewRotation.ResolveApplied(1200, 900, 1200, 900, 4.0));   // not rotated
        Assert.Equal(-4f, DeskewRotation.ResolveApplied(1200, 900, w, h, 4.0));      // port agrees

        // A port that measured 3° where the library rotated by 4°: the magnitude comes from the canvas,
        // the direction from the port.
        float recovered = DeskewRotation.ResolveApplied(1200, 900, w, h, 3.0);
        Assert.InRange(recovered, -4.1f, -3.9f);
        Assert.Equal((w, h), DeskewRotation.PredictCanvasSize(1200, 900, recovered));
    }

    [Fact]
    public void Result_lines_words_and_characters_come_back_on_the_unrotated_image()
    {
        var (cw, ch) = DeskewRotation.PredictCanvasSize(1000, 700, 3f);
        var canvas = new DeskewCanvas(3f, cw, ch, 1000, 700);
        var line = NonSquarePixelsTests.LineAt(200, 300, 600, 330, withWords: true);
        var result = new OcrResult
        {
            FullText = line.Text,
            Lines = new[] { line },
            Languages = new[] { "en" },
            SourceWidth = 1000,
            SourceHeight = 700,
        };

        var mapped = EasyOcrService.ToCallerFrame(result, canvas, canvas.RotationDegrees, null);

        Assert.Equal(3f, mapped.DeskewAngle);
        Assert.Equal(1000, mapped.SourceWidth);
        Assert.Equal(700, mapped.SourceHeight);
        Assert.Equal(result.FullText, mapped.FullText);
        var expected = canvas.ToSource(line.BoundingPolygon[0]);
        Assert.Equal(expected, mapped.Lines[0].BoundingPolygon[0]);
        Assert.Equal(canvas.ToSource(line.Words[0].BoundingPolygon[2]), mapped.Lines[0].Words[0].BoundingPolygon[2]);
        Assert.Equal(canvas.ToSource(line.Characters[0].BoundingPolygon[1]), mapped.Lines[0].Characters[0].BoundingPolygon[1]);
        Assert.Equal(OcrBoundingBox.FromPoints(mapped.Lines[0].BoundingPolygon), mapped.Lines[0].BoundingBox);
    }

    [Fact]
    public void DeskewAngle_is_left_out_of_the_json_when_zero()
    {
        var plain = new OcrResult { FullText = "", Lines = Array.Empty<OcrLine>(), Languages = Array.Empty<string>() };
        string json = System.Text.Json.JsonSerializer.Serialize(plain, EasyOcrJsonContext.Default.OcrResult);
        Assert.DoesNotContain("DeskewAngle", json);

        string rotated = System.Text.Json.JsonSerializer.Serialize(plain with { DeskewAngle = -2.5f }, EasyOcrJsonContext.Default.OcrResult);
        Assert.Contains("\"DeskewAngle\":-2.5", rotated);
    }

    /// <summary>The <see cref="PreprocessingTests"/> fixture: six full-width bars standing in for lines of text.</summary>
    internal static Image<Rgb24> TextLinePage(int width, int height, (int X, int Y)? markerAt = null)
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

            if (markerAt is { } m)
            {
                for (int y = m.Y; y < m.Y + 10; y++)
                {
                    var row = rows.GetRowSpan(y);
                    for (int x = m.X; x < m.X + 10; x++) row[x] = new Rgb24(255, 0, 0);
                }
            }
        });
        return image;
    }

    private static Image<Rgb24> TiltedPage(float skewDegrees, (int X, int Y)? markerAt)
    {
        using var straight = TextLinePage(460, 320, markerAt);
        return straight.Clone(c => c.Rotate(skewDegrees, KnownResamplers.Bicubic, Color.White));
    }

    /// <summary>Centroid (continuous pixel coordinates) of the clearly red pixels.</summary>
    private static OcrPoint RedCentroid(Image<Rgb24> image)
    {
        double sx = 0, sy = 0;
        int n = 0;
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    if (p.R > 180 && p.G < 90 && p.B < 90)
                    {
                        sx += x + 0.5;
                        sy += y + 0.5;
                        n++;
                    }
                }
            }
        });
        Assert.True(n > 10, "marker not found");
        return new OcrPoint(sx / n, sy / n);
    }
}
