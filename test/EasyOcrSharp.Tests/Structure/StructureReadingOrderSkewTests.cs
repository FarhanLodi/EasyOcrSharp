using EasyOcrSharp.Structure.Engine;
using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure.Engine.Services;
using Xunit;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;
using OcrWord = EasyOcrSharp.Models.OcrWord;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure tests for the reading-order fixes: row chaining in the service sort, the quad-based skew
/// estimate, and skew-aware <see cref="SortedBoxes"/>, <see cref="LineGrouper"/> and
/// <see cref="ParagraphGrouper"/> — including the guarantee that unskewed input keeps the exact Python
/// <c>sorted_boxes</c> order.
/// </summary>
public class StructureReadingOrderSkewTests
{
    /// <summary>A w×h rectangle whose top-left corner is at (x, y), rotated clockwise by angle° about that corner.</summary>
    private static OcrLine Quad(string text, double x, double y, double w, double h, double angleDegrees)
    {
        double rad = angleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        var p0 = new OcrPoint(x, y);
        var p1 = new OcrPoint(x + (w * cos), y + (w * sin));
        var p3 = new OcrPoint(x - (h * sin), y + (h * cos));
        var p2 = new OcrPoint(p1.X - (h * sin), p1.Y + (h * cos));
        var poly = new[] { p0, p1, p2, p3 };
        return new OcrLine { Text = text, Confidence = 1, BoundingPolygon = poly, BoundingBox = OcrBoundingBox.FromPoints(poly) };
    }

    /// <summary>
    /// rows × cols word quads on rows tilted by angle°; pitch 28px, words 100×20 every 125px. At 2–4° the
    /// drop across a row (~30–60px) exceeds the row pitch, so axis-aligned ordering interleaves rows.
    /// </summary>
    private static List<OcrLine> SkewedPage(double angle, int rows = 3, int cols = 8, double pitch = 28)
    {
        double rad = angle * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        var words = new List<OcrLine>();
        for (int r = 0; r < rows; r++)
        {
            double rowX = 60 - (r * pitch * sin);
            double rowY = 40 + (r * pitch * cos);
            for (int c = 0; c < cols; c++)
            {
                words.Add(Quad($"r{r}c{c}", rowX + (c * 125 * cos), rowY + (c * 125 * sin), 100, 20, angle));
            }
        }
        return words;
    }

    private static string[] RowMajor(int rows = 3, int cols = 8)
        => Enumerable.Range(0, rows).SelectMany(r => Enumerable.Range(0, cols).Select(c => $"r{r}c{c}")).ToArray();

    private static List<OcrLine> Shuffled(List<OcrLine> lines)
    {
        var rng = new Random(1234);
        return lines.OrderBy(_ => rng.Next()).ToList();
    }

    [Fact]
    public void Service_sort_keeps_boxes_straddling_a_band_edge_on_one_row()
    {
        // tol = 0.5 × 20 = 10: MinY 14 and 16 used to round into different bands (10 vs 20), putting
        // "right" first. The 20px gap is below the 30px column gutter, so this is one column.
        var right = Quad("right", 100, 14, 80, 20, 0);
        var left = Quad("left", 0, 16, 80, 20, 0);
        var below = Quad("below", 0, 60, 80, 20, 0);

        var ordered = StructureService.SortLinesByReadingOrder(new[] { below, right, left });

        Assert.Equal(new[] { "left", "right", "below" }, ordered.Select(l => l.Text));
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(3.0)]
    [InlineData(-3.0)]
    [InlineData(4.0)]
    public void Skew_is_estimated_from_elongated_quads(double angle)
    {
        Assert.Equal(angle, TextSkew.Estimate(SkewedPage(angle)), 3);
    }

