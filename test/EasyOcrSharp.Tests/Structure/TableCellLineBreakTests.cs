using System.Collections.Generic;
using System.Text;
using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure.Table;
using Xunit;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for how
/// <see cref="SlanetTableRecognizer"/> weaves a cell's matched OCR lines into the recovered table HTML.
/// A cell frequently swallows several detection boxes: fragments of one printed line (the detector splits at
/// a wide inter-word gap) and, for a wrapped paragraph, several stacked lines. Joining all of them with
/// spaces — what PaddleOCR's <c>get_pred_html</c> does — collapses a multi-line cell into a single run, so
/// the recognizer rebuilds the visual rows from the boxes' vertical overlap and separates them with
/// <c>&lt;br&gt;</c>. These tests pin that rule.
/// </summary>
public class TableCellLineBreakTests
{
    private static OcrLine Line(string text, double x1, double y1, double x2, double y2) => new()
    {
        Text = text,
        Confidence = 0.99,
        BoundingPolygon = new[]
        {
            new OcrPoint(x1, y1), new OcrPoint(x2, y1),
            new OcrPoint(x2, y2), new OcrPoint(x1, y2),
        },
        BoundingBox = new OcrBoundingBox(x1, y1, x2, y2),
    };

    /// <summary>
    /// Runs the recognizer's cell-text writer over a single cell's matched lines and returns what it emitted.
    /// </summary>
    private static string CellText(params OcrLine[] lines)
    {
        var matched = new Dictionary<int, List<OcrLine>> { [0] = new List<OcrLine>(lines) };
        var sb = new StringBuilder();
        SlanetTableRecognizer.AppendCellText(sb, matched, cellIndex: 0);
        return sb.ToString();
    }

    // Two 20px-tall lines stacked with a 10px gap: no vertical overlap at all.
    private static OcrLine TopLine(string text) => Line(text, 10, 0, 200, 20);
    private static OcrLine BottomLine(string text) => Line(text, 10, 30, 200, 50);

    [Fact]
    public void Stacked_lines_in_one_cell_are_separated_by_a_line_break()
    {
        Assert.Equal("Heading<br>Body text", CellText(TopLine("Heading"), BottomLine("Body text")));
    }

    [Fact]
    public void Fragments_of_the_same_printed_line_are_joined_with_a_space()
    {
        // Same vertical band, split horizontally — one printed line the detector cut in two.
        var left = Line("Available in PyPI", 10, 0, 100, 20);
        var right = Line("and NuGet.", 130, 0, 220, 20);

        Assert.Equal("Available in PyPI and NuGet.", CellText(left, right));
    }

    [Fact]
    public void Rows_are_emitted_top_to_bottom_and_left_to_right_regardless_of_input_order()
    {
        var bottomRight = Line("d", 130, 30, 220, 50);
        var topLeft = Line("a", 10, 0, 100, 20);
        var bottomLeft = Line("c", 10, 30, 100, 50);
        var topRight = Line("b", 130, 0, 220, 20);

        Assert.Equal("a b<br>c d", CellText(bottomRight, topLeft, bottomLeft, topRight));
    }

    [Fact]
    public void A_slightly_misaligned_fragment_still_counts_as_the_same_line()
    {
        // Baselines drift by a few pixels across a printed line; the boxes still overlap almost fully.
        var left = Line("CUDA", 10, 0, 100, 20);
        var right = Line("13.x", 130, 3, 220, 23);

        Assert.Equal("CUDA 13.x", CellText(left, right));
    }

    [Fact]
    public void Barely_overlapping_boxes_are_treated_as_separate_lines()
    {
        // 20px-tall boxes overlapping by 4px — a fifth of the shorter height, well under the 0.5 threshold.
        var upper = Line("first", 10, 0, 200, 20);
        var lower = Line("second", 10, 16, 200, 36);

        Assert.Equal("first<br>second", CellText(upper, lower));
    }

    [Fact]
    public void A_single_line_cell_gains_no_line_break()
    {
        Assert.Equal("Notes", CellText(TopLine("Notes")));
    }

    [Fact]
    public void Cell_text_is_still_html_escaped()
    {
        var first = Line("a < b", 10, 0, 200, 20);
        var second = Line("x & y > z", 10, 30, 200, 50);

        Assert.Equal("a &lt; b<br>x &amp; y &gt; z", CellText(first, second));
    }

    [Fact]
    public void An_unmatched_cell_emits_nothing()
    {
        var matched = new Dictionary<int, List<OcrLine>> { [0] = new List<OcrLine> { TopLine("only cell") } };
        var sb = new StringBuilder();

        SlanetTableRecognizer.AppendCellText(sb, matched, cellIndex: 1);
        SlanetTableRecognizer.AppendCellText(sb, matched, cellIndex: -1);

        Assert.Equal(string.Empty, sb.ToString());
    }

    [Fact]
    public void Grouping_reports_one_row_per_printed_line()
    {
        var rows = SlanetTableRecognizer.GroupIntoVisualLines(new List<OcrLine>
        {
            Line("a", 10, 0, 100, 20),
            Line("b", 130, 0, 220, 20),
            Line("c", 10, 30, 220, 50),
        });

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "a", "b" }, rows[0].ConvertAll(l => l.Text));
        Assert.Equal(new[] { "c" }, rows[1].ConvertAll(l => l.Text));
    }

    [Fact]
    public void Degenerate_zero_height_boxes_stay_on_one_row()
    {
        // Nothing to scale the overlap against; keeping them together beats inventing line breaks.
        var rows = SlanetTableRecognizer.GroupIntoVisualLines(new List<OcrLine>
        {
            Line("a", 10, 5, 100, 5),
            Line("b", 130, 5, 220, 5),
        });

        Assert.Single(rows);
    }
}
