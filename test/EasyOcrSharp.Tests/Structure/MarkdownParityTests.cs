using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure;
using Xunit;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for the Python-parity Markdown rendering added with
/// PP-StructureV3 alignment: numbered-title heading levels (<c>format_title</c>), the geometric
/// paragraph seg-merge of consecutive text blocks (<c>get_seg_flag</c>), and paragraph-aware multi-page
/// concatenation (<c>concatenate_markdown_pages</c>).
/// </summary>
public class MarkdownParityTests
{
    private static OcrBoundingBox Box(double x1, double y1, double x2, double y2) => new(x1, y1, x2, y2);

    private static OcrLine Line(string text, double x1, double y1, double x2, double y2)
        => new() { Text = text, BoundingBox = Box(x1, y1, x2, y2) };

    private static StructureResult Build(params StructureBlock[] blocks)
        => new() { Blocks = blocks, SourceWidth = 600, SourceHeight = 800 };

    // -----------------------------------------------------------------------------------------------------
    // title levels (format_title port)
    // -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Overview", "## Overview")]
    [InlineData("1.2 Overview", "### 1.2 Overview")]
    [InlineData("1.2.3 Deep dive", "#### 1.2.3 Deep dive")]
    public void Title_heading_level_follows_the_numbering_depth(string text, string expected)
    {
        var md = Build(
            new StructureBlock(StructureBlockType.Title, Box(0, 0, 600, 40), Order: 0, Text: text))
            .ToMarkdown();

        Assert.Equal(expected, md);
    }

    [Fact]
    public void Doc_title_renders_as_a_level_one_heading()
    {
        var md = Build(
            new StructureBlock(StructureBlockType.DocTitle, Box(0, 0, 600, 40), Order: 0, Text: "The Paper"))
            .ToMarkdown();

        Assert.Equal("# The Paper", md);
    }

    // -----------------------------------------------------------------------------------------------------
    // seg-merge of consecutive text blocks (get_seg_flag port)
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A multi-line text block whose last line runs to the right edge (so it "ends mid-paragraph").
    /// </summary>
    private static StructureBlock OpenEndedText(string text, int order, double y1, double y2) => new(
        StructureBlockType.Text, Box(100, y1, 500, y2), Order: order, Text: text,
        Lines: new[]
        {
            Line("l1", 100, y1, 500, y1 + 20),
            Line("l2", 100, y1 + 40, 498, y1 + 60),   // last line ends 2px short of the right edge
        });

    /// <summary>
    /// A text block whose first line starts flush at the left edge (so it "continues" a paragraph).
    /// </summary>
    private static StructureBlock FlushStartText(string text, int order, double y1, double y2, double firstLineX = 100)
        => new(
            StructureBlockType.Text, Box(100, y1, 500, y2), Order: order, Text: text,
            Lines: new[]
            {
                Line("l1", firstLineX, y1, 500, y1 + 20),
                Line("l2", firstLineX, y1 + 40, 300, y1 + 60),
            });

    [Fact]
    public void Consecutive_text_blocks_continuing_one_paragraph_concatenate_directly()
    {
        var md = Build(
            OpenEndedText("First part ", 0, 100, 200),
            FlushStartText("second part.", 1, 220, 320))
            .ToMarkdown();

        Assert.Equal("First part second part.", md);
    }

    [Fact]
    public void An_indented_second_block_starts_its_own_paragraph()
    {
        // Its first line starts 50px past the block's left edge — over the 10px indent tolerance — so it
        // is a fresh paragraph, joined with a blank line.
        var md = Build(
            OpenEndedText("First part.", 0, 100, 200),
            FlushStartText("Second paragraph.", 1, 220, 320, firstLineX: 150))
            .ToMarkdown();

        Assert.Equal("First part.\n\nSecond paragraph.", md);
    }

    // -----------------------------------------------------------------------------------------------------
    // multi-page concatenation continuation (concatenate_markdown_pages port)
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_paragraph_split_across_pages_is_joined_with_a_space()
    {
        // Page 1 ends mid-paragraph (last line reaches the right edge), page 2 begins mid-paragraph
        // (first line flush left): the Latin boundary joins with a single space, not a blank line.
        var page1 = Build(OpenEndedText("ends mid", 0, 100, 200));
        var page2 = Build(FlushStartText("continues on.", 0, 100, 200));

        var md = new[] { page1, page2 }.ConcatenateMarkdownPages();

        Assert.Equal("ends mid continues on.", md);
    }

    [Fact]
    public void A_cjk_paragraph_split_across_pages_is_joined_without_a_space()
    {
        var page1 = Build(OpenEndedText("跨页的汉", 0, 100, 200));
        var page2 = Build(FlushStartText("字段落。", 0, 100, 200));

        var md = new[] { page1, page2 }.ConcatenateMarkdownPages();

        Assert.Equal("跨页的汉字段落。", md);
    }

    [Fact]
    public void Complete_paragraphs_on_both_sides_of_a_page_break_stay_separate()
    {
        // Page 1's single-line block never reads as "ends mid-paragraph" (python's single-line quirk keeps
        // seg_end at -inf → true end), so the pages join with a blank line.
        var page1 = Build(new StructureBlock(
            StructureBlockType.Text, Box(100, 100, 500, 130), Order: 0, Text: "A whole paragraph.",
            Lines: new[] { Line("l1", 100, 100, 350, 120) }));
        var page2 = Build(FlushStartText("Next page text.", 0, 100, 200, firstLineX: 150));

        var md = new[] { page1, page2 }.ConcatenateMarkdownPages();

        Assert.Equal("A whole paragraph.\n\nNext page text.", md);
    }
}
