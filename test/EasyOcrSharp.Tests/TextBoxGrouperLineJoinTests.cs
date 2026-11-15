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

    [Fact]
    public void Tightly_set_lines_are_not_joined_by_their_far_ends()
    {
        // Monthly_Report_With_Image.pdf shape: 32 px boxes on lines 34 px apart. The last word of line 2 sits
        // ~750 px right of the lowest word of line 1 and 30 px below it, so a test against that one box (dy
        // under one line height, gentle slope) joined the lines and both were read as one box of noise.
        var boxes = new List<OcrPoint[]>();
        for (int i = 0; i < 9; i++) boxes.Add(Box(214 + i * 125, 382, 324 + i * 125, 414));
        for (int i = 0; i < 9; i++) boxes.Add(Box(214 + i * 135, 416, 334 + i * 135, 442));

        var grouped = TextBoxGrouper.Group(boxes, 1500, 600);

        Assert.Equal(2, grouped.Count);
        Assert.All(grouped, g => Assert.InRange(g.Max(p => p.Y) - g.Min(p => p.Y), 0, 40));
    }

    [Fact]
    public void Lines_of_a_rotated_page_stay_apart()
    {
        // 005_plain_text.png shape: a page rotated ~1.5°, lines 57 px apart. Line 2's right end overlaps line
        // 1's left end vertically, so only the lines' own courses tell them apart.
        var boxes = new List<OcrPoint[]>();
        foreach (double baseline in new[] { 158.0, 215.0 })
        {
            for (int i = 0; i < 12; i++)
            {
                double x = 60 + i * 100, y = baseline - 0.026 * (x - 60);
                boxes.Add(Box(x, y - 16, x + 90, y + 16));
            }
        }

        var grouped = TextBoxGrouper.Group(boxes, 1400, 400);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void A_long_tilted_line_is_cut_along_its_course()
    {
        // An axis-aligned box around this line would be ~60 px tall for 28 px text and take in the lines
        // above and below; the region follows the line instead.
        var boxes = new List<OcrPoint[]>();
        for (int i = 0; i < 12; i++)
        {
            double x = 60 + i * 100, y = 400 - 0.026 * (x - 60);
            boxes.Add(Box(x, y - 14, x + 90, y + 14));
        }

        var region = Assert.Single(TextBoxGrouper.Group(boxes, 1400, 600));

        Assert.True(region[0].Y - region[1].Y > 20, "the top edge should rise with the line");
        Assert.InRange(region[3].Y - region[0].Y, 28, 40);   // thickness at the left edge: the text plus margin
        Assert.InRange(region[2].Y - region[1].Y, 28, 40);   // and at the right edge
    }

    [Fact]
    public void A_level_line_keeps_its_axis_aligned_box()
    {
        var boxes = new List<OcrPoint[]>();
        for (int i = 0; i < 8; i++) boxes.Add(Box(50 + i * 120, 100 + (i % 2), 160 + i * 120, 130 + (i % 2)));

        var region = Assert.Single(TextBoxGrouper.Group(boxes, 1200, 300));

        Assert.Equal(region[0].Y, region[1].Y);
        Assert.Equal(region[2].Y, region[3].Y);
        Assert.Equal(region[0].X, region[3].X);
    }

    [Fact]
    public void A_one_word_line_does_not_absorb_the_next_line_through_its_neighbour()
    {
        // A one-word heading cannot fit a course, so the neighbour test applies; the next line's far word is
        // under one line height lower but shares no height with the heading.
        var boxes = new List<OcrPoint[]> { Box(100, 100, 300, 136) };
        for (int i = 0; i < 6; i++) boxes.Add(Box(100 + i * 150, 135, 230 + i * 150, 163));

        var grouped = TextBoxGrouper.Group(boxes, 1200, 300);

        Assert.Equal(2, grouped.Count);
    }
}
