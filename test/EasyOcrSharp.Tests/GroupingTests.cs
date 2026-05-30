using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using Xunit;

namespace EasyOcrSharp.Tests;

public class GroupingTests
{
    private static OcrPoint[] Box(double x0, double y0, double x1, double y1) => new[]
    {
        new OcrPoint(x0, y0), new OcrPoint(x1, y0), new OcrPoint(x1, y1), new OcrPoint(x0, y1),
    };

    private static OcrLine Line(string text, double x0, double y0, double x1, double y1) => new()
    {
        Text = text,
        Confidence = 0.9,
        BoundingPolygon = Box(x0, y0, x1, y1),
        BoundingBox = new OcrBoundingBox(x0, y0, x1, y1),
    };

    [Fact]
    public void TextBoxGrouper_merges_adjacent_words_on_same_line()
    {
        // Two words, same line, small gap relative to height (~20px) -> one merged box.
        var polys = new List<OcrPoint[]>
        {
            Box(10, 10, 60, 30),
            Box(65, 10, 120, 30),
        };
        var grouped = TextBoxGrouper.Group(polys, imageWidth: 200, imageHeight: 100);
        Assert.Single(grouped);
    }

    [Fact]
    public void TextBoxGrouper_keeps_separate_lines_apart()
    {
        var polys = new List<OcrPoint[]>
        {
            Box(10, 10, 120, 30),
            Box(10, 60, 120, 80),
        };
        var grouped = TextBoxGrouper.Group(polys, imageWidth: 200, imageHeight: 200);
        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void ParagraphGrouper_merges_close_stacked_lines()
    {
        var lines = new List<OcrLine>
        {
            Line("first line", 10, 10, 200, 30),
            Line("second line", 10, 34, 200, 54),
        };
        var paras = ParagraphGrouper.Merge(lines);
        Assert.Single(paras);
        Assert.Contains("first line", paras[0].Text);
        Assert.Contains("second line", paras[0].Text);
        Assert.Contains("\n", paras[0].Text);
    }

    [Fact]
    public void ParagraphGrouper_keeps_distant_blocks_separate()
    {
        var lines = new List<OcrLine>
        {
            Line("block one", 10, 10, 200, 30),
            Line("block two", 10, 300, 200, 320),
        };
        var paras = ParagraphGrouper.Merge(lines);
        Assert.Equal(2, paras.Count);
    }
}
