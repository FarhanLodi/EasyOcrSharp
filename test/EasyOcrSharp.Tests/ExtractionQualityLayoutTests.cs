using EasyOcrSharp.Extraction;
using EasyOcrSharp.Models;
using Xunit;
using static EasyOcrSharp.Tests.ExtractionTestPages;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Model-free tests for <see cref="OcrQualityExtensions"/> (confidence statistics and review lists) and
/// <see cref="LayoutTextExtensions.ToLayoutText"/> (pdftotext -layout style rendering with CJK and RTL).
/// </summary>
public class ExtractionQualityLayoutTests
{
    // ---- quality ----

    private static OcrResult QualityPage() => Page(
        Line("Hello world", 0, 0, 110, 20, confidence: 0.9),   // 10 non-whitespace characters
        Line("ab", 0, 30, 20, 20, confidence: 0.5),            // 2
        Line("xyz", 0, 60, 30, 20, confidence: 0.7));          // 3

    [Fact]
    public void GetQuality_computes_mean_weighted_and_minimum_confidence()
    {
        var quality = QualityPage().GetQuality();

        Assert.Equal(3, quality.LineCount);
        Assert.Equal(15, quality.CharacterCount);
        Assert.Equal(0.7, quality.MeanConfidence, 10);
        Assert.Equal((0.9 * 10 + 0.5 * 2 + 0.7 * 3) / 15, quality.CharacterWeightedConfidence, 10);
        Assert.Equal(0.5, quality.MinConfidence, 10);
    }

    [Fact]
    public void GetQuality_counts_low_confidence_lines_and_their_characters()
    {
        var quality = QualityPage().GetQuality(lowConfidenceThreshold: 0.8);

        Assert.Equal(2, quality.LowConfidenceLineCount);
        Assert.Equal(5, quality.LowConfidenceCharacterCount);
        Assert.Equal(2.0 / 3, quality.LowConfidenceRatio, 10);
        Assert.Equal(0.8, quality.LowConfidenceThreshold);

        var strict = QualityPage().GetQuality(lowConfidenceThreshold: 0.95);
        Assert.Equal(3, strict.LowConfidenceLineCount);
    }

    [Fact]
    public void GetQuality_threshold_is_strict_less_than()
    {
        var quality = QualityPage().GetQuality(lowConfidenceThreshold: 0.7);
        Assert.Equal(1, quality.LowConfidenceLineCount); // only the 0.5 line; 0.7 is not below 0.7
    }

    [Fact]
    public void GetQuality_weights_a_line_without_characters_as_one()
    {
        var page = Page(Line(" ", 0, 0, 10, 20, confidence: 0.2), Line("abc", 0, 30, 30, 20, confidence: 0.8));
        var quality = page.GetQuality();

        Assert.Equal(3, quality.CharacterCount);
        Assert.Equal((0.2 * 1 + 0.8 * 3) / 4, quality.CharacterWeightedConfidence, 10);
    }

    [Fact]
    public void GetQuality_of_an_empty_result_is_all_zero_but_keeps_the_threshold()
    {
        var quality = OcrResult.Empty.GetQuality(0.6);

        Assert.Equal(0, quality.LineCount);
        Assert.Equal(0, quality.MeanConfidence);
        Assert.Equal(0, quality.CharacterWeightedConfidence);
        Assert.Equal(0, quality.MinConfidence);
        Assert.Equal(0, quality.LowConfidenceRatio);
        Assert.Equal(0.6, quality.LowConfidenceThreshold);
    }

