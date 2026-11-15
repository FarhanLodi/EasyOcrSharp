using System.Text.RegularExpressions;
using EasyOcrSharp.Correction;
using EasyOcrSharp.Extraction;
using EasyOcrSharp.Models;
using EasyOcrSharp.Redaction;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>Hand-built OCR results for the model-free extraction-helper tests.</summary>
internal static class ExtractionTestPages
{
    /// <summary>A recognized line with an axis-aligned box at (x, y) of the given size.</summary>
    public static OcrLine Line(string text, double x, double y, double width, double height, double confidence = 0.9)
        => new()
        {
            Text = text,
            Confidence = confidence,
            BoundingBox = new OcrBoundingBox(x, y, x + width, y + height),
            BoundingPolygon = new[]
            {
                new OcrPoint(x, y), new OcrPoint(x + width, y),
                new OcrPoint(x + width, y + height), new OcrPoint(x, y + height),
            },
        };

    /// <summary>A line with no geometry at all.</summary>
    public static OcrLine TextOnly(string text, double confidence = 0.9) => new() { Text = text, Confidence = confidence };

    public static OcrResult Page(params OcrLine[] lines)
        => new()
        {
            FullText = string.Join('\n', lines.Select(l => l.Text)),
            Lines = lines,
            Languages = new[] { "en" },
        };

    /// <summary>One 20 px tall line of text 10 px per character, starting at the origin.</summary>
    public static OcrResult SingleLine(string text, double confidence = 0.9)
        => Page(Line(text, 0, 0, text.Length * 10, 20, confidence));
}

/// <summary>
/// Model-free tests for <see cref="OcrPatterns"/> and <see cref="OcrMatchExtensions.FindMatches(OcrResult, OcrPattern)"/>:
/// validators (with real IBAN and card test numbers), look-alike tolerance that never alters the matched
/// text, normalized forms, and the boxes reported for each match.
/// </summary>
public class ExtractionPatternTests
{
    private static OcrMatch Single(string text, OcrPattern pattern)
        => Assert.Single(ExtractionTestPages.SingleLine(text).FindMatches(pattern));

    private static IReadOnlyList<OcrMatch> Find(string text, OcrPattern pattern)
        => ExtractionTestPages.SingleLine(text).FindMatches(pattern);

    // ---- IBAN ----

    [Fact]
    public void Iban_grouped_is_found_with_compact_normalized_form_and_position()
    {
        var match = Single("IBAN: DE89 3704 0044 0532 0130 00", OcrPatterns.Iban);

        Assert.Equal(OcrMatchKind.Iban, match.Kind);
        Assert.Equal("DE89 3704 0044 0532 0130 00", match.Value);
        Assert.Equal("DE89370400440532013000", match.Normalized);
        Assert.Equal(6, match.Index);
        Assert.Equal(match.Value.Length, match.Length);
    }

    [Theory]
    [InlineData("GB82WEST12345698765432", "GB82WEST12345698765432")]
    [InlineData("NL91 ABNA 0417 1643 00", "NL91ABNA0417164300")]
    [InlineData("FR14 2004 1010 0505 0001 3M02 606", "FR1420041010050500013M02606")]
    public void Iban_real_test_numbers_validate(string text, string normalized)
    {
        var match = Single(text, OcrPatterns.Iban);
        Assert.Equal(text, match.Value);
        Assert.Equal(normalized, match.Normalized);
    }

    [Fact]
    public void Iban_lookalike_in_check_digits_is_repaired_for_validation_only()
    {
        // 'l' read for '1' in the check digits: the value stays as recognized, the normalized form is repaired.
        var match = Single("FRl4 2004 1010 0505 0001 3M02 606", OcrPatterns.Iban);

        Assert.Equal("FRl4 2004 1010 0505 0001 3M02 606", match.Value);
        Assert.Equal("FR1420041010050500013M02606", match.Normalized);
    }

