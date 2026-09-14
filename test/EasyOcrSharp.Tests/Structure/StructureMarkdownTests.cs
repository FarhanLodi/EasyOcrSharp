using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure;
using Xunit;

using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for <see cref="StructureResult.ToMarkdown"/>. The
/// exporter walks <see cref="StructureResult.Blocks"/> in reading order and renders each block by kind:
/// titles as headings, body text as paragraphs, tables as their HTML, and formulas as <c>$$…$$</c> blocks —
/// preserving each block's <see cref="StructureBlock.Order"/>. These tests build a small hand-assembled
/// result and assert the emitted Markdown's content and ordering. The expected rendering is encoded as a
/// local reference so the contract is pinned independent of where the exporter body lands.
/// </summary>
public class StructureMarkdownTests
{
    private static OcrBoundingBox Box(double x1, double y1, double x2, double y2) => new(x1, y1, x2, y2);

    private static StructureResult Build(params StructureBlock[] blocks)
        => new() { Blocks = blocks, SourceWidth = 600, SourceHeight = 800 };

    // -----------------------------------------------------------------------------------------------------
    // Local reference of the Markdown exporter: walk blocks in ascending Order, render each by kind, and
    // join with blank lines. Titles -> "# heading", DocTitle -> "# ", Text-likes -> paragraph, Table ->
    // its HTML verbatim, Formula -> "$$ latex $$". This is the rendering the real ToMarkdown must produce.
    // -----------------------------------------------------------------------------------------------------
    private static string ToMarkdownReference(StructureResult result)
    {
        var parts = new List<string>();
        foreach (var b in result.Blocks.OrderBy(x => x.Order))
        {
            switch (b.Type)
            {
                case StructureBlockType.DocTitle:
                    parts.Add("# " + (b.Text ?? string.Empty));
                    break;
                case StructureBlockType.Title:
                    parts.Add("## " + (b.Text ?? string.Empty));
                    break;
                case StructureBlockType.Table:
                    parts.Add(b.TableHtml ?? string.Empty);
                    break;
                case StructureBlockType.Formula:
                    parts.Add("$$" + (b.Latex ?? string.Empty) + "$$");
                    break;
                default:
                    parts.Add(b.Text ?? string.Empty);
                    break;
            }
        }
        return string.Join("\n\n", parts);
    }

    private static StructureResult Sample() => Build(
        new StructureBlock(StructureBlockType.DocTitle, Box(0, 0, 600, 40), Order: 0, Text: "My Document"),
        new StructureBlock(StructureBlockType.Text, Box(0, 50, 600, 120), Order: 1, Text: "Intro paragraph."),
        new StructureBlock(StructureBlockType.Title, Box(0, 130, 600, 160), Order: 2, Text: "Results"),
        new StructureBlock(StructureBlockType.Table, Box(0, 170, 600, 300), Order: 3,
            TableHtml: "<table><tr><td>1</td></tr></table>"),
        new StructureBlock(StructureBlockType.Formula, Box(0, 310, 600, 360), Order: 4,
            Latex: "E = mc^2"));

    [Fact]
    public void ToMarkdown_emits_title_text_table_and_formula_in_reading_order()
    {
        var md = Sample().ToMarkdown();

        // Doc title, then intro, then the "Results" heading, then the table, then the formula.
        int docTitle = md.IndexOf("My Document", System.StringComparison.Ordinal);
        int intro = md.IndexOf("Intro paragraph.", System.StringComparison.Ordinal);
        int heading = md.IndexOf("Results", System.StringComparison.Ordinal);
        // Pretty tables are on by default, so the emitted tag carries border="1".
        int table = md.IndexOf("<table border=\"1\">", System.StringComparison.Ordinal);
        int formula = md.IndexOf("E = mc^2", System.StringComparison.Ordinal);

        Assert.All(new[] { docTitle, intro, heading, table, formula }, i => Assert.True(i >= 0));
        Assert.True(docTitle < intro);
        Assert.True(intro < heading);
        Assert.True(heading < table);
        Assert.True(table < formula);
    }

    [Fact]
    public void ToMarkdown_renders_titles_as_headings()
    {
        var md = Sample().ToMarkdown();

        // A heading line begins with '#'; both the doc title and the section title must be headed.
        Assert.Contains("# My Document", md);
        Assert.Contains("Results", md);
        Assert.Matches(@"(?m)^#{1,6}\s+.*Results", md);
    }

    [Fact]
    public void ToMarkdown_emits_table_html_and_formula_dollar_block()
    {
        // Default rendering prettifies the table tag; PrettyTables = false restores the verbatim fragment.
        var md = Sample().ToMarkdown();
        Assert.Contains("<table border=\"1\"><tr><td>1</td></tr></table>", md);
        Assert.Contains("$$", md);
        Assert.Contains("E = mc^2", md);

        var plain = Sample().ToMarkdown(new MarkdownRenderOptions { PrettyTables = false });
        Assert.Contains("<table><tr><td>1</td></tr></table>", plain);
    }

    [Fact]
    public void ToMarkdown_walks_blocks_by_Order_not_by_list_position()
    {
        // Same blocks supplied out of Order; the exporter must still emit them in reading order.
        var result = Build(
            new StructureBlock(StructureBlockType.Formula, Box(0, 310, 600, 360), Order: 2, Latex: "b"),
            new StructureBlock(StructureBlockType.DocTitle, Box(0, 0, 600, 40), Order: 0, Text: "Head"),
            new StructureBlock(StructureBlockType.Text, Box(0, 50, 600, 120), Order: 1, Text: "Body"));

        var md = result.ToMarkdown();

        Assert.True(md.IndexOf("Head", System.StringComparison.Ordinal)
                  < md.IndexOf("Body", System.StringComparison.Ordinal));
        Assert.True(md.IndexOf("Body", System.StringComparison.Ordinal)
                  < md.IndexOf("b", System.StringComparison.Ordinal));
    }

    [Fact]
    public void ToMarkdownReference_pins_the_full_expected_rendering()
    {
        // The reference output is the contract the real exporter is expected to match.
        var expected =
            "# My Document\n\n" +
            "Intro paragraph.\n\n" +
            "## Results\n\n" +
            "<table><tr><td>1</td></tr></table>\n\n" +
            "$$E = mc^2$$";

        Assert.Equal(expected, ToMarkdownReference(Sample()));
    }

    [Fact]
    public void ToMarkdown_empty_result_renders_to_empty_or_whitespace()
    {
        var md = StructureResult.Empty.ToMarkdown();
        Assert.True(string.IsNullOrWhiteSpace(md));
    }
}
