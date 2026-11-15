using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Pdf.Internal;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Pure unit tests for the born-digital PDF path: the quality gate that decides whether a page's embedded text
/// can be trusted, the line and word grouping rebuilt from PDFium character boxes, the shaping of those lines
/// into an <see cref="OcrResult"/> under the caller's grouping options, and the auto-DPI formula. Every input is
/// a synthetic character box, so nothing here touches PDFium or a model.
/// </summary>
public class PdfEmbeddedTextLayerTests
{
    private const int PageWidth = 1700;
    private const int PageHeight = 2200;

    // ---------------------------------------------------------------- fixtures

    /// <summary>
    /// Lays <paramref name="text"/> out left to right like a real font would: glyph ink boxes 0.5 em wide on a
    /// 0.55 em advance (a 0.05 em letter gap), 0.7 em tall above the baseline. A space either becomes an explicit
    /// space character (as PDFium reports one present in the content stream) or just a 0.3 em advance.
    /// </summary>
    private static List<PdfTextChar> Line(
        string text, double x, double baseline, double size = 20, bool explicitSpaces = true,
        double tracking = 0, double? reportedFontSize = null)
    {
        var chars = new List<PdfTextChar>();
        double fontSize = reportedFontSize ?? size;
        foreach (char ch in text)
        {
            if (ch == ' ')
            {
                if (explicitSpaces)
                    chars.Add(new PdfTextChar(' ', x, baseline - size * 0.7, x + size * 0.25, baseline, fontSize));
                x += size * 0.3;
                continue;
            }

            chars.Add(new PdfTextChar(ch, x, baseline - size * 0.7, x + size * 0.5, baseline, fontSize));
            x += size * 0.55 + tracking;
        }
        return chars;
    }

    /// <summary>
    /// Lays text out right to left in logical order — each character to the left of the one before — the way
    /// PDFium reports a Hebrew or Arabic run.
    /// </summary>
    private static List<PdfTextChar> RtlLine(string text, double right, double baseline, double size = 20)
    {
        var chars = new List<PdfTextChar>();
        double x = right;
        foreach (char ch in text)
        {
            if (ch == ' ')
            {
                x -= size * 0.3;
                continue;
            }

            chars.Add(new PdfTextChar(ch, x - size * 0.5, baseline - size * 0.7, x, baseline, size));
            x -= size * 0.55;
        }
        return chars;
    }

    private static List<PdfTextChar> Concat(params List<PdfTextChar>[] parts) => parts.SelectMany(p => p).ToList();

    private static List<PdfTextChar> Chars(string text)
        => text.Select((c, i) => new PdfTextChar(c, i * 10, 0, i * 10 + 8, 14, 20)).ToList();

    private static IReadOnlyList<OcrLine> Shape(IReadOnlyList<EmbeddedLine> lines, RecognitionOptions options)
        => EmbeddedTextLayer.ToOcrLines(lines, options, new[] { "en" }, PageWidth, PageHeight);

    // ---------------------------------------------------------------- quality gate

    [Fact]
    public void Gate_accepts_ordinary_text()
    {
        Assert.True(EmbeddedTextLayer.PassesQualityGate(Chars("The quick brown fox jumps over the lazy dog.")));
    }

    [Fact]
    public void Gate_rejects_fewer_than_twenty_non_whitespace_characters()
    {
        // 19 letters plus spaces: whitespace does not count towards the minimum.
        Assert.False(EmbeddedTextLayer.PassesQualityGate(Chars("abcde fghij klmno pqrs")));
        Assert.True(EmbeddedTextLayer.PassesQualityGate(Chars("abcde fghij klmno pqrst")));
    }

    [Fact]
    public void Gate_rejects_five_percent_or_more_replacement_characters()
    {
        // 100 non-whitespace characters: 4 replacements pass (4% < 5%), 5 fail (exclusive bound).
        string four = new string('a', 96) + new string('�', 4);
        string five = new string('a', 95) + new string('�', 5);
        Assert.True(EmbeddedTextLayer.PassesQualityGate(Chars(four)));
        Assert.False(EmbeddedTextLayer.PassesQualityGate(Chars(five)));
    }