    [Fact]
    public void Iban_O_for_zero_in_a_digit_group_is_repaired_for_validation_only()
    {
        var match = Single("DE89 37O4 0044 0532 0130 00", OcrPatterns.Iban);

        Assert.Equal("DE89 37O4 0044 0532 0130 00", match.Value);
        Assert.Equal("DE89370400440532013000", match.Normalized);
    }

    [Fact]
    public void Iban_trailing_group_that_breaks_the_checksum_is_dropped()
    {
        // "2026" is the next word, not part of the account number.
        var match = Single("BE68 5390 0754 7034 2026", OcrPatterns.Iban);

        Assert.Equal("BE68 5390 0754 7034", match.Value);
        Assert.Equal(19, match.Length);
        Assert.Equal("BE68539007547034", match.Normalized);
    }

    [Fact]
    public void Iban_with_a_wrong_check_digit_is_not_reported()
    {
        Assert.Empty(Find("DE89 3704 0044 0532 0130 01", OcrPatterns.Iban));
    }

    [Fact]
    public void Iban_validator_reuses_the_shared_mod97_check()
    {
        Assert.True(OcrPatterns.TryValidateIban("GB82 WEST 1234 5698 7654 32", out var normalized));
        Assert.Equal("GB82WEST12345698765432", normalized);
        Assert.True(FieldNormalizers.IsValidIban(normalized));
        Assert.False(OcrPatterns.TryValidateIban("GB82 WEST 1234 5698 7654 33", out _));
    }

    // ---- payment cards ----

    [Theory]
    [InlineData("4111 1111 1111 1111", "4111111111111111")]
    [InlineData("5555-5555-5555-4444", "5555555555554444")]
    [InlineData("378282246310005", "378282246310005")]
    [InlineData("4012888888881881", "4012888888881881")]
    public void PaymentCard_luhn_valid_test_numbers_are_found(string text, string digits)
    {
        var match = Single("Card " + text + " exp", OcrPatterns.PaymentCard);

        Assert.Equal(OcrMatchKind.PaymentCard, match.Kind);
        Assert.Equal(text, match.Value);
        Assert.Equal(digits, match.Normalized);
        Assert.True(RedactionPatterns.IsValidLuhn(digits));
    }

    [Fact]
    public void PaymentCard_failing_luhn_is_rejected()
    {
        Assert.Empty(Find("Card 4111 1111 1111 1112", OcrPatterns.PaymentCard));
    }

    [Fact]
    public void PaymentCard_tolerates_two_lookalikes_but_not_three()
    {
        var match = Single("4111 1111 1111 111l", OcrPatterns.PaymentCard);
        Assert.Equal("4111 1111 1111 111l", match.Value);
        Assert.Equal("4111111111111111", match.Normalized);

        Assert.Empty(Find("4OII 1111 1111 1111", OcrPatterns.PaymentCard));
    }

    [Fact]
    public void PaymentCard_rejects_all_zero_number_even_though_luhn_passes()
    {
        Assert.True(RedactionPatterns.IsValidLuhn("0000000000000000"));
        Assert.Empty(Find("0000 0000 0000 0000", OcrPatterns.PaymentCard));
        Assert.False(OcrPatterns.TryValidateCard("0000000000000000", out _));
    }

    // ---- e-mail, URL, phone, percentage ----

    [Fact]
    public void Email_and_url_are_found_with_trailing_punctuation_excluded()
    {
        const string text = "Contact Jane.Doe@Example.COM or https://example.com/path.";

        var email = Single(text, OcrPatterns.Email);
        Assert.Equal("Jane.Doe@Example.COM", email.Value);
        Assert.Equal("Jane.Doe@example.com", email.Normalized);

        var url = Single(text, OcrPatterns.Url);
        Assert.Equal("https://example.com/path", url.Value);
        Assert.Null(url.Normalized);
    }

    [Theory]
    [InlineData("Tel +44 20 7946 0958", "+44 20 7946 0958", "+442079460958")]
    [InlineData("Call (555) 123-4567 now", "(555) 123-4567", "5551234567")]
    [InlineData("Phone +49 (0) 30 12345 678", "+49 (0) 30 12345 678", "+493012345678")]
    public void Phone_numbers_are_normalized_to_digits(string text, string value, string normalized)
    {
        var match = Single(text, OcrPatterns.Phone);
        Assert.Equal(value, match.Value);
        Assert.Equal(normalized, match.Normalized);
    }

