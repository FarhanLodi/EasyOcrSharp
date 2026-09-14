using EasyOcrSharp.Structure.Engine;
using EasyOcrSharp.Structure.Engine.Geometry;
using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure.Engine.Services;
using Xunit;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no models, CI-safe) for the document-orientation coordinate mapback added with
/// the OCR-path doc preprocessor: <see cref="OrientationMapper"/> (inverse-rotating quads from the
/// uprighted working frame back into the original image's orientation), the new
/// <see cref="RecognitionOptions"/> defaults (doc orientation ON / unwarp OFF, the documented
/// python-parity stance), the extracted <see cref="SortedBoxes"/> python <c>sorted_boxes</c> helper the
/// parity emitter uses, and the rotation-aware reading-order sort in <see cref="StructureService"/>.
/// </summary>
public class DocOrientationMapbackTests
{
    // ---- OrientationMapper: forward point mapping -----------------------------------------------------

    [Fact]
    public void RotatePoint_0_degrees_is_identity()
    {
        var p = new OcrPoint(12.5, 34.5);
        Assert.Equal(p, OrientationMapper.RotatePoint(p, 0, 100, 50));
    }

    [Fact]
    public void RotatePoint_90_cw_maps_top_left_to_top_right()
    {
        // 100×50 image rotated 90° CW → 50×100 frame. Continuous corner (0,0) → (H, 0) = (50, 0).
        var p = OrientationMapper.RotatePoint(new OcrPoint(0, 0), 90, 100, 50);
        Assert.Equal(new OcrPoint(50, 0), p);

        // Interior point: (x,y) → (H − y, x).
        var q = OrientationMapper.RotatePoint(new OcrPoint(10, 20), 90, 100, 50);
        Assert.Equal(new OcrPoint(30, 10), q);
    }

    [Fact]
    public void RotatePoint_180_maps_via_width_and_height()
    {
        var p = OrientationMapper.RotatePoint(new OcrPoint(10, 20), 180, 100, 50);
        Assert.Equal(new OcrPoint(90, 30), p);
    }