    [Fact]
    public void Gate_rejects_a_private_use_soup_from_a_broken_ToUnicode_map()
    {
        // A font with no usable /ToUnicode often extracts as its raw private-use glyph codes.
        var soup = new string(Enumerable.Range(0xE000, 60).Select(c => (char)c).ToArray());
        Assert.False(EmbeddedTextLayer.PassesQualityGate(Chars(soup)));
    }

    [Fact]
    public void Gate_rejects_text_that_is_mostly_symbols()
    {
        // Under 60% letters/digits: the signature of glyph codes mapped onto the wrong (symbol) characters.
        Assert.False(EmbeddedTextLayer.PassesQualityGate(Chars("#$%&*+<=>@^~|#$%&*+<=>@^~|abcdefghij")));
    }

    [Fact]
    public void Gate_counts_combining_marks_as_letters()
    {
        // Devanagari "किताबें" is 3 consonants and 4 vowel signs/nasalization marks (combining marks). Four
        // repetitions are 43% letters by char.IsLetterOrDigit alone — under the 60% bar — so counting marks
        // against the ratio would send perfectly good born-digital Hindi pages to OCR.
        const string hindi = "किताबें किताबें किताबें किताबें";
        Assert.Equal(28, hindi.Count(c => !char.IsWhiteSpace(c)));
        Assert.True(hindi.Count(char.IsLetterOrDigit) < 0.6 * 28);
        Assert.True(EmbeddedTextLayer.PassesQualityGate(Chars(hindi)));
    }

    [Theory]
    [InlineData('�', true)]
    [InlineData('￿', true)]
    [InlineData('\u0001', true)]
    [InlineData('', true)]
    [InlineData('A', false)]
    [InlineData(' ', false)]
    [InlineData('\n', false)]
    [InlineData('€', false)]
    public void Invalid_characters_are_replacement_control_noncharacter_or_private_use(char ch, bool invalid)
        => Assert.Equal(invalid, EmbeddedTextLayer.IsInvalid(ch));

    [Fact]
    public void The_PDFium_line_end_hyphen_marker_is_restored_to_a_hyphen()
    {
        Assert.Equal('-', EmbeddedTextLayer.NormalizeCharacter('\u0002'));
        Assert.Equal('a', EmbeddedTextLayer.NormalizeCharacter('a'));
    }

    // ---------------------------------------------------------------- mode selection and coverage

    [Fact]
    public void Ignore_never_uses_the_text_layer()
    {
        var chars = Line("The quick brown fox jumps over the lazy dog", 100, 200);
        Assert.Null(EmbeddedTextLayer.SelectLines(chars, PdfTextLayerMode.Ignore, PageWidth, PageHeight));
    }

    [Fact]
    public void A_page_that_fails_the_gate_falls_back_to_ocr_in_every_mode()
    {
        var chars = Line("short", 100, 200);
        Assert.Null(EmbeddedTextLayer.SelectLines(chars, PdfTextLayerMode.PreferEmbedded, PageWidth, PageHeight));
        Assert.Null(EmbeddedTextLayer.SelectLines(chars, PdfTextLayerMode.Auto, PageWidth, PageHeight));
    }

    [Fact]
    public void Auto_ocrs_a_page_whose_text_covers_under_one_percent_but_PreferEmbedded_reads_it()
    {
        // A scanned page with a single small digital header line: plenty of characters for the gate, but the
        // line covers ~0.2% of the page — the real content is in the scan.
        var header = Line("CONFIDENTIAL - Bates number 000123 - page 1 of 9", 100, 80, size: 12);
        Assert.NotNull(EmbeddedTextLayer.SelectLines(header, PdfTextLayerMode.PreferEmbedded, PageWidth, PageHeight));
        Assert.Null(EmbeddedTextLayer.SelectLines(header, PdfTextLayerMode.Auto, PageWidth, PageHeight));
    }

