using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Regression tests for the tilted-line join in <see cref="TextBoxGrouper"/>. Without a vertical bound, the
/// slope rule chained each line of a wide page onto the one above and merged whole paragraphs into one box.
/// </summary>
public class TextBoxGrouperLineJoinTests
{
    private static OcrPoint[] Box(double x1, double y1, double x2, double y2) =>
        new[] { new OcrPoint(x1, y1), new OcrPoint(x2, y1), new OcrPoint(x2, y2), new OcrPoint(x1, y2) };

    [Fact]
    public void Stacked_lines_on_a_wide_page_are_not_chained_into_one_box()
    {
        // Page-001 shape: two full-width text lines, 30 px tall, 55 px apart. The first word of line 2 is
        // ~1170 px left of the last word of line 1, so the slope test alone (0.1 * 1170 = 117 px) would
        // admit the 55 px drop.
        var boxes = new List<OcrPoint[]>();
        for (int i = 0; i < 6; i++) boxes.Add(Box(50 + i * 210, 100, 240 + i * 210, 130));
        for (int i = 0; i < 6; i++) boxes.Add(Box(50 + i * 210, 155, 240 + i * 210, 185));

        var grouped = TextBoxGrouper.Group(boxes, 1400, 400);

        Assert.Equal(2, grouped.Count);
        Assert.True(grouped[0].Max(p => p.Y) < grouped[1].Max(p => p.Y));
        Assert.All(grouped, g => Assert.InRange(g.Max(p => p.Y) - g.Min(p => p.Y), 0, 60));
    }

    [Fact]
    public void A_gently_tilted_line_still_joins_into_one_box()
    {
        // Two adjacent words whose centres drift 20 px over 200 px (slope 0.1): outside the y-centre band,
        // but well under one line height, so the slope rule must still join them.
        var boxes = new List<OcrPoint[]> { Box(0, 100, 190, 130), Box(200, 120, 390, 150) };

        var grouped = TextBoxGrouper.Group(boxes, 600, 300);

        Assert.Single(grouped);
    }
}