    [Fact]
    public void RotatePoint_270_cw_maps_via_width()
    {
        // (x,y) → (y, W − x).
        var p = OrientationMapper.RotatePoint(new OcrPoint(10, 20), 270, 100, 50);
        Assert.Equal(new OcrPoint(20, 90), p);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RotatePoint_roundtrip_returns_original(int degrees)
    {
        // Rotating W×H by d, then the result frame by (360−d), must be the identity.
        int w = 640, h = 480;
        int rw = degrees is 90 or 270 ? h : w;
        int rh = degrees is 90 or 270 ? w : h;

        var original = new OcrPoint(123.25, 45.75);
        var rotated = OrientationMapper.RotatePoint(original, degrees, w, h);
        var back = OrientationMapper.RotatePoint(rotated, 360 - degrees, rw, rh);

        Assert.Equal(original.X, back.X, 9);
        Assert.Equal(original.Y, back.Y, 9);
    }

    [Fact]
    public void RotatePoint_rejects_non_right_angles()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OrientationMapper.RotatePoint(new OcrPoint(0, 0), 45, 10, 10));
    }

    // ---- OrientationMapper: quad mapback (working frame → original frame) -----------------------------

    [Fact]
    public void MapLinesToOriginalFrame_180_restores_original_quad()
    {
        // Original page 200×100 was rotated 180° to upright it (working frame also 200×100). A quad at
        // the working frame's top-left maps back to the original's bottom-right, corner for corner.
        var workingQuad = new[]
        {
            new OcrPoint(10, 20), new OcrPoint(60, 20),
            new OcrPoint(60, 40), new OcrPoint(10, 40),
        };
        var line = MakeLine("hello", workingQuad);

        var mapped = OrientationMapper.MapLinesToOriginalFrame(new[] { line }, 180, 200, 100);

        var poly = mapped[0].BoundingPolygon;
        Assert.Equal(new OcrPoint(190, 80), poly[0]);
        Assert.Equal(new OcrPoint(140, 80), poly[1]);
        Assert.Equal(new OcrPoint(140, 60), poly[2]);
        Assert.Equal(new OcrPoint(190, 60), poly[3]);
        // The recomputed axis-aligned box covers the same area, reflected.
        Assert.Equal(140, mapped[0].BoundingBox.MinX, 9);
        Assert.Equal(60, mapped[0].BoundingBox.MinY, 9);
        Assert.Equal(190, mapped[0].BoundingBox.MaxX, 9);
        Assert.Equal(80, mapped[0].BoundingBox.MaxY, 9);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void MapLinesToOriginalFrame_90_and_270_roundtrip(int applied)
    {
        // Original 300×200 rotated `applied`° CW → working frame is 200×300. Mapping a working-frame
        // quad back and then forward again must reproduce it exactly.
        int workW = 200, workH = 300;
        var workingQuad = new[]
        {
            new OcrPoint(15, 25), new OcrPoint(80, 25),
            new OcrPoint(80, 45), new OcrPoint(15, 45),
        };
        var line = MakeLine("q", workingQuad);

        var mapped = OrientationMapper.MapLinesToOriginalFrame(new[] { line }, applied, workW, workH);
        // Every mapped point must land inside the ORIGINAL 300×200 frame.
        foreach (var p in mapped[0].BoundingPolygon)
        {
            Assert.InRange(p.X, 0, 300);
            Assert.InRange(p.Y, 0, 200);
        }

        var forward = OrientationMapper.RotatePolygon(mapped[0].BoundingPolygon, applied, 300, 200);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(workingQuad[i].X, forward[i].X, 9);
            Assert.Equal(workingQuad[i].Y, forward[i].Y, 9);
        }
    }

    [Fact]
    public void MapLinesToOriginalFrame_zero_rotation_returns_same_list()
    {
        var line = MakeLine("x", new[] { new OcrPoint(1, 2), new OcrPoint(3, 2), new OcrPoint(3, 4), new OcrPoint(1, 4) });
        var lines = new[] { line };
        Assert.Same(lines, OrientationMapper.MapLinesToOriginalFrame(lines, 0, 10, 10));
    }

    // ---- RecognitionOptions / OcrResult defaults ------------------------------------------------------

    [Fact]
    public void RecognitionOptions_doc_orientation_defaults_match_documented_stance()
    {
        // Python parity has BOTH stages on; C# keeps orientation on and unwarp off (documented deviation).
        var options = new RecognitionOptions();
        Assert.True(options.UseDocOrientation);
        Assert.False(options.UseDocUnwarp);
        Assert.True(RecognitionOptions.Default.UseDocOrientation);
        Assert.False(RecognitionOptions.Default.UseDocUnwarp);
    }

    [Fact]
    public void OcrResult_detected_orientation_defaults_to_zero()
    {
        Assert.Equal(0, OcrResult.Empty.DetectedOrientation);
    }

    // ---- SortedBoxes (python sorted_boxes, used by the parity emitter) --------------------------------

    [Fact]
    public void SortedBoxes_SortLines_same_row_within_tolerance_reads_left_to_right()
    {
        // Y differs by 8 (< 10px tolerance): left box first despite being listed second.
        var right = MakeLine("right", Quad(100, 8, 40, 20));
        var left = MakeLine("left", Quad(10, 0, 40, 20));

        var sorted = SortedBoxes.SortLines(new[] { right, left });

        Assert.Equal(new[] { "left", "right" }, sorted.Select(l => l.Text).ToArray());
    }

    [Fact]
    public void SortedBoxes_SortLines_beyond_tolerance_keeps_top_to_bottom()
    {
        // Y differs by 40 (>= 10): top row stays first regardless of X (the else-break path).
        var top = MakeLine("top", Quad(100, 0, 40, 20));
        var bottom = MakeLine("bottom", Quad(10, 40, 40, 20));

        var sorted = SortedBoxes.SortLines(new[] { bottom, top });

        Assert.Equal(new[] { "top", "bottom" }, sorted.Select(l => l.Text).ToArray());
    }

    [Fact]
    public void SortedBoxes_SortLines_does_not_mutate_input()
    {
        var a = MakeLine("a", Quad(100, 0, 40, 20));
        var b = MakeLine("b", Quad(10, 0, 40, 20));
        var input = new List<OcrLine> { a, b };

        SortedBoxes.SortLines(input);

        Assert.Same(a, input[0]);
        Assert.Same(b, input[1]);
    }

    // ---- Rotation-aware reading order (service) -------------------------------------------------------

    [Fact]
    public void SortLinesByReadingOrder_with_180_rotation_sorts_in_uprighted_frame()
    {
        // Page 200×100 was read after a 180° upright; quads are mapped back to the ORIGINAL frame, where
        // the FIRST logical line sits at the BOTTOM. A naive original-frame sort would reverse the text.
        var first = MakeLine("first", Quad(140, 70, 50, 20));   // logical line 1 (bottom-right of original)
        var second = MakeLine("second", Quad(10, 10, 50, 20));  // logical line 2 (top-left of original)

        var naive = StructureService.SortLinesByReadingOrder(new[] { first, second });
        Assert.Equal(new[] { "second", "first" }, naive.Select(l => l.Text).ToArray());

        var aware = StructureService.SortLinesByReadingOrder(new[] { first, second }, appliedRotation: 180, sourceWidth: 200, sourceHeight: 100);
        Assert.Equal(new[] { "first", "second" }, aware.Select(l => l.Text).ToArray());
        // Coordinates themselves stay in the original frame — only the sort key was rotated.
        Assert.Equal(140, aware[0].BoundingBox.MinX, 9);
    }

    [Fact]
    public void SortLinesByReadingOrder_with_zero_rotation_matches_plain_overload()
    {
        var lines = new[]
        {
            MakeLine("b", Quad(0, 40, 50, 20)),
            MakeLine("a", Quad(0, 0, 50, 20)),
        };
        var plain = StructureService.SortLinesByReadingOrder(lines);
        var aware = StructureService.SortLinesByReadingOrder(lines, 0, 100, 100);
        Assert.Equal(plain.Select(l => l.Text), aware.Select(l => l.Text));
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private static OcrPoint[] Quad(double x, double y, double w, double h) => new[]
    {
        new OcrPoint(x, y), new OcrPoint(x + w, y),
        new OcrPoint(x + w, y + h), new OcrPoint(x, y + h),
    };

    private static OcrLine MakeLine(string text, OcrPoint[] quad) => new()
    {
        Text = text,
        Confidence = 1.0,
        BoundingPolygon = quad,
        BoundingBox = OcrBoundingBox.FromPoints(quad),
    };
}