    [Fact]
    public void Auto_reads_a_page_with_real_text_coverage()
    {
        var page = new List<PdfTextChar>();
        for (int i = 0; i < 40; i++)
            page.AddRange(Line($"Line {i:00} of an ordinary born-digital report page", 100, 150 + i * 45, size: 30));

        var lines = EmbeddedTextLayer.SelectLines(page, PdfTextLayerMode.Auto, PageWidth, PageHeight);
        Assert.NotNull(lines);
        Assert.Equal(40, lines!.Count);
        Assert.True(EmbeddedTextLayer.CoverageRatio(lines, PageWidth, PageHeight) >= EmbeddedTextLayer.MinAutoCoverage);
    }

    [Fact]
    public void Characters_off_the_rendered_page_are_ignored()
    {
        // Text clipped away by the crop box: centres outside the page. OCR could not see it, so neither may we.
        var visible = Line("Visible text inside the page area here", 100, 200);
        var clipped = Line("Clipped text outside the crop box", -900, 200);
        var lines = EmbeddedTextLayer.SelectLines(Concat(visible, clipped), PdfTextLayerMode.PreferEmbedded, PageWidth, PageHeight);

        var line = Assert.Single(lines!);
        Assert.Equal("Visible text inside the page area here", line.Text);
    }

    [Fact]
    public void Coverage_is_the_line_box_area_over_the_page_area()
    {
        var word = new EmbeddedWord("x", Array.Empty<PdfTextChar>(), new OcrBoundingBox(0, 0, 100, 50));
        var line = new EmbeddedLine(new[] { word }, word.Box);
        Assert.Equal(5000.0 / (1000 * 1000), EmbeddedTextLayer.CoverageRatio(new[] { line }, 1000, 1000), 12);
        Assert.Equal(0, EmbeddedTextLayer.CoverageRatio(new[] { line }, 0, 1000));
    }

    // ---------------------------------------------------------------- line and word grouping

    [Fact]
    public void Explicit_spaces_separate_words_on_one_line()
    {
        var lines = EmbeddedTextLayer.BuildLines(Line("Hello brave new world", 100, 200));

        var line = Assert.Single(lines);
        Assert.Equal("Hello brave new world", line.Text);
        Assert.Equal(new[] { "Hello", "brave", "new", "world" }, line.Words.Select(w => w.Text));
    }

    [Fact]
    public void A_gap_wider_than_a_third_of_an_em_starts_a_word_without_an_explicit_space()
    {
        // Many generators position words absolutely and emit no space character at all.
        var lines = EmbeddedTextLayer.BuildLines(Line("Hello brave world", 100, 200, explicitSpaces: false));

        Assert.Equal(new[] { "Hello", "brave", "world" }, Assert.Single(lines).Words.Select(w => w.Text));
    }

    [Fact]
    public void Explicit_spaces_are_trusted_over_wide_ink_gaps_inside_a_word()
    {
        // Tight ink boxes leave a wide empty gap after a glyph with big side bearings ("1" before "." in
        // Helvetica is ~0.28 em). Widen it past the 0.3 em gap threshold: because the line carries explicit
        // spaces, only they may split it.
        var chars = Line("Total 1.2 million", 100, 200);
        int dot = chars.FindIndex(c => c.Value == '.');
        for (int i = dot; i < chars.Count; i++)
            chars[i] = chars[i] with { Left = chars[i].Left + 8, Right = chars[i].Right + 8 };

        var line = Assert.Single(EmbeddedTextLayer.BuildLines(chars));
        Assert.Equal(new[] { "Total", "1.2", "million" }, line.Words.Select(w => w.Text));
    }

    [Fact]
    public void Kerned_overlapping_glyphs_stay_in_one_word()
    {
        var chars = new List<PdfTextChar>
        {
            new('A', 100, 186, 112, 200, 20),
            new('V', 110, 186, 122, 200, 20), // overlaps A by 2 px, as kerned "AV" does
            new('A', 121, 186, 133, 200, 20),
        };

        var line = Assert.Single(EmbeddedTextLayer.BuildLines(chars));
        Assert.Equal("AVA", line.Text);
    }