    [Theory]
    [InlineData("Qty 123 456 789")]  // digit-grouped number, not a phone
    [InlineData("Ref 1 234 567")]    // too few digits
    public void Phone_rejects_grouped_numbers(string text)
    {
        Assert.Empty(Find(text, OcrPatterns.Phone));
    }

    [Fact]
    public void Percentages_are_normalized_to_invariant_numbers()
    {
        var matches = Find("VAT 15% and -2,5 %", OcrPatterns.Percentage);

        Assert.Equal(2, matches.Count);
        Assert.Equal("15%", matches[0].Value);
        Assert.Equal("15%", matches[0].Normalized);
        Assert.Equal("-2,5 %", matches[1].Value);
        Assert.Equal("-2.5%", matches[1].Normalized);
    }

    // ---- dates ----

    [Theory]
    [InlineData("Date 2026-03-12", "2026-03-12", "2026-03-12")]
    [InlineData("on 12 Mar 2026", "12 Mar 2026", "2026-03-12")]
    [InlineData("on March 12, 2026", "March 12, 2026", "2026-03-12")]
    [InlineData("due 25/12/2026", "25/12/2026", "2026-12-25")]   // only day-first is a real date
    [InlineData("due 12/25/2026", "12/25/2026", "2026-12-25")]   // only month-first is a real date
    public void Date_unambiguous_forms_are_normalized_to_iso(string text, string value, string iso)
    {
        var match = Single(text, OcrPatterns.Date);
        Assert.Equal(OcrMatchKind.Date, match.Kind);
        Assert.Equal(value, match.Value);
        Assert.Equal(iso, match.Normalized);
    }

    [Theory]
    [InlineData("03/04/2026")]  // day/month order unknown
    [InlineData("12.03.26")]    // two-digit year
    public void Date_ambiguous_forms_match_without_a_normalized_value(string text)
    {
        var match = Single(text, OcrPatterns.Date);
        Assert.Equal(text, match.Value);
        Assert.Null(match.Normalized);
    }

    [Theory]
    [InlineData("31/02/2026")]  // no reading is a real day
    [InlineData("v1.5.26")]     // version number
    [InlineData("1.5.26")]
    public void Date_impossible_or_version_like_values_are_rejected(string text)
    {
        Assert.Empty(Find(text, OcrPatterns.Date));
    }

    [Fact]
    public void CreateDate_resolves_ambiguous_numeric_dates_with_the_given_order()
    {
        Assert.Equal("2026-04-03", Single("03/04/2026", OcrPatterns.CreateDate(DateFieldOrder.DayMonthYear)).Normalized);
        Assert.Equal("2026-03-04", Single("03/04/2026", OcrPatterns.CreateDate(DateFieldOrder.MonthDayYear)).Normalized);
        Assert.Equal("2026-03-12", Single("12.03.26", OcrPatterns.CreateDate(DateFieldOrder.DayMonthYear)).Normalized);

        // ISO dates are unaffected by the order, and an impossible reading has no normalized form.
        Assert.Equal("2026-03-12", Single("2026-03-12", OcrPatterns.CreateDate(DateFieldOrder.MonthDayYear)).Normalized);
        Assert.Null(Single("25/12/2026", OcrPatterns.CreateDate(DateFieldOrder.MonthDayYear)).Normalized);
    }

