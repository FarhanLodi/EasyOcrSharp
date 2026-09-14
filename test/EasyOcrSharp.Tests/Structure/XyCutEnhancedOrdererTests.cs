using EasyOcrSharp.Structure.ReadingOrder;
using Xunit;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for <see cref="XyCutEnhancedOrderer"/> — the XY-Cut++
/// reading-order port. The orderer receives layout blocks with raw paddle labels and returns the blocks'
/// <see cref="OrderableBlock.Index"/> values as a permutation in reading order: page furniture is pulled to
/// the edges (headers first, footers last), multi-column bodies are read column by column, and captions stay
/// attached to the figure/table they describe.
/// </summary>
public class XyCutEnhancedOrdererTests
{
    private const float PageW = 1000f;
    private const float PageH = 1400f;

    private static OrderableBlock B(
        int index, float x1, float y1, float x2, float y2, string label = "text", int modelOrder = -1)
        => new(index, x1, y1, x2, y2, label, modelOrder);

    [Fact]
    public void Headers_come_first_and_footers_last_regardless_of_input_order()
    {
        // Supplied deliberately scrambled; geometry alone puts header on top and footer at the bottom,
        // but the orderer must ALSO pin them to the ends by label, ahead/behind the body text.
        var blocks = new[]
        {
            B(3, 100, 1350, 900, 1390, "footer"),
            B(2, 100, 320, 900, 600),
            B(0, 100, 20, 900, 60, "header"),
            B(1, 100, 100, 900, 300),
        };

        var order = XyCutEnhancedOrderer.Order(blocks, PageW, PageH);

        Assert.Equal(new[] { 0, 1, 2, 3 }, order);
    }

    [Fact]
    public void A_two_column_page_is_read_column_by_column()
    {
        // Multi-line paragraph blocks in two columns with a clean 40px gutter, plus one short single-line
        // block per column so the line-height estimate (a documented simplification: derived from block
        // geometry, not OCR lines) recognises the tall blocks as multi-line paragraphs. The left column
        // must be read top-to-bottom before any right-column block. NOTE: a page whose text blocks are all
        // the same height estimates as single-line and falls back to python's max_text_line_num == 1
        // row-major sort — that is faithful xycut_enhanced behaviour, not a bug.
        var blocks = new[]
        {
            B(4, 520, 160, 950, 560),   // R2 (tall paragraph)
            B(0, 50, 100, 480, 140),    // L1 (single line)
            B(3, 520, 100, 950, 140),   // R1 (single line)
            B(2, 50, 580, 480, 980),    // L3 (tall paragraph)
            B(1, 50, 160, 480, 560),    // L2 (tall paragraph)
            B(5, 520, 580, 950, 980),   // R3 (tall paragraph)
        };

        var order = XyCutEnhancedOrderer.Order(blocks, PageW, PageH);

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, order);
    }

    [Fact]
    public void A_caption_attaches_directly_after_its_figure()
    {
        // The caption is narrower than the figure and sits just below it; the vision child-matching pass
        // must keep it glued to the figure even though the input order interleaves other text.
        var blocks = new[]
        {
            B(2, 300, 710, 700, 745, "figure_title"),
            B(3, 100, 800, 900, 1100),
            B(1, 100, 350, 900, 700, "image"),
            B(0, 100, 100, 900, 300),
        };

        var order = XyCutEnhancedOrderer.Order(blocks, PageW, PageH);

        Assert.Equal(new[] { 0, 1, 2, 3 }, order);
    }

    [Fact]
    public void Order_is_deterministic_and_a_permutation_of_the_input_indices()
    {
        // Awkward geometry on purpose: overlapping boxes, a degenerate sliver, arbitrary non-contiguous
        // index values. Whatever the ordering decision, every index must come back exactly once, and two
        // runs over the same input must agree.
        var blocks = new[]
        {
            B(11, 100, 100, 900, 200),
            B(7, 120, 180, 880, 320),            // overlaps the first block
            B(42, 50, 400, 490, 800),
            B(3, 510, 400, 950, 800),
            B(19, 200, 900, 205, 905),           // near-degenerate box
            B(23, 100, 1000, 900, 1300, "table"),
        };

        var first = XyCutEnhancedOrderer.Order(blocks, PageW, PageH);
        var second = XyCutEnhancedOrderer.Order(blocks, PageW, PageH);

        Assert.Equal(first, second);
        Assert.Equal(
            new[] { 3, 7, 11, 19, 23, 42 },
            first.OrderBy(i => i).ToArray());
    }

    [Fact]
    public void Single_block_and_empty_input_are_handled()
    {
        Assert.Empty(XyCutEnhancedOrderer.Order(Array.Empty<OrderableBlock>(), PageW, PageH));
        Assert.Equal(new[] { 5 }, XyCutEnhancedOrderer.Order(new[] { B(5, 10, 10, 100, 50) }, PageW, PageH));
    }
}