    [Fact]
    public void Letter_spaced_text_splits_at_words_not_at_letters()
    {
        // Tracking of 0.4 em makes every letter gap wider than the 0.3 em word threshold; the line's median
        // letter gap must lift the threshold so only the real word gap splits.
        var lines = EmbeddedTextLayer.BuildLines(Line("TRACKED HEADING", 100, 200, explicitSpaces: false, tracking: 8));

        Assert.Equal(new[] { "TRACKED", "HEADING" }, Assert.Single(lines).Words.Select(w => w.Text));
    }

    [Fact]
    public void An_unscaled_font_size_does_not_split_words_into_letters()
    {
        // "/F1 1 Tf" with the text matrix scaled to 20: PDFium reports a 1 pt font. The glyph boxes are drawn at
        // the real size, and they — not the bogus font size — must set the thresholds.
        var lines = EmbeddedTextLayer.BuildLines(Line("Matrix scaled text runs", 100, 200, explicitSpaces: false, reportedFontSize: 1));

        Assert.Equal(new[] { "Matrix", "scaled", "text", "runs" }, Assert.Single(lines).Words.Select(w => w.Text));
    }

    [Fact]
    public void A_vertical_jump_starts_a_new_line_and_content_order_is_kept()
    {
        var chars = Concat(
            Line("First line of text", 100, 200),
            Line("Second line of text", 100, 240),
            Line("Third line", 100, 280));

        var lines = EmbeddedTextLayer.BuildLines(chars);
        Assert.Equal(new[] { "First line of text", "Second line of text", "Third line" }, lines.Select(l => l.Text));
    }

    [Fact]
    public void A_gap_wider_than_two_ems_splits_a_baseline_into_two_lines_like_ocr_detection_does()
    {
        // Two table cells (or columns) on one baseline, 3 em apart.
        var left = Line("Quantity", 100, 200);
        var right = Line("Price", 100 + 8 * 11 + 60, 200);
        var lines = EmbeddedTextLayer.BuildLines(Concat(left, right));

        Assert.Equal(new[] { "Quantity", "Price" }, lines.Select(l => l.Text));
    }

    [Fact]
    public void A_superscript_joins_its_line()
    {
        var chars = Line("Footnote", 100, 200);
        double x = chars[^1].Right + 1;
        chars.Add(new PdfTextChar('2', x, 182, x + 6, 193, 12)); // raised, smaller

        var line = Assert.Single(EmbeddedTextLayer.BuildLines(chars));
        Assert.Equal("Footnote2", line.Text);
    }

    [Fact]
    public void A_right_to_left_run_forms_one_line_in_logical_order()
    {
        // Hebrew in logical order, each glyph left of the previous one; no explicit space characters.
        var lines = EmbeddedTextLayer.BuildLines(RtlLine("שלום עולם", 900, 200));

        var line = Assert.Single(lines);
        Assert.Equal("שלום עולם", line.Text);
        Assert.Equal(new[] { "שלום", "עולם" }, line.Words.Select(w => w.Text));
        // The first (logical) word sits to the right of the second.
        Assert.True(line.Words[0].Box.MinX > line.Words[1].Box.MaxX);
    }

    [Fact]
    public void Word_and_line_boxes_are_the_union_of_their_glyph_boxes()
    {
        var line = Assert.Single(EmbeddedTextLayer.BuildLines(Line("ab cd", 100, 200)));

        // a: 100..110, b: 111..121, a 6 px space advance, c: 128..138, d: 139..149; glyphs 14 px tall above
        // baseline 200.
        Assert.Equal(new OcrBoundingBox(100, 186, 121, 200), line.Words[0].Box);
        Assert.Equal(new OcrBoundingBox(128, 186, 149, 200), line.Words[1].Box);
        Assert.Equal(new OcrBoundingBox(100, 186, 149, 200), line.Box);
    }

    // ---------------------------------------------------------------- shaping into an OcrResult