    [Fact]
    public void CreateDate_rejects_an_undefined_order()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OcrPatterns.CreateDate((DateFieldOrder)99));
    }

    // ---- amounts ----

    [Theory]
    [InlineData("Total: €1.234,56", "€1.234,56", "EUR 1234.56")]
    [InlineData("Total USD 99.90", "USD 99.90", "USD 99.90")]
    [InlineData("Net 1,234.50 EUR", "1,234.50 EUR", "EUR 1234.50")]
    [InlineData("Paid £12", "£12", "GBP 12")]
    [InlineData("Refund -$5.00", "-$5.00", "$ -5.00")]
    public void Amounts_are_normalized_with_currency_code(string text, string value, string normalized)
    {
        var match = Single(text, OcrPatterns.Amount);
        Assert.Equal(OcrMatchKind.Amount, match.Kind);
        Assert.Equal(value, match.Value);
        Assert.Equal(normalized, match.Normalized);
    }

    [Fact]
    public void Amount_without_a_currency_marker_is_not_reported()
    {
        Assert.Empty(Find("Qty 1,234.50", OcrPatterns.Amount));
    }

    [Theory]
    [InlineData("1.234,56", "1234.56")]
    [InlineData("1,234.56", "1234.56")]
    [InlineData("1'234'567", "1234567")]
    [InlineData("1 234,50", "1234.50")]
    [InlineData("0012", "12")]
    [InlineData("1.234.56", null)]   // the grouping character cannot also be the decimal mark
    [InlineData("1,234.567,89", null)]
    public void NormalizeNumber_requires_consistent_separators(string input, string? expected)
    {
        Assert.Equal(expected, OcrPatterns.NormalizeNumber(input));
    }

    // ---- multiple patterns ----

    [Fact]
    public void FindMatches_with_all_patterns_orders_by_line_then_position()
    {
        var page = ExtractionTestPages.Page(
            ExtractionTestPages.Line("Invoice 2026-03-12 total €12.50", 0, 0, 310, 20),
            ExtractionTestPages.Line("Email billing@example.com, VAT 20%", 0, 30, 340, 20));

        var matches = page.FindMatches(OcrPatterns.All);

        Assert.Equal(
            new[] { OcrMatchKind.Date, OcrMatchKind.Amount, OcrMatchKind.Email, OcrMatchKind.Percentage },
            matches.Select(m => m.Kind));
        Assert.Equal(new[] { 0, 0, 1, 1 }, matches.Select(m => m.LineIndex));
        Assert.Equal(new[] { 8, 25, 6, 31 }, matches.Select(m => m.Index));
        Assert.Same(page.Lines[1], matches[2].Line);
        Assert.Equal(page.Lines[1].Confidence, matches[2].Confidence);
    }

    [Fact]
    public void All_contains_every_builtin_pattern_once()
    {
        Assert.Equal(8, OcrPatterns.All.Count);
        Assert.Equal(8, OcrPatterns.All.Select(p => p.Kind).Distinct().Count());
    }

    [Fact]
    public void Custom_regex_reports_custom_kind_without_normalized_form()
    {
        var match = Single("Invoice INV-20931 due", new OcrPattern(new Regex(@"INV-\d+")));
        Assert.Equal(OcrMatchKind.Custom, match.Kind);
        Assert.Equal("INV-20931", match.Value);
        Assert.Null(match.Normalized);

        var viaRegex = Assert.Single(ExtractionTestPages.SingleLine("INV-1 INV-22").FindMatches(new Regex(@"INV-\d\d")));
        Assert.Equal("INV-22", viaRegex.Value);
    }

    [Fact]
    public void Custom_pattern_validator_and_normalizer_are_applied()
    {
        var pattern = new OcrPattern(
            new Regex(@"\b[A-Z]{3}\d{3}\b"),
            OcrMatchKind.Custom,
            validator: v => !v.StartsWith("XXX", StringComparison.Ordinal),
            normalizer: v => v.ToLowerInvariant());

        var match = Single("ABC123 XXX999", pattern);
        Assert.Equal("ABC123", match.Value);
        Assert.Equal("abc123", match.Normalized);
    }

    [Fact]
    public void FindMatches_validates_arguments()
    {
        var page = ExtractionTestPages.SingleLine("x");
        Assert.Throws<ArgumentNullException>(() => ((OcrResult)null!).FindMatches(OcrPatterns.Email));
        Assert.Throws<ArgumentNullException>(() => page.FindMatches((OcrPattern)null!));
        Assert.Throws<ArgumentNullException>(() => page.FindMatches((Regex)null!));
        Assert.Throws<ArgumentNullException>(() => page.FindMatches((IEnumerable<OcrPattern>)null!));
        Assert.Throws<ArgumentNullException>(() => page.FindMatches(new OcrPattern[] { null! }));
        Assert.Throws<ArgumentNullException>(() => new OcrPattern(null!));
    }

    [Fact]
    public void Empty_lines_and_results_yield_no_matches()
    {
        Assert.Empty(OcrResult.Empty.FindMatches(OcrPatterns.All));
        Assert.Empty(ExtractionTestPages.Page(ExtractionTestPages.TextOnly(string.Empty)).FindMatches(OcrPatterns.All));
    }

    // ---- boxes ----

    [Fact]
    public void Match_box_is_the_proportional_share_of_the_line_box()
    {
        var page = ExtractionTestPages.Page(ExtractionTestPages.Line("AB CD", 0, 10, 100, 20));

        var match = Assert.Single(page.FindMatches(new Regex("CD")));

        Assert.Equal(new OcrBoundingBox(60, 10, 100, 30), match.BoundingBox);
    }

    [Fact]
    public void Match_box_counts_cjk_characters_as_two_columns()
    {
        // "合计 100": 2 + 2 + 1 + 3 = 8 cells over 80 px, so "100" starts after 5 cells.
        var page = ExtractionTestPages.Page(ExtractionTestPages.Line("合计 100", 0, 0, 80, 20));

        var match = Assert.Single(page.FindMatches(new Regex(@"\d+")));

        Assert.Equal(new OcrBoundingBox(50, 0, 80, 20), match.BoundingBox);
    }

    [Fact]
    public void Match_box_is_measured_from_the_right_edge_on_rtl_lines()
    {
        // Logical order "<6 Arabic letters> 100" is displayed with the number on the left.
        var page = ExtractionTestPages.Page(ExtractionTestPages.Line("المبلغ 100", 0, 0, 100, 20));

        var match = Assert.Single(page.FindMatches(new Regex(@"\d+")));

        Assert.Equal(new OcrBoundingBox(0, 0, 30, 20), match.BoundingBox);
    }

    [Fact]
    public void Match_box_uses_character_geometry_when_the_line_has_it()
    {
        static OcrChar Ch(string value, double minX) => new()
        {
            Value = value,
            Confidence = 0.9,
            BoundingBox = new OcrBoundingBox(minX, 2, minX + 10, 18),
        };

        var line = ExtractionTestPages.Line("A1B", 0, 0, 100, 20) with
        {
            Characters = new[] { Ch("A", 0), Ch("1", 20), Ch("B", 40) },
        };

        var match = Assert.Single(ExtractionTestPages.Page(line).FindMatches(new Regex("1B")));
        Assert.Equal(new OcrBoundingBox(20, 2, 50, 18), match.BoundingBox);

        // Characters that do not reproduce the text are ignored in favour of the estimate.
        var mismatched = line with { Characters = new[] { Ch("A", 0), Ch("B", 40) } };
        var estimated = Assert.Single(ExtractionTestPages.Page(mismatched).FindMatches(new Regex("1B")));
        Assert.Equal(100.0 / 3, estimated.BoundingBox.MinX, 6);
        Assert.Equal(100.0, estimated.BoundingBox.MaxX, 6);
    }

    [Fact]
    public void Match_box_falls_back_to_the_polygon_when_the_box_is_missing()
    {
        var line = new OcrLine
        {
            Text = "AB CD",
            Confidence = 0.9,
            BoundingPolygon = new[] { new OcrPoint(0, 0), new OcrPoint(100, 0), new OcrPoint(100, 20), new OcrPoint(0, 20) },
        };

        var match = Assert.Single(ExtractionTestPages.Page(line).FindMatches(new Regex("CD")));
        Assert.Equal(new OcrBoundingBox(60, 0, 100, 20), match.BoundingBox);
    }
}
