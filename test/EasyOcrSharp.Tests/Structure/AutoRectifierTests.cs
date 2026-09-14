using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure.Seal;
using Xunit;

using OcrPoint = EasyOcrSharp.Models.OcrPoint;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for <see cref="AutoRectifier"/>'s geometry helpers —
/// the curved-seal-text rectification port of PaddleX's <c>get_poly_rect_crop</c> /
/// <c>seal_det_warp.AutoRectifier</c>: polygon IoU (the straight-vs-curved gate), corner clustering
/// (<c>sample_points_on_bbox</c>), head/tail edge detection (<c>find_head_tail</c>) and arc-length
/// resampling (<c>sample_points_on_bbox_bp</c>).
/// </summary>
public class AutoRectifierTests
{
    private static OcrPoint[] Pts(params (double X, double Y)[] points)
        => points.Select(p => new OcrPoint(p.X, p.Y)).ToArray();

    // -----------------------------------------------------------------------------------------------------
    // PolygonIoU — the ≥ 0.7 straight-line gate
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Polygon_iou_of_identical_squares_is_one()
    {
        var square = Pts((0, 0), (10, 0), (10, 10), (0, 10));
        Assert.Equal(1.0, AutoRectifier.PolygonIoU(square, square), 6);
    }

    [Fact]
    public void Polygon_iou_of_disjoint_squares_is_zero()
    {
        var a = Pts((0, 0), (10, 0), (10, 10), (0, 10));
        var b = Pts((20, 0), (30, 0), (30, 10), (20, 10));
        Assert.Equal(0.0, AutoRectifier.PolygonIoU(a, b), 6);
    }

    [Fact]
    public void Polygon_iou_of_half_overlapping_squares_is_one_third()
    {
        // Intersection 1x2, union 3x2 (two 2x2 squares sharing half their area).
        var a = Pts((0, 0), (2, 0), (2, 2), (0, 2));
        var b = Pts((1, 0), (3, 0), (3, 2), (1, 2));
        Assert.Equal(1.0 / 3.0, AutoRectifier.PolygonIoU(a, b), 6);
    }

    // -----------------------------------------------------------------------------------------------------
    // ClusterPolygonPoints — corner clustering
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Cluster_polygon_points_merges_close_points_into_corner_means()
    {
        // Edges: 1, 49, 1, 30, 51 (mean 26.4, short bar 23.76): points 0+1 and 2+3 collapse to their
        // means, the last two stand alone; the closing edge (30) is long, so no wrap-around fold.
        var contour = Pts((0, 0), (1, 0), (50, 0), (51, 0), (51, 30), (0, 30));

        var corners = AutoRectifier.ClusterPolygonPoints(contour);

        Assert.Equal(4, corners.Length);
        Assert.Equal(0.5, corners[0].X, 6);
        Assert.Equal(0.0, corners[0].Y, 6);
        Assert.Equal(50.5, corners[1].X, 6);
        Assert.Equal(new OcrPoint(51, 30), corners[2]);
        Assert.Equal(new OcrPoint(0, 30), corners[3]);
    }

    [Fact]
    public void Cluster_polygon_points_folds_a_short_closing_edge_into_the_first_cluster()
    {
        // Same idea but the ring closes with a SHORT edge (last point next to the first): the final
        // cluster folds into the first, matching python's wrap-around intent.
        // Edges: 50, 50, 50, 48 (all long, mean 49.5); closing edge length 2 — points 4 and 0 are one corner.
        var contour = Pts((0, 0), (50, 0), (50, 50), (0, 50), (0, 2));

        var corners = AutoRectifier.ClusterPolygonPoints(contour);

        Assert.Equal(4, corners.Length);
        Assert.Equal(0.0, corners[0].X, 6);
        Assert.Equal(1.0, corners[0].Y, 6);
        Assert.Equal(new OcrPoint(50, 0), corners[1]);
        Assert.Equal(new OcrPoint(50, 50), corners[2]);
        Assert.Equal(new OcrPoint(0, 50), corners[3]);
    }

    // -----------------------------------------------------------------------------------------------------
    // FindHeadTail — quad branch
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Find_head_tail_picks_the_short_ends_of_a_wide_quad()
    {
        // A wide horizontal rectangle: the head/tail are the short vertical ends (left edge 3→0, right
        // edge 1→2), which makes the top and bottom chains the sidelines.
        var quad = Pts((0, 0), (100, 0), (100, 20), (0, 20));

        var (headStart, headEnd, tailStart, tailEnd) = AutoRectifier.FindHeadTail(quad, orientationThr: 2.0);

        Assert.Equal((3, 0, 1, 2), (headStart, headEnd, tailStart, tailEnd));
    }

    // -----------------------------------------------------------------------------------------------------
    // ResampleLine — arc-length resampling
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Resample_line_returns_n_plus_one_evenly_spaced_points()
    {
        var line = Pts((0, 0), (40, 0), (100, 0));   // uneven input spacing, total length 100

        var resampled = AutoRectifier.ResampleLine(line, 4);

        Assert.Equal(5, resampled.Length);
        for (int i = 0; i < resampled.Length; i++)
        {
            Assert.Equal(25.0 * i, resampled[i].X, 4);
            Assert.Equal(0.0, resampled[i].Y, 6);
        }
    }

    [Fact]
    public void Resample_line_keeps_the_original_endpoints_on_a_bent_polyline()
    {
        var line = Pts((0, 0), (30, 40));   // single segment of length 50

        var resampled = AutoRectifier.ResampleLine(line, 2);

        Assert.Equal(3, resampled.Length);
        Assert.Equal(new OcrPoint(0, 0), resampled[0]);
        Assert.Equal(15.0, resampled[1].X, 4);
        Assert.Equal(20.0, resampled[1].Y, 4);
        Assert.Equal(new OcrPoint(30, 40), resampled[^1]);
    }
}
