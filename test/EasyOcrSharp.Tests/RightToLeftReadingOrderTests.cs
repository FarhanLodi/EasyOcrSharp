using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Right-to-left reading order: an Arabic or Hebrew page is read starting from the right-most column, and
/// within a row band the right-most fragment comes first. These cover the direction resolution and each of
/// the ordering sites that consumes it.
/// </summary>
public class RightToLeftReadingOrderTests
{
    private static OcrLine Line(string text, double x, double y, double w = 100, double h = 20)
    {
        var box = new OcrBoundingBox { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
        return new OcrLine
        {
            Text = text,
            Confidence = 0.99f,
            BoundingBox = box,
            BoundingPolygon =
            [
                new OcrPoint(box.MinX, box.MinY), new OcrPoint(box.MaxX, box.MinY),
                new OcrPoint(box.MaxX, box.MaxY), new OcrPoint(box.MinX, box.MaxY),
            ],
        };
    }

    // ---- direction resolution ----

    [Theory]
    [InlineData("ar")]
    [InlineData("fa")]
    [InlineData("ur")]
    [InlineData("ug")]
    [InlineData("he")]
    [InlineData("yi")]
    [InlineData("ps")]
    [InlineData("AR")]      // case-insensitive
    [InlineData("ar-SA")]   // tagged codes match on the primary subtag
    [InlineData("fa_IR")]
    public void Right_to_left_scripts_are_detected(string code)
        => Assert.True(ScriptDirection.IsRightToLeft(TextReadingDirection.Auto, [code]));

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("ru")]
    [InlineData("ja")]
    [InlineData("hi")]
    [InlineData("ku")]  // EasyOCR routes Kurdish to the Latin pack: Kurmanji, left-to-right
    public void Left_to_right_scripts_are_not_flagged(string code)
        => Assert.False(ScriptDirection.IsRightToLeft(TextReadingDirection.Auto, [code]));

    [Fact]
    public void A_mixed_language_request_stays_left_to_right()
    {
        // Guessing right-to-left on a bilingual page is worse than leaving the established order alone.
        Assert.False(ScriptDirection.IsRightToLeft(TextReadingDirection.Auto, ["ar", "en"]));
        Assert.True(ScriptDirection.IsRightToLeft(TextReadingDirection.Auto, ["ar", "fa"]));
    }

    [Fact]
    public void An_explicit_direction_overrides_the_languages()
    {
        Assert.True(ScriptDirection.IsRightToLeft(TextReadingDirection.RightToLeft, ["en"]));
        Assert.False(ScriptDirection.IsRightToLeft(TextReadingDirection.LeftToRight, ["ar"]));
    }

    [Fact]
    public void No_languages_means_left_to_right()
    {
        Assert.False(ScriptDirection.IsRightToLeft(TextReadingDirection.Auto, null));
        Assert.False(ScriptDirection.IsRightToLeft(TextReadingDirection.Auto, []));
        Assert.False(ScriptDirection.IsRightToLeft(TextReadingDirection.Auto, ["", "  "]));
    }

    // ---- line ordering ----

    [Fact]
    public void Within_a_row_the_right_most_fragment_leads()
    {
        // An Arabic form row: the label sits to the right of its value.
        var lines = new[] { Line("value", 100, 50), Line("label", 400, 50) };

        var ltr = EasyOcrService.SortLinesByReadingOrder(lines, rightToLeft: false);
        Assert.Equal(["value", "label"], ltr.Select(l => l.Text));

        var rtl = EasyOcrService.SortLinesByReadingOrder(lines, rightToLeft: true);
        Assert.Equal(["label", "value"], rtl.Select(l => l.Text));
    }

    [Fact]
    public void Columns_are_read_right_to_left()
    {
        // Two columns separated by a clear gutter, two rows each.
        var lines = new[]
        {
            Line("L1", 0, 0), Line("L2", 0, 40),
            Line("R1", 400, 0), Line("R2", 400, 40),
        };

        var ltr = EasyOcrService.SortLinesByReadingOrder(lines, rightToLeft: false);
        Assert.Equal(["L1", "L2", "R1", "R2"], ltr.Select(l => l.Text));

        // The right-hand column is the first one read, and each column still runs top-to-bottom.
        var rtl = EasyOcrService.SortLinesByReadingOrder(lines, rightToLeft: true);
        Assert.Equal(["R1", "R2", "L1", "L2"], rtl.Select(l => l.Text));
    }

    [Fact]
    public void Rows_still_run_top_to_bottom_when_right_to_left()
    {
        var lines = new[] { Line("bottom", 0, 200), Line("top", 0, 0), Line("middle", 0, 100) };

        var rtl = EasyOcrService.SortLinesByReadingOrder(lines, rightToLeft: true);
        Assert.Equal(["top", "middle", "bottom"], rtl.Select(l => l.Text));
    }

    [Fact]
    public void A_short_fragment_right_of_a_long_one_still_leads()
    {
        // Keyed on MaxX, not MinX: a narrow box whose right edge is further right comes first even though
        // its left edge is not.
        var lines = new[] { Line("wide", 0, 0, w: 300), Line("narrow", 250, 0, w: 100) };

        var rtl = EasyOcrService.SortLinesByReadingOrder(lines, rightToLeft: true);
        Assert.Equal(["narrow", "wide"], rtl.Select(l => l.Text));
    }

    // ---- paragraph grouping ----

    [Fact]
    public void Paragraph_merging_orders_fragments_right_to_left()
    {
        var lines = new[] { Line("second", 100, 0, w: 80), Line("first", 200, 0, w: 80) };
        var grouping = new GroupingOptions { ParagraphXThreshold = 5.0, ParagraphYThreshold = 5.0 };

        var merged = ParagraphGrouper.Merge(lines, grouping, rightToLeft: true);

        var paragraph = Assert.Single(merged);
        Assert.Equal("first\nsecond", paragraph.Text);
    }

    // ---- XY-cut block ordering ----

    [Fact]
    public void XyCut_emits_columns_right_to_left()
    {
        OcrBoundingBox Box(double x, double y) => new() { MinX = x, MinY = y, MaxX = x + 100, MaxY = y + 50 };

        // index: 0 = left-top, 1 = left-bottom, 2 = right-top, 3 = right-bottom
        var blocks = new[] { Box(0, 0), Box(0, 200), Box(500, 0), Box(500, 200) };

        Assert.Equal([0, 1, 2, 3], Structure.ReadingOrder.XyCutOrderer.Order(blocks));
        Assert.Equal([2, 3, 0, 1], Structure.ReadingOrder.XyCutOrderer.Order(blocks, rightToLeft: true));
    }

    // ---- the default must not disturb existing behaviour ----

    [Fact]
    public void English_pages_are_unchanged_by_the_new_default()
    {
        var lines = new[]
        {
            Line("L1", 0, 0), Line("L2", 0, 40),
            Line("R1", 400, 0), Line("R2", 400, 40),
        };

        // Auto over an LTR language resolves false, which is the pre-existing code path verbatim.
        bool rtl = ScriptDirection.IsRightToLeft(TextReadingDirection.Auto, ["en"]);
        Assert.False(rtl);
        Assert.Equal(
            EasyOcrService.SortLinesByReadingOrder(lines).Select(l => l.Text),
            EasyOcrService.SortLinesByReadingOrder(lines, rtl).Select(l => l.Text));
    }
}