    [Fact]
    public void GetLinesForReview_returns_low_confidence_lines_in_reading_order()
    {
        var page = QualityPage();
        var review = page.GetLinesForReview(0.8);

        Assert.Equal(new[] { page.Lines[1], page.Lines[2] }, review);
        Assert.Empty(page.GetLinesForReview(0.5));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Quality_thresholds_outside_zero_to_one_are_rejected(double threshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityPage().GetQuality(threshold));
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityPage().GetLinesForReview(threshold));
    }

    [Fact]
    public void Quality_helpers_reject_a_null_result()
    {
        Assert.Throws<ArgumentNullException>(() => ((OcrResult)null!).GetQuality());
        Assert.Throws<ArgumentNullException>(() => ((OcrResult)null!).GetLinesForReview());
    }

    // ---- layout text ----

    [Fact]
    public void ToLayoutText_keeps_columns_aligned()
    {
        // 10 px per character everywhere, so the cell width is 10 px and x = 200 is column 20.
        var page = Page(
            Line("Item", 0, 0, 40, 20),
            Line("Price", 200, 0, 50, 20),
            Line("Coffee", 0, 30, 60, 20),
            Line("3.50", 200, 30, 40, 20));

        string expected =
            "Item" + new string(' ', 16) + "Price" + "\n" +
            "Coffee" + new string(' ', 14) + "3.50";
        Assert.Equal(expected, page.ToLayoutText());
    }

    [Fact]
    public void ToLayoutText_turns_vertical_gaps_into_capped_blank_lines()
    {
        var page = Page(
            Line("Coffee", 0, 0, 60, 20),
            Line("Total", 0, 70, 50, 20)); // 50 px gap = 2.5 line heights

        Assert.Equal("Coffee\n\nTotal", page.ToLayoutText());
        Assert.Equal("Coffee\nTotal", page.ToLayoutText(new LayoutTextOptions { MaxBlankLines = 0 }));
        Assert.Equal("Coffee\n\n\nTotal", page.ToLayoutText(new LayoutTextOptions { MaxBlankLines = 5 }));
    }

    [Fact]
    public void ToLayoutText_counts_cjk_characters_as_two_columns()
    {
        // "合计" is 4 cells over 40 px; "100" at x = 100 is column 10, so 6 spaces follow the two ideographs.
        var page = Page(
            Line("合计", 0, 0, 40, 20),
            Line("100", 100, 0, 30, 20));

        Assert.Equal("合计" + new string(' ', 6) + "100", page.ToLayoutText());
    }

    [Fact]
    public void ToLayoutText_right_aligns_rtl_lines_at_their_right_edge()
    {
        const string arabic = "المبلغ"; // 6 cells
        var page = Page(
            Line(arabic, 100, 0, 100, 20),          // right edge at column 20
            Line("Hello world", 0, 30, 110, 20));

        var options = new LayoutTextOptions { CharacterWidth = 10 };
        Assert.Equal(new string(' ', 14) + arabic + "\nHello world", page.ToLayoutText(options));

        var leftAnchored = options with { RightAlignRtlLines = false };
        Assert.Equal(new string(' ', 10) + arabic + "\nHello world", page.ToLayoutText(leftAnchored));
    }

    [Fact]
    public void ToLayoutText_never_lets_lines_in_a_row_overlap()
    {
        var page = Page(Line("AAAA", 0, 0, 40, 20), Line("BBBB", 30, 0, 40, 20));
        Assert.Equal("AAAA BBBB", page.ToLayoutText());
    }

    [Fact]
    public void ToLayoutText_removes_the_common_left_margin()
    {
        var page = Page(Line("Indented", 300, 0, 80, 20), Line("More", 340, 30, 40, 20));
        Assert.Equal("Indented\n    More", page.ToLayoutText());
    }

    [Fact]
    public void ToLayoutText_flattens_paragraph_line_breaks()
    {
        var page = Page(Line("foo\nbar", 0, 0, 70, 20));
        Assert.Equal("foo bar", page.ToLayoutText());
    }

    [Fact]
    public void ToLayoutText_without_geometry_emits_one_line_per_row_in_order()
    {
        var page = Page(TextOnly("first"), TextOnly("  "), TextOnly("second"));
        Assert.Equal("first\nsecond", page.ToLayoutText());
    }

    [Fact]
    public void ToLayoutText_of_an_empty_result_is_empty()
    {
        Assert.Equal(string.Empty, OcrResult.Empty.ToLayoutText());
    }

    [Fact]
    public void ToLayoutText_validates_options()
    {
        var page = Page(Line("x", 0, 0, 10, 20));
        Assert.Throws<ArgumentNullException>(() => ((OcrResult)null!).ToLayoutText());
        Assert.Throws<ArgumentOutOfRangeException>(() => page.ToLayoutText(new LayoutTextOptions { MaxBlankLines = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => page.ToLayoutText(new LayoutTextOptions { CharacterWidth = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => page.ToLayoutText(new LayoutTextOptions { CharacterWidth = double.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => page.ToLayoutText(new LayoutTextOptions { RowOverlapThreshold = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => page.ToLayoutText(new LayoutTextOptions { RowOverlapThreshold = 1.5 }));
    }
}