    [Fact]
    public void CreateResult_reports_lf_joined_text_full_confidence_and_the_page_size()
    {
        var lines = EmbeddedTextLayer.BuildLines(Concat(Line("Alpha beta", 100, 200), Line("Gamma", 100, 240)));
        var result = EmbeddedTextLayer.CreateResult(lines, null, new[] { "en" }, PageWidth, PageHeight, TimeSpan.FromMilliseconds(3));

        Assert.Equal("Alpha beta\nGamma", result.FullText);
        Assert.All(result.Lines, l => Assert.Equal(1.0, l.Confidence));
        Assert.All(result.Lines, l => Assert.Equal(4, l.BoundingPolygon.Count));
        Assert.All(result.Lines, l => Assert.Empty(l.Words)); // WordLevelDetail.None by default
        Assert.Equal(PageWidth, result.SourceWidth);
        Assert.Equal(PageHeight, result.SourceHeight);
        Assert.Equal(new[] { "en" }, result.Languages);
        Assert.Equal(TimeSpan.FromMilliseconds(3), result.Duration);
        Assert.False(result.UsedGpu);
    }

    [Fact]
    public void Word_level_detail_fills_words_with_their_own_boxes()
    {
        var lines = EmbeddedTextLayer.BuildLines(Line("ab cd", 100, 200));
        var line = Assert.Single(Shape(lines, new RecognitionOptions { WordLevelDetail = WordLevelDetail.Words }));

        Assert.Equal(new[] { "ab", "cd" }, line.Words.Select(w => w.Text));
        Assert.Equal(new OcrBoundingBox(128, 186, 149, 200), line.Words[1].BoundingBox);
        Assert.All(line.Words, w => Assert.Equal(1.0, w.Confidence));
        Assert.Empty(line.Characters);
    }

    [Fact]
    public void Character_detail_concatenates_back_to_the_line_text()
    {
        var lines = EmbeddedTextLayer.BuildLines(Line("Hello brave world", 100, 200, explicitSpaces: false));
        var line = Assert.Single(Shape(lines, new RecognitionOptions { WordLevelDetail = WordLevelDetail.Characters }));

        Assert.Equal(line.Text, string.Concat(line.Characters.Select(c => c.Value)));
        var space = line.Characters.First(c => c.Value == " ");
        Assert.True(space.BoundingBox.MinX >= line.Words[0].BoundingBox.MaxX - 0.001);
        Assert.True(space.BoundingBox.MaxX <= line.Words[1].BoundingBox.MinX + 0.001);
    }

    [Fact]
    public void Word_grouping_returns_one_result_per_word()
    {
        var lines = EmbeddedTextLayer.BuildLines(Concat(Line("one two", 100, 200), Line("three", 100, 240)));
        var words = Shape(lines, new RecognitionOptions { Grouping = TextGrouping.Word });

        Assert.Equal(new[] { "one", "two", "three" }, words.Select(w => w.Text));
    }

    [Fact]
    public void Paragraph_grouping_merges_lines_and_keeps_columns_in_content_order()
    {
        // Two columns, emitted column by column as a born-digital article is. ParagraphGrouper alone would order
        // the paragraphs top to bottom; the content order must win.
        var chars = Concat(
            Line("Left column first", 100, 225),
            Line("left column second", 100, 250),
            Line("Right column first", 900, 200),
            Line("right column second", 900, 225));
        var lines = EmbeddedTextLayer.BuildLines(chars);

        var paragraphs = Shape(lines, new RecognitionOptions { Grouping = TextGrouping.Paragraph, WordLevelDetail = WordLevelDetail.Words });

        Assert.Equal(2, paragraphs.Count);
        Assert.Equal("Left column first\nleft column second", paragraphs[0].Text);
        Assert.Equal("Right column first\nright column second", paragraphs[1].Text);
        // The grouper drops word geometry when it merges; it is stitched back from the source lines.
        Assert.Equal(new[] { "Left", "column", "first", "left", "column", "second" }, paragraphs[0].Words.Select(w => w.Text));
    }

