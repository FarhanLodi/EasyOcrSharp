using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure.Engine.Services;
using Xunit;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for how <see cref="OcrResult.FullText"/> joins the
/// recognized blocks. Under <see cref="TextGrouping.Paragraph"/> each block is already a multi-line
/// paragraph (the merged lines are newline-joined by the paragraph grouper), so blocks must be separated by
/// a BLANK line: with a single newline the paragraph boundary is indistinguishable from the line breaks
/// inside a paragraph and the grouping carries no information into the text at all. Word/line grouping keeps
/// one newline per line, where every newline already means the same thing.
/// </summary>
public class FullTextGroupingTests
{
    private static OcrLine Line(string text) => new()
    {
        Text = text,
        Confidence = 0.99,
        BoundingBox = new OcrBoundingBox(0, 0, 100, 20),
    };

    private static string FullText(TextGrouping grouping, params string[] blocks)
        => StructureService.BuildFullText(blocks.Select(Line).ToList(), grouping);

    [Fact]
    public void Paragraph_blocks_are_separated_by_a_blank_line()
    {
        string text = FullText(TextGrouping.Paragraph, "first line\nsecond line", "next paragraph");

        Assert.Equal("first line\nsecond line\n\nnext paragraph", text);
    }

    [Fact]
    public void Line_grouping_keeps_one_newline_per_line()
    {
        string text = FullText(TextGrouping.Line, "first", "second", "third");

        Assert.Equal("first\nsecond\nthird", text);
    }

    [Fact]
    public void Word_grouping_keeps_one_newline_per_box()
    {
        Assert.Equal("a\nb", FullText(TextGrouping.Word, "a", "b"));
    }

    [Fact]
    public void A_blocks_own_trailing_newline_does_not_widen_the_gap()
    {
        string text = FullText(TextGrouping.Paragraph, "para one\n", "para two");

        Assert.Equal("para one\n\npara two", text);
    }

    [Fact]
    public void Empty_blocks_are_skipped_without_leaving_a_stray_separator()
    {
        string text = FullText(TextGrouping.Paragraph, "first", "", "last");

        Assert.Equal("first\n\nlast", text);
    }

    [Fact]
    public void A_single_paragraph_gains_no_trailing_separator()
    {
        Assert.Equal("only", FullText(TextGrouping.Paragraph, "only"));
    }
}
