using EasyOcrSharp.Structure.Text;
using Xunit;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for <see cref="TextLineAssembler"/> — the port of
/// PaddleX's line/paragraph text assembly. Spans (OCR line boxes or recognized formulas, page coordinates)
/// are grouped into visual lines, joined with language-aware spacing, soft hyphens are collapsed, real
/// paragraph breaks emit <c>'\n'</c>, and the per-label delimiter rules (<c>doc_title</c>, <c>content</c>)
/// override the default flow.
/// </summary>
public class TextLineAssemblerTests
{
    private static BlockSpan S(float x1, float y1, float x2, float y2, string text, bool formula = false)
        => new(x1, y1, x2, y2, text, formula);

    [Fact]
    public void Soft_hyphen_at_a_line_wrap_is_stripped_and_the_halves_joined()
    {
        // Line 1 runs to the block's right edge and ends "exam-"; line 2 starts lowercase: the '-' is a
        // hyphenation artifact, so the two halves concatenate with no separator.
        var spans = new[]
        {
            S(0, 0, 398, 20, "This is an exam-"),
            S(0, 22, 150, 42, "ple of it."),
        };

        var text = TextLineAssembler.Assemble(spans, "text", 0, 0, 400, 50);

        Assert.Equal("This is an example of it.", text);
    }

    [Fact]
    public void A_wrapped_latin_line_joins_the_next_with_a_single_space()
    {
        // Line 1 reaches the right edge (2px short) so the wrap is a continuation, and both boundary
        // characters are Latin letters: joined by one space, not a newline.
        var spans = new[]
        {
            S(0, 0, 398, 20, "The quick brown fox"),
            S(0, 22, 300, 42, "jumps over."),
        };

        var text = TextLineAssembler.Assemble(spans, "text", 0, 0, 400, 50);

        Assert.Equal("The quick brown fox jumps over.", text);
    }

    [Fact]
    public void A_line_ending_far_short_of_the_right_edge_breaks_the_paragraph()
    {
        // Line 1 ends 250px short of the 400px block — beyond even the first line's 50% allowance — so a
        // real paragraph break ('\n') is emitted.
        var spans = new[]
        {
            S(0, 0, 150, 20, "First paragraph."),
            S(0, 22, 390, 42, "Second one starts."),
        };

        var text = TextLineAssembler.Assemble(spans, "text", 0, 0, 400, 50);

        Assert.Equal("First paragraph.\nSecond one starts.", text);
    }

    [Fact]
    public void A_formula_span_is_spliced_into_the_line_wrapped_in_dollars()
    {
        var spans = new[]
        {
            S(0, 0, 100, 20, "energy"),
            S(105, 0, 200, 20, "E=mc^2", formula: true),
            S(205, 0, 300, 20, "holds"),
        };

        var text = TextLineAssembler.Assemble(spans, "text", 0, 0, 300, 20);

        Assert.Equal("energy $E=mc^2$ holds", text);
    }

    [Fact]
    public void Doc_title_lines_are_joined_with_spaces()
    {
        // doc_title flows onto one line whatever the geometry says (setting.py per-label delimiter " ").
        var spans = new[]
        {
            S(0, 0, 100, 20, "GREAT"),
            S(0, 30, 100, 50, "TITLE"),
        };

        var text = TextLineAssembler.Assemble(spans, "doc_title", 0, 0, 400, 60);

        Assert.Equal("GREAT TITLE", text);
    }

    [Fact]
    public void Content_lines_keep_one_entry_per_line()
    {
        // content (table of contents) joins every visual line with '\n' regardless of line-end geometry.
        var spans = new[]
        {
            S(0, 0, 380, 20, "Chapter 1 ......... 5"),
            S(0, 30, 380, 50, "Chapter 2 ......... 9"),
        };

        var text = TextLineAssembler.Assemble(spans, "content", 0, 0, 400, 60);

        Assert.Equal("Chapter 1 ......... 5\nChapter 2 ......... 9", text);
    }

    [Fact]
    public void Spans_on_one_visual_line_are_ordered_left_to_right()
    {
        // Two fragments of one line supplied right-fragment-first must still read left to right.
        var spans = new[]
        {
            S(210, 0, 400, 20, "world"),
            S(0, 2, 200, 22, "Hello"),
        };

        var text = TextLineAssembler.Assemble(spans, "text", 0, 0, 400, 25);

        Assert.Equal("Hello world", text);
    }

    [Fact]
    public void Cjk_adjacency_gets_no_space()
    {
        // Neither boundary character is a Latin letter/digit/'$': fragments join with no separator.
        var spans = new[]
        {
            S(0, 0, 100, 20, "你好"),
            S(105, 0, 200, 20, "世界"),
        };

        var text = TextLineAssembler.Assemble(spans, "text", 0, 0, 200, 20);

        Assert.Equal("你好世界", text);
    }

    [Fact]
    public void Empty_and_whitespace_spans_contribute_nothing()
    {
        Assert.Equal(string.Empty, TextLineAssembler.Assemble(Array.Empty<BlockSpan>(), "text", 0, 0, 100, 20));
        Assert.Equal(
            "kept",
            TextLineAssembler.Assemble(
                new[] { S(0, 0, 50, 20, "   "), S(60, 0, 100, 20, "kept") }, "text", 0, 0, 100, 20));
    }
}