    [Fact]
    public void A_region_keeps_only_characters_centred_inside_it()
    {
        var lines = EmbeddedTextLayer.BuildLines(Concat(Line("Header outside", 100, 100), Line("Total due 42.00", 100, 400)));
        var shaped = Shape(lines, new RecognitionOptions { Region = OcrRegion.Pixels(0, 300, PageWidth, 200) });

        var line = Assert.Single(shaped);
        Assert.Equal("Total due 42.00", line.Text);
    }

    // ---------------------------------------------------------------- auto DPI

    [Theory]
    [InlineData(612, 792, 232)]        // US Letter: 2560 / 11 in
    [InlineData(792, 612, 232)]        // landscape Letter — the longest side decides
    [InlineData(595.28, 841.89, 218)]  // A4
    [InlineData(419.53, 595.28, 309)]  // A5
    [InlineData(612, 1008, 182)]       // US Legal
    [InlineData(841.89, 1190.55, 154)] // A3
    [InlineData(792, 1224, 150)]       // Tabloid: 150.6, floored
    [InlineData(1684, 2384, 150)]      // A1: 77 DPI, clamped to the 150 floor
    [InlineData(216, 576, 320)]        // 3 x 8 in receipt
    [InlineData(252, 144, 400)]        // business card: 731 DPI, clamped to the 400 ceiling
    public void Auto_dpi_renders_the_longest_side_up_to_the_detector_canvas(double widthPt, double heightPt, int expected)
    {
        int dpi = PdfRasterizer.ResolveAutoDpi(widthPt, heightPt, canvasSize: 2560);
        Assert.Equal(expected, dpi);

        // Inside the clamps the rendered longest side never exceeds the canvas the detector would shrink it to.
        if (dpi is > PdfRasterizer.AutoDpiMin and < PdfRasterizer.AutoDpiMax)
            Assert.True(Math.Max(widthPt, heightPt) / 72.0 * dpi <= 2560);
    }

    [Fact]
    public void Auto_dpi_follows_a_raised_detector_canvas()
    {
        // Raising CanvasSize (EasyOCR's knob for small print on big pages) raises the render resolution with it.
        Assert.Equal(363, PdfRasterizer.ResolveAutoDpi(612, 792, canvasSize: 4000));
        Assert.Equal(400, PdfRasterizer.ResolveAutoDpi(612, 792, canvasSize: 8000));
    }

    [Fact]
    public void Auto_dpi_handles_degenerate_inputs()
    {
        // The detector floors its canvas at 32 px; so does the formula, landing on the 150 DPI floor.
        Assert.Equal(PdfRasterizer.AutoDpiMin, PdfRasterizer.ResolveAutoDpi(612, 792, canvasSize: 0));
        // A zero-size or NaN page cannot be measured: render at the ceiling rather than divide by zero.
        Assert.Equal(PdfRasterizer.AutoDpiMax, PdfRasterizer.ResolveAutoDpi(0, 0, 2560));
        Assert.Equal(PdfRasterizer.AutoDpiMax, PdfRasterizer.ResolveAutoDpi(double.NaN, double.NaN, 2560));
    }

    // ---------------------------------------------------------------- options and result defaults

    [Fact]
    public void Defaults_are_unchanged_and_new_options_are_opt_in()
    {
        var options = new PdfOcrOptions();
        Assert.Equal(200, options.Dpi);
        Assert.Equal(PdfTextLayerMode.Ignore, options.TextLayer);
        Assert.Equal(0, PdfOcrOptions.AutoDpi);

        var page = new PdfPageResult { PageNumber = 1, Ocr = OcrResult.Empty };
        Assert.Equal(PdfPageSource.Ocr, page.Source);
    }

    [Fact]
    public void Validate_accepts_auto_dpi_and_rejects_undefined_text_layer_modes()
    {
        new PdfOcrOptions { Dpi = PdfOcrOptions.AutoDpi }.Validate();
        new PdfOcrOptions { TextLayer = PdfTextLayerMode.Auto }.Validate();

        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfOcrOptions { Dpi = 35 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfOcrOptions { Dpi = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfOcrOptions { TextLayer = (PdfTextLayerMode)42 }.Validate());
    }
}