    [Fact]
    public void Skew_estimate_is_conservative()
    {
        // Fewer than 5 quads.
        Assert.Equal(0, TextSkew.Estimate(SkewedPage(3, rows: 1, cols: 4)));

        // Inconsistent angles (MAD ≥ 2°).
        var mixed = new[] { -8.0, 8.0, -6.0, 6.0, -7.0, 7.0 }
            .Select((a, i) => Quad($"m{i}", 0, i * 40, 100, 20, a)).ToList();
        Assert.Equal(0, TextSkew.Estimate(mixed));

        // Squarish boxes and vertical lines do not vote.
        var noVotes = Enumerable.Range(0, 6).Select(i => Quad($"s{i}", 0, i * 40, 30, 20, 3))
            .Concat(Enumerable.Range(0, 6).Select(i => Quad($"v{i}", i * 40, 0, 20, 100, 0)))
            .ToList();
        Assert.Equal(0, TextSkew.Estimate(noVotes));
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(3.0)]
    [InlineData(4.0)]
    public void SortedBoxes_orders_skewed_rows_row_by_row(double angle)
    {
        var page = Shuffled(SkewedPage(angle));

        // Axis-aligned sorted_boxes interleaves the tilted rows (this is what the fix is for) ...
        Assert.NotEqual(RowMajor(), SortedBoxes.SortLines(page).Select(l => l.Text).ToArray());
        // ... while the deskewed keys read each row left to right.
        Assert.Equal(RowMajor(), SortedBoxes.SortLinesSkewAware(page).Select(l => l.Text).ToArray());
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(-2.5)]
    public void Service_sort_orders_skewed_rows_row_by_row(double angle)
    {
        var ordered = StructureService.SortLinesByReadingOrder(Shuffled(SkewedPage(angle)));
        Assert.Equal(RowMajor(), ordered.Select(l => l.Text).ToArray());
    }

    [Fact]
    public void Below_half_a_degree_the_python_sorted_boxes_order_is_untouched()
    {
        var rng = new Random(42);
        foreach (double angle in new[] { 0.0, 0.3, -0.45 })
        {
            var page = SkewedPage(angle, rows: 6, cols: 7);
            // Jitter positions so the bubble pass and ties are exercised.
            var jittered = page.Select(l => Quad(l.Text, l.BoundingPolygon[0].X + rng.Next(-6, 7), l.BoundingPolygon[0].Y + rng.Next(-6, 7), 100, 20, angle)).ToList();
            jittered = Shuffled(jittered);

            var python = SortedBoxes.SortLines(jittered);
            var aware = SortedBoxes.SortLinesSkewAware(jittered);
            Assert.Equal(python.Select(l => l.Text), aware.Select(l => l.Text));

            var viaSort = jittered.ToList();
            SortedBoxes.SortDeskewed(viaSort, SortedBoxes.KeyPoint, 0.49);
            Assert.Equal(python.Select(l => l.Text), viaSort.Select(l => l.Text));
        }
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(4.0)]
    public void LineGrouper_merges_each_skewed_row_into_one_line(double angle)
    {
        var page = SortedBoxes.SortLines(SkewedPage(angle));

        var merged = LineGrouper.Merge(page);

        Assert.Equal(3, merged.Count);
        for (int r = 0; r < 3; r++)
        {
            Assert.Equal(string.Join(" ", Enumerable.Range(0, 8).Select(c => $"r{r}c{c}")), merged[r].Text);
        }
    }

    [Fact]
    public void ParagraphGrouper_splits_skewed_paragraphs_at_the_real_gap()
    {
        const double angle = 3.0;
        double rad = angle * Math.PI / 180.0;
        var rows = new List<OcrLine>();
        foreach (int r in new[] { 0, 1, 2, 5, 6, 7 })
        {
            rows.Add(Quad($"row{r}", 60 - (r * 28 * Math.Sin(rad)), 40 + (r * 28 * Math.Cos(rad)), 900, 20, angle));
        }

        var paragraphs = ParagraphGrouper.Merge(Shuffled(rows));

        Assert.Equal(2, paragraphs.Count);
        Assert.Contains(paragraphs, p => p.Text == "row0\nrow1\nrow2");
        Assert.Contains(paragraphs, p => p.Text == "row5\nrow6\nrow7");
    }
}
