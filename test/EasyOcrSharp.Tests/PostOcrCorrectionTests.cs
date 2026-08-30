using EasyOcrSharp.Correction;
using EasyOcrSharp.Models;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Unit tests for post-OCR correction: <see cref="SymSpellIndex"/>, <see cref="CorrectionOptions"/>,
/// <see cref="FieldNormalizers"/> and the <see cref="OcrCorrectionExtensions"/> entry points. Everything
/// here is a pure function over synthetic in-memory data — no models, no images, no network.
/// </summary>
public class PostOcrCorrectionTests
{
    // The ICAO Doc 9303 TD3 (passport) specimen. Every check digit in this line was verified by hand:
    // document number L898902C3 -> 6, birth 740812 -> 2, expiry 120415 -> 9, personal ZE184226B<<<<< -> 1,
    // and the composite over columns 0-9, 13-19, 21-42 -> 0.
    private const string Td3Lower = "L898902C36UTO7408122F1204159ZE184226B<<<<<10";

    // A TD1 (ID card) middle line: birth 740812/2, sex F, expiry 120415/9, nationality UTO, filler, 6.
    private const string Td1Middle = "7408122F1204159UTO<<<<<<<<<<<6";

    // Real, well-formed IBANs (mod-97 == 1 confirmed for each).
    private const string GermanIban = "DE89370400440532013000";
    private const string BritishIban = "GB82WEST12345698765432";

    // ---------------------------------------------------------------- fixtures

    /// <summary>A fresh lexicon array per call, so the per-instance index cache never leaks between tests.</summary>
    private static CorrectionOptions Lexicon(params string[] words) => new() { Dictionary = words };

    private static readonly IReadOnlyList<OcrPoint> Quad =
    [
        new OcrPoint(4, 8), new OcrPoint(64, 8), new OcrPoint(64, 28), new OcrPoint(4, 28),
    ];

    private static OcrLine Line(string text, double confidence = 0.10) => new()
    {
        Text = text,
        Confidence = confidence,
        BoundingPolygon = Quad,
        BoundingBox = new OcrBoundingBox(4, 8, 64, 28),
    };

    private static OcrResult Result(params OcrLine[] lines) => new()
    {
        // LF, matching the library: FullText is documented as LF-separated on every platform.
        FullText = string.Join('\n', lines.Select(l => l.Text).Where(t => t.Length > 0)),
        Lines = lines,
        Languages = ["en"],
        SourceWidth = 100,
        SourceHeight = 50,
    };

    /// <summary>One <see cref="OcrChar"/> per UTF-16 unit of <paramref name="text"/>, so the confidences line up with the text.</summary>
    private static IReadOnlyList<OcrChar> Characters(string text, params double[] confidences)
    {
        var chars = new OcrChar[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            chars[i] = new OcrChar { Value = text[i].ToString(), Confidence = confidences[i] };
        }
        return chars;
    }

    private static OcrWord Word(string text, double confidence) => new()
    {
        Text = text,
        Confidence = confidence,
        BoundingPolygon = Quad,
        BoundingBox = new OcrBoundingBox(4, 8, 64, 28),
    };

    private static string WithCharAt(string text, int index, char value)
    {
        var chars = text.ToCharArray();
        chars[index] = value;
        return new string(chars);
    }

    // ================================================================ SymSpellIndex: building

    [Fact]
    public void An_index_reports_its_term_count_and_the_edit_distance_it_was_built_for()
    {
        var index = SymSpellIndex.Build(["invoice", "total"], 2);

        Assert.Equal(2, index.TermCount);
        Assert.Equal(2, index.MaxEditDistance);
    }

    [Fact]
    public void Blank_and_null_lexicon_entries_are_ignored_and_the_rest_are_trimmed()
    {
        var index = SymSpellIndex.Build([null!, "", "   ", "invoice", "  total  "]);

        Assert.Equal(2, index.TermCount);
        Assert.True(index.Contains("total"));
        Assert.True(index.Contains("invoice"));
    }

    [Fact]
    public void Terms_that_differ_only_in_casing_collapse_onto_the_first_spelling_seen()
    {
        var index = SymSpellIndex.Build(["Invoice", "INVOICE", "invoice"]);

        Assert.Equal(1, index.TermCount);
        Assert.Equal("Invoice", Assert.Single(index.Lookup("invoce")).Term);
    }

    [Fact]
    public void Contains_is_case_insensitive_and_ignores_surrounding_whitespace()
    {
        var index = SymSpellIndex.Build(["Invoice"]);

        Assert.True(index.Contains("invoice"));
        Assert.True(index.Contains("INVOICE"));
        Assert.True(index.Contains("  Invoice  "));
        Assert.False(index.Contains("invoic"));
    }

    // The variant count is the memory driver, so pin the arithmetic: "ab" at k=1 stores ab, a, b.
    [Fact]
    public void The_variant_count_grows_with_the_indexed_edit_distance()
    {
        Assert.Equal(2, SymSpellIndex.Build(["ab", "cd"], 0).VariantCount);
        Assert.Equal(3, SymSpellIndex.Build(["ab"], 1).VariantCount);
    }

    [Fact]
    public void An_empty_lexicon_yields_an_empty_index_that_looks_up_without_throwing()
    {
        var index = SymSpellIndex.Build([]);

        Assert.Equal(0, index.TermCount);
        Assert.Equal(0, index.VariantCount);
        Assert.Empty(index.Lookup("invoice"));
        Assert.False(index.Contains("invoice"));
    }

    [Fact]
    public void The_largest_indexable_edit_distance_is_three()
    {
        Assert.Equal(3, SymSpellIndex.MaxSupportedEditDistance);
        Assert.Throws<ArgumentOutOfRangeException>(() => SymSpellIndex.Build(["a"], 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => SymSpellIndex.Build(["a"], -1));
    }

    [Fact]
    public void Building_or_querying_with_a_null_argument_throws()
    {
        var index = SymSpellIndex.Build(["invoice"]);

        Assert.Throws<ArgumentNullException>(() => SymSpellIndex.Build(null!));
        Assert.Throws<ArgumentNullException>(() => index.Lookup(null!));
        Assert.Throws<ArgumentNullException>(() => index.Contains(null!));
    }

    // ================================================================ SymSpellIndex: lookup

    [Fact]
    public void A_token_one_edit_from_a_lexicon_word_finds_it()
    {
        var suggestion = Assert.Single(SymSpellIndex.Build(["invoice"]).Lookup("invoce"));

        Assert.Equal("invoice", suggestion.Term);
        Assert.Equal(1, suggestion.Distance);
    }

    [Fact]
    public void A_token_beyond_the_indexed_radius_yields_no_candidates()
    {
        // "inwoise" is two substitutions from "invoice" (w->v, s->c).
        Assert.Empty(SymSpellIndex.Build(["invoice"], 1).Lookup("inwoise"));
        Assert.Equal("invoice", Assert.Single(SymSpellIndex.Build(["invoice"], 2).Lookup("inwoise")).Term);
    }

    [Fact]
    public void A_word_unrelated_to_anything_in_the_lexicon_yields_no_candidates()
    {
        Assert.Empty(SymSpellIndex.Build(["invoice", "total"]).Lookup("kzxqwv"));
    }

    [Fact]
    public void An_empty_or_whitespace_query_yields_no_candidates()
    {
        var index = SymSpellIndex.Build(["invoice"]);

        Assert.Empty(index.Lookup(""));
        Assert.Empty(index.Lookup("   "));
    }

    [Fact]
    public void A_query_radius_larger_than_the_index_is_clamped_to_what_was_built()
    {
        var index = SymSpellIndex.Build(["invoice"], 1);

        // Asking for 3 cannot conjure candidates the index never stored the deletions for.
        Assert.Empty(index.Lookup("inwoise", 3));
    }

    [Fact]
    public void A_query_radius_of_zero_only_matches_the_term_itself()
    {
        var index = SymSpellIndex.Build(["invoice"], 2);

        Assert.Empty(index.Lookup("invoce", 0));
        Assert.Equal(0, Assert.Single(index.Lookup("invoice", 0)).Distance);
    }

    // Ranking must prefer the classic misread over an unrelated word at the same plain edit distance,
    // even when the unrelated word comes first in the lexicon.
    [Fact]
    public void A_confusable_candidate_outranks_an_equally_distant_unrelated_word()
    {
        var suggestions = SymSpellIndex.Build(["cast", "cost"]).Lookup("c0st");

        Assert.Equal(2, suggestions.Count);
        Assert.Equal("cost", suggestions[0].Term);
        Assert.Equal(1, suggestions[0].Distance);
        Assert.Equal(0.3, suggestions[0].Score, 6);
        Assert.Equal("cast", suggestions[1].Term);
        Assert.Equal(1.0, suggestions[1].Score, 6);
    }

    // "rn" misread as "m" is two plain edits but only 0.4 of OCR-weighted cost.
    [Fact]
    public void A_merge_misread_outranks_an_unrelated_word_at_the_same_distance()
    {
        var suggestions = SymSpellIndex.Build(["nodes", "modem"], 2).Lookup("rnodem");

        Assert.Equal("modem", suggestions[0].Term);
        Assert.Equal(2, suggestions[0].Distance);
        Assert.Equal(0.4, suggestions[0].Score, 6);
    }

    // Escaped rather than pasted so the file stays pure ASCII and cannot be broken by an encoding change.
    [Fact]
    public void Lookup_handles_non_ascii_and_surrogate_pair_terms_without_throwing()
    {
        var index = SymSpellIndex.Build(["café", "ab\U0001F642"]);

        Assert.Equal("café", Assert.Single(index.Lookup("cafe")).Term);
        Assert.True(index.Contains("AB\U0001F642"));
    }

    // ================================================================ OCR-weighted distance

    [Fact]
    public void The_weighted_distance_is_zero_exactly_when_the_strings_match_ignoring_case()
    {
        Assert.Equal(0.0, SymSpellIndex.OcrWeightedDistance("invoice", "INVOICE"), 6);
        Assert.Equal(1.0, SymSpellIndex.OcrWeightedDistance("a", "b"), 6);
    }

    [Fact]
    public void The_weighted_distance_of_an_empty_string_is_the_length_of_the_other()
    {
        Assert.Equal(3.0, SymSpellIndex.OcrWeightedDistance("", "abc"), 6);
        Assert.Equal(3.0, SymSpellIndex.OcrWeightedDistance("abc", ""), 6);
        Assert.Equal(0.0, SymSpellIndex.OcrWeightedDistance("", ""), 6);
    }

    [Theory]
    [InlineData("0", "O")]
    [InlineData("1", "l")]
    [InlineData("1", "I")]
    [InlineData("5", "S")]
    [InlineData("8", "B")]
    [InlineData("2", "Z")]
    [InlineData("u", "v")]
    [InlineData("c", "e")]
    public void A_single_glyph_confusion_costs_three_tenths_instead_of_a_full_edit(string a, string b)
    {
        Assert.Equal(0.3, SymSpellIndex.OcrWeightedDistance(a, b), 6);
    }

    [Theory]
    [InlineData("rn", "m")]
    [InlineData("cl", "d")]
    [InlineData("vv", "w")]
    [InlineData("ri", "n")]
    public void A_two_for_one_merge_costs_four_tenths_in_both_directions(string pair, string merged)
    {
        Assert.Equal(0.4, SymSpellIndex.OcrWeightedDistance(pair, merged), 6);
        Assert.Equal(0.4, SymSpellIndex.OcrWeightedDistance(merged, pair), 6);
    }

    [Fact]
    public void The_weighted_distance_rejects_null_inputs()
    {
        Assert.Throws<ArgumentNullException>(() => SymSpellIndex.OcrWeightedDistance(null!, "a"));
        Assert.Throws<ArgumentNullException>(() => SymSpellIndex.OcrWeightedDistance("a", null!));
    }

    [Theory]
    [InlineData('0', 'O', true)]
    [InlineData('O', '0', true)]
    [InlineData('1', 'l', true)]
    [InlineData('I', 'l', true)]
    [InlineData('5', 's', true)]
    [InlineData('8', 'b', true)]
    [InlineData('6', 'G', true)]
    [InlineData('9', 'q', true)]
    [InlineData('a', 'b', false)]
    [InlineData('r', 'm', false)]
    [InlineData('A', 'a', false)]
    public void Known_confusion_pairs_are_reported_symmetrically_and_case_insensitively(char first, char second, bool expected)
    {
        Assert.Equal(expected, SymSpellIndex.IsConfusable(first, second));
    }

    // ================================================================ CorrectionOptions

    [Fact]
    public void The_default_options_are_conservative_and_correct_nothing()
    {
        var options = CorrectionOptions.Default;

        Assert.Equal(1, options.MaxEditDistance);
        Assert.Equal(0.75, options.MinConfidenceToCorrect);
        Assert.Equal(3, options.MinTokenLength);
        Assert.True(options.PreserveCase);
        Assert.False(options.CorrectTokensWithDigits);
        Assert.Null(options.Dictionary);
        Assert.Null(options.DictionaryIndex);
        Assert.Null(options.CustomReplacements);
        Assert.Null(options.Normalizers);
        Assert.Equal("invoce", "invoce".CorrectText(options));
    }

    [Fact]
    public void An_out_of_range_edit_distance_or_confidence_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CorrectionOptions { MaxEditDistance = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new CorrectionOptions { MaxEditDistance = 4 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new CorrectionOptions { MinConfidenceToCorrect = -0.01 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new CorrectionOptions { MinConfidenceToCorrect = 1.01 });
    }

    [Fact]
    public void The_boundary_values_of_edit_distance_and_confidence_are_accepted()
    {
        var options = new CorrectionOptions { MaxEditDistance = 0, MinConfidenceToCorrect = 0.0 };
        var wide = options with { MaxEditDistance = 3, MinConfidenceToCorrect = 1.0 };

        Assert.Equal(0, options.MaxEditDistance);
        Assert.Equal(0.0, options.MinConfidenceToCorrect);
        Assert.Equal(3, wide.MaxEditDistance);
        Assert.Equal(1.0, wide.MinConfidenceToCorrect);
    }

    [Fact]
    public void An_absent_or_empty_lexicon_disables_lexicon_correction()
    {
        Assert.Equal("invoce", "invoce".CorrectText(new CorrectionOptions()));
        Assert.Equal("invoce", "invoce".CorrectText(new CorrectionOptions { Dictionary = [] }));
    }

    [Fact]
    public void A_prebuilt_index_takes_precedence_over_the_lexicon()
    {
        var options = new CorrectionOptions
        {
            Dictionary = ["zebra"],
            DictionaryIndex = SymSpellIndex.Build(["invoice"]),
        };

        Assert.Equal("invoice", "invoce".CorrectText(options));
    }

    // The index is cached against the lexicon instance but keyed by edit distance too, so flipping the
    // distance back and forth on options derived from one lexicon must keep giving the right answer.
    [Fact]
    public void Deriving_options_with_a_different_edit_distance_reindexes_the_same_lexicon()
    {
        var lexicon = new[] { "invoice" };
        var narrow = new CorrectionOptions { Dictionary = lexicon, MaxEditDistance = 1 };
        var wide = narrow with { MaxEditDistance = 2 };

        Assert.Equal("inwoise", "inwoise".CorrectText(narrow));
        Assert.Equal("invoice", "inwoise".CorrectText(wide));
        Assert.Equal("inwoise", "inwoise".CorrectText(narrow));
    }

    // ================================================================ CorrectText: the lexicon

    [Fact]
    public void A_token_one_edit_from_the_lexicon_is_corrected_and_a_known_word_is_left_alone()
    {
        Assert.Equal("invoice total", "invoce total".CorrectText(Lexicon("invoice", "total")));
    }

    [Fact]
    public void A_token_beyond_the_configured_edit_distance_is_not_corrected()
    {
        var narrow = Lexicon("invoice");
        var wide = new CorrectionOptions { Dictionary = ["invoice"], MaxEditDistance = 2 };

        Assert.Equal("inwoise", "inwoise".CorrectText(narrow));
        Assert.Equal("invoice", "inwoise".CorrectText(wide));
    }

    [Fact]
    public void Tokens_shorter_than_the_minimum_length_are_never_rewritten_by_the_lexicon()
    {
        var options = Lexicon("the");

        Assert.Equal("th", "th".CorrectText(options));
        Assert.Equal("the", "th".CorrectText(options with { MinTokenLength = 2 }));
    }

    [Fact]
    public void Tokens_containing_a_digit_are_left_alone_unless_that_is_explicitly_enabled()
    {
        var options = Lexicon("cost");

        Assert.Equal("c0st", "c0st".CorrectText(options));
        Assert.Equal("cost", "c0st".CorrectText(options with { CorrectTokensWithDigits = true }));
    }

    // The whole point of the OCR-weighted ranking: "c0st" should land on "cost", not on "cast".
    [Fact]
    public void The_ocr_plausible_candidate_wins_over_an_equally_close_one()
    {
        var options = new CorrectionOptions { Dictionary = ["cast", "cost"], CorrectTokensWithDigits = true };

        Assert.Equal("cost", "c0st".CorrectText(options));
    }

    [Fact]
    public void A_merge_misread_is_repaired_when_the_edit_distance_allows_it()
    {
        var options = new CorrectionOptions { Dictionary = ["modem"], MaxEditDistance = 2 };

        Assert.Equal("modem", "rnodem".CorrectText(options));
    }

    [Fact]
    public void Leading_and_trailing_punctuation_is_stripped_before_lookup_and_put_back_afterwards()
    {
        Assert.Equal("(invoice),", "(invoce),".CorrectText(Lexicon("invoice")));
    }

    [Fact]
    public void A_pure_punctuation_token_is_returned_untouched()
    {
        Assert.Equal("--- ... !!", "--- ... !!".CorrectText(Lexicon("invoice")));
    }

    [Fact]
    public void Whitespace_runs_and_newlines_are_reproduced_exactly()
    {
        Assert.Equal("  invoice\ttotal\n", "  invoce\ttotal\n".CorrectText(Lexicon("invoice", "total")));
    }

    [Fact]
    public void Empty_and_whitespace_only_text_is_returned_unchanged()
    {
        var options = Lexicon("invoice");

        Assert.Equal("", "".CorrectText(options));
        Assert.Equal("   \t ", "   \t ".CorrectText(options));
    }

    [Fact]
    public void Non_ascii_lexicon_entries_are_matched_and_emitted_intact()
    {
        Assert.Equal("café", "cafe".CorrectText(Lexicon("café")));
    }

    [Fact]
    public void A_token_made_entirely_of_surrogate_pairs_is_left_alone()
    {
        // Emoji are neither letters nor digits, so the token has no "core" to look up.
        Assert.Equal("🙂🙂", "🙂🙂".CorrectText(Lexicon("invoice")));
    }

    // ================================================================ CorrectText: the confidence gate

    // The core promise: a misspelling the recognizer was sure about is NOT rewritten.
    [Fact]
    public void A_high_confidence_token_is_left_untouched_even_when_it_is_misspelled()
    {
        var options = Lexicon("invoice");

        Assert.Equal("invoce", "invoce".CorrectText(options, confidence: 0.95));
        Assert.Equal("invoice", "invoce".CorrectText(options, confidence: 0.50));
    }

    [Fact]
    public void The_confidence_gate_is_inclusive_so_a_token_exactly_at_the_threshold_is_trusted()
    {
        var options = Lexicon("invoice");

        Assert.Equal("invoce", "invoce".CorrectText(options, confidence: 0.75));
        Assert.Equal("invoice", "invoce".CorrectText(options, confidence: 0.7499));
    }

    [Fact]
    public void A_threshold_of_zero_disables_lexicon_correction_entirely()
    {
        var options = Lexicon("invoice") with { MinConfidenceToCorrect = 0.0 };

        Assert.Equal("invoce", "invoce".CorrectText(options));
    }

    [Fact]
    public void A_threshold_of_one_considers_every_token_short_of_total_certainty()
    {
        var options = Lexicon("invoice") with { MinConfidenceToCorrect = 1.0 };

        Assert.Equal("invoice", "invoce".CorrectText(options, confidence: 0.999));

        // The gate is "confidence >= threshold", so a token scored a perfect 1.0 still stops here.
        Assert.Equal("invoce", "invoce".CorrectText(options, confidence: 1.0));
    }

    // ================================================================ CorrectText: casing

    [Theory]
    [InlineData("invoce", "invoice")]
    [InlineData("Invoce", "Invoice")]
    [InlineData("INVOCE", "INVOICE")]
    public void A_replacement_adopts_the_casing_of_the_token_it_replaces(string token, string expected)
    {
        Assert.Equal(expected, token.CorrectText(Lexicon("invoice")));
    }

    [Fact]
    public void Mixed_casing_keeps_the_lexicons_own_spelling()
    {
        // Neither all-upper nor all-lower and not initial-capital: nothing to imitate, so emit the entry.
        Assert.Equal("Invoice", "iNVOCE".CorrectText(Lexicon("Invoice")));
    }

    [Fact]
    public void Turning_off_case_preservation_emits_the_lexicon_entry_verbatim()
    {
        var options = Lexicon("Invoice") with { PreserveCase = false };

        Assert.Equal("Invoice", "INVOCE".CorrectText(options));
        Assert.Equal("Invoice", "invoce".CorrectText(options));
    }

    // ================================================================ CorrectText: custom replacements

    [Fact]
    public void Custom_replacements_are_applied_regardless_of_confidence()
    {
        var options = new CorrectionOptions
        {
            CustomReplacements = new Dictionary<string, string>(StringComparer.Ordinal) { ["RX-l00"] = "RX-100" },
        };

        Assert.Equal("RX-100", "RX-l00".CorrectText(options, confidence: 1.0));
    }

    [Fact]
    public void Custom_replacements_match_the_token_core_and_keep_its_punctuation()
    {
        var options = new CorrectionOptions
        {
            CustomReplacements = new Dictionary<string, string>(StringComparer.Ordinal) { ["RX-l00"] = "RX-100" },
        };

        Assert.Equal("(RX-100),", "(RX-l00),".CorrectText(options));
    }

    [Fact]
    public void A_custom_replacement_is_emitted_verbatim_ignoring_case_preservation()
    {
        var options = new CorrectionOptions
        {
            CustomReplacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["widgit"] = "widget" },
            Dictionary = ["Widgets"],
        };

        Assert.Equal("widget", "WIDGIT".CorrectText(options));
    }

    [Fact]
    public void A_custom_replacement_pre_empts_the_lexicon()
    {
        var options = new CorrectionOptions
        {
            CustomReplacements = new Dictionary<string, string>(StringComparer.Ordinal) { ["invoce"] = "INVOICE-DOC" },
            Dictionary = ["invoice"],
        };

        Assert.Equal("INVOICE-DOC", "invoce".CorrectText(options));
    }

    // ================================================================ OcrLine.Correct

    [Fact]
    public void A_line_that_needs_no_change_comes_back_as_the_very_same_instance()
    {
        var line = Line("total");

        Assert.Same(line, line.Correct(Lexicon("total")));
    }

    [Fact]
    public void An_empty_line_is_returned_as_is()
    {
        var line = Line("");

        Assert.Same(line, line.Correct(Lexicon("total")));
    }

    [Fact]
    public void A_corrected_line_keeps_its_polygon_and_box_but_drops_stale_characters()
    {
        var line = Line("invoce") with { Characters = Characters("invoce", 0.1, 0.9, 0.9, 0.9, 0.9, 0.9) };

        var corrected = line.Correct(Lexicon("invoice"));

        Assert.Equal("invoice", corrected.Text);
        Assert.Same(Quad, corrected.BoundingPolygon);
        Assert.Equal(new OcrBoundingBox(4, 8, 64, 28), corrected.BoundingBox);
        Assert.Empty(corrected.Characters);
        Assert.Equal(0.10, corrected.Confidence);
    }

    [Fact]
    public void Word_geometry_survives_a_token_for_token_correction()
    {
        var line = Line("invoce total") with { Words = [Word("invoce", 0.1), Word("total", 0.1)] };

        var corrected = line.Correct(Lexicon("invoice", "total"));

        Assert.Equal("invoice total", corrected.Text);
        Assert.Equal(2, corrected.Words.Count);
        Assert.Equal("invoice", corrected.Words[0].Text);
        Assert.Equal("total", corrected.Words[1].Text);
        Assert.Equal(new OcrBoundingBox(4, 8, 64, 28), corrected.Words[0].BoundingBox);
        Assert.Equal(0.1, corrected.Words[0].Confidence);
    }

    [Fact]
    public void Word_geometry_is_dropped_when_the_words_do_not_line_up_with_the_text()
    {
        var line = Line("invoce total") with { Words = [Word("invoce", 0.1)] };

        var corrected = line.Correct(Lexicon("invoice", "total"));

        Assert.Equal("invoice total", corrected.Text);
        Assert.Empty(corrected.Words);
    }

    // Word-level confidence is more specific than the line's, so it decides the gate per token.
    [Fact]
    public void Word_confidence_gates_each_token_independently_of_the_line_score()
    {
        var line = Line("invoce totl", confidence: 0.10) with
        {
            Words = [Word("invoce", 0.99), Word("totl", 0.10)],
        };

        var corrected = line.Correct(Lexicon("invoice", "total"));

        Assert.Equal("invoce total", corrected.Text);
    }

    // With per-character confidences a single shaky glyph makes the whole token eligible.
    [Fact]
    public void The_minimum_character_confidence_decides_whether_a_token_is_eligible()
    {
        var confident = Line("invoce", confidence: 0.10) with
        {
            Characters = Characters("invoce", 0.90, 0.90, 0.90, 0.90, 0.90, 0.90),
        };
        var shaky = Line("invoce", confidence: 0.99) with
        {
            Characters = Characters("invoce", 0.90, 0.90, 0.90, 0.20, 0.90, 0.90),
        };

        Assert.Same(confident, confident.Correct(Lexicon("invoice")));
        Assert.Equal("invoice", shaky.Correct(Lexicon("invoice")).Text);
    }

    // The correction should land on the glyph the model doubted, not the one it was certain about:
    // "cost" and "test" are both one edit from "tost", but only "test" changes the low-confidence 'o'.
    [Fact]
    public void A_candidate_that_rewrites_a_confident_glyph_is_penalized()
    {
        var line = Line("tost", confidence: 0.99) with
        {
            Characters = Characters("tost", 0.99, 0.10, 0.99, 0.99),
        };

        Assert.Equal("test", line.Correct(new CorrectionOptions { Dictionary = ["cost", "test"] }).Text);
    }

    [Fact]
    public void Characters_that_do_not_add_up_to_the_line_text_fall_back_to_the_line_confidence()
    {
        // Five characters for a six-character line: the offsets cannot be trusted, so the line score wins.
        var line = Line("invoce", confidence: 0.10) with
        {
            Characters = Characters("invoc", 0.99, 0.99, 0.99, 0.99, 0.99),
        };

        Assert.Equal("invoice", line.Correct(Lexicon("invoice")).Text);
    }

    // ================================================================ OcrResult.Correct

    [Fact]
    public void Correcting_a_result_never_mutates_the_original()
    {
        var lines = new[] { Line("invoce"), Line("total") };
        var result = Result(lines);
        var originalLines = result.Lines;
        var originalFullText = result.FullText;

        var corrected = result.Correct(Lexicon("invoice", "total"));

        Assert.NotSame(result, corrected);
        Assert.Same(originalLines, result.Lines);
        Assert.Equal(originalFullText, result.FullText);
        Assert.Equal("invoce", result.Lines[0].Text);
        Assert.Same(lines[0], result.Lines[0]);
        Assert.Equal("invoice", corrected.Lines[0].Text);
    }

    [Fact]
    public void Lines_that_did_not_change_are_carried_over_by_reference()
    {
        var lines = new[] { Line("invoce"), Line("total") };
        var corrected = Result(lines).Correct(Lexicon("invoice", "total"));

        Assert.Same(lines[1], corrected.Lines[1]);
        Assert.NotSame(lines[0], corrected.Lines[0]);
    }

    [Fact]
    public void A_result_with_nothing_to_fix_is_handed_straight_back()
    {
        var result = Result(Line("total"), Line("invoice"));

        Assert.Same(result, result.Correct(Lexicon("invoice", "total")));
    }

    [Fact]
    public void An_empty_result_is_handed_straight_back()
    {
        Assert.Same(OcrResult.Empty, OcrResult.Empty.Correct(Lexicon("invoice")));
        Assert.Same(OcrResult.Empty, OcrResult.Empty.Correct(CorrectionOptions.Default));
    }

    [Fact]
    public void The_full_text_is_rebuilt_from_the_corrected_lines_skipping_empty_ones()
    {
        var result = Result(Line("invoce"), Line(""), Line("total"));

        var corrected = result.Correct(Lexicon("invoice", "total"));

        Assert.Equal("invoice\ntotal", corrected.FullText);
    }

    [Fact]
    public void Correcting_a_result_preserves_the_metadata_that_is_not_text()
    {
        var result = Result(Line("invoce")) with { Duration = TimeSpan.FromSeconds(2), UsedGpu = true };

        var corrected = result.Correct(Lexicon("invoice"));

        Assert.Equal(TimeSpan.FromSeconds(2), corrected.Duration);
        Assert.True(corrected.UsedGpu);
        Assert.Equal(100, corrected.SourceWidth);
        Assert.Equal(50, corrected.SourceHeight);
        Assert.Equal(["en"], corrected.Languages);
    }

    // ================================================================ argument guards

    [Fact]
    public void Every_correction_entry_point_rejects_null_arguments()
    {
        var result = Result(Line("invoce"));
        var line = Line("invoce");

        Assert.Throws<ArgumentNullException>(() => OcrCorrectionExtensions.Correct((OcrResult)null!, CorrectionOptions.Default));
        Assert.Throws<ArgumentNullException>(() => result.Correct(null!));
        Assert.Throws<ArgumentNullException>(() => OcrCorrectionExtensions.Correct((OcrLine)null!, CorrectionOptions.Default));
        Assert.Throws<ArgumentNullException>(() => line.Correct(null!));
        Assert.Throws<ArgumentNullException>(() => OcrCorrectionExtensions.CorrectText(null!, CorrectionOptions.Default));
        Assert.Throws<ArgumentNullException>(() => "text".CorrectText(null!));
    }

    // ================================================================ FieldNormalizationResult

    [Fact]
    public void The_not_applicable_result_carries_no_information()
    {
        var outcome = FieldNormalizationResult.NotApplicable;

        Assert.False(outcome.Handled);
        Assert.False(outcome.IsValid);
        Assert.False(outcome.Repaired);
        Assert.Equal("", outcome.Value);
    }

    [Fact]
    public void The_result_factories_set_the_flags_they_advertise()
    {
        var valid = FieldNormalizationResult.Valid("V");
        var repaired = FieldNormalizationResult.Fixed("F");
        var invalid = FieldNormalizationResult.Invalid("I");

        Assert.True(valid.Handled);
        Assert.True(valid.IsValid);
        Assert.False(valid.Repaired);
        Assert.Equal("V", valid.Value);

        Assert.True(repaired.Handled);
        Assert.True(repaired.IsValid);
        Assert.True(repaired.Repaired);
        Assert.Equal("F", repaired.Value);

        Assert.True(invalid.Handled);
        Assert.False(invalid.IsValid);
        Assert.False(invalid.Repaired);
        Assert.Equal("I", invalid.Value);
    }

    // ================================================================ FieldNormalizers.Date

    [Fact]
    public void A_day_first_date_is_re_emitted_in_iso_form()
    {
        var outcome = FieldNormalizers.Date()("12/03/2026");

        Assert.True(outcome.Handled);
        Assert.True(outcome.IsValid);
        Assert.True(outcome.Repaired);
        Assert.Equal("2026-03-12", outcome.Value);
    }

    [Fact]
    public void An_already_canonical_date_is_valid_but_not_reported_as_repaired()
    {
        var outcome = FieldNormalizers.Date()("2026-03-12");

        Assert.True(outcome.IsValid);
        Assert.False(outcome.Repaired);
        Assert.Equal("2026-03-12", outcome.Value);
    }

    // Letters misread for digits are coerced back before the calendar check.
    [Fact]
    public void Letters_misread_for_digits_inside_a_date_are_repaired()
    {
        var outcome = FieldNormalizers.Date()("l2/O3/2O26");

        Assert.True(outcome.IsValid);
        Assert.True(outcome.Repaired);
        Assert.Equal("2026-03-12", outcome.Value);
    }

    [Fact]
    public void An_ambiguous_date_follows_the_configured_component_order()
    {
        Assert.Equal("2026-04-03", FieldNormalizers.Date()("03/04/2026").Value);
        Assert.Equal("2026-03-04", FieldNormalizers.Date(DateFieldOrder.MonthDayYear)("03/04/2026").Value);
        Assert.Equal("2026-04-03", FieldNormalizers.Date(DateFieldOrder.DayMonthYear)("03/04/2026").Value);
    }

    [Fact]
    public void An_unambiguous_day_above_twelve_is_read_day_first_even_in_auto_mode()
    {
        Assert.Equal("2026-03-25", FieldNormalizers.Date()("25/03/2026").Value);
        Assert.Equal("2026-12-05", FieldNormalizers.Date()("05/12/2026").Value);
    }

    [Theory]
    [InlineData("01/02/68", "2068-02-01")]
    [InlineData("01/02/69", "1969-02-01")]
    public void Two_digit_years_pivot_at_sixty_eight(string input, string expected)
    {
        Assert.Equal(expected, FieldNormalizers.Date()(input).Value);
    }

    [Fact]
    public void A_date_can_be_re_emitted_in_a_custom_format()
    {
        Assert.Equal("12.03.2026", FieldNormalizers.Date(DateFieldOrder.Auto, "dd.MM.yyyy")("2026-03-12").Value);
    }

    [Fact]
    public void A_shape_that_is_not_a_date_is_not_claimed()
    {
        var date = FieldNormalizers.Date();

        Assert.False(date("invoice").Handled);
        Assert.False(date("").Handled);
        Assert.False(date("   ").Handled);
        Assert.False(date("12/03-2026").Handled);   // separators must match on both sides
        Assert.False(date("12/03/20265").Handled);  // a five-figure year is not a year
    }

    [Fact]
    public void A_date_shaped_value_that_is_not_a_real_day_is_handled_but_invalid()
    {
        var outcome = FieldNormalizers.Date()("32/13/2026");

        Assert.True(outcome.Handled);
        Assert.False(outcome.IsValid);
        Assert.Equal("32/13/2026", outcome.Value);
    }

    [Fact]
    public void A_null_output_format_is_rejected_when_the_normalizer_is_built()
    {
        Assert.Throws<ArgumentNullException>(() => FieldNormalizers.Date(DateFieldOrder.Auto, null!));
    }

    // ================================================================ FieldNormalizers.Currency

    [Fact]
    public void An_amount_with_a_leading_symbol_is_normalized_and_the_symbol_kept()
    {
        var outcome = FieldNormalizers.Currency()("$1,234.50");

        Assert.True(outcome.IsValid);
        Assert.True(outcome.Repaired);
        Assert.Equal("$1234.50", outcome.Value);
    }

    [Fact]
    public void A_trailing_iso_code_is_re_emitted_after_the_number()
    {
        Assert.Equal("1234.50 EUR", FieldNormalizers.Currency()("1234,50 EUR").Value);
    }

    [Fact]
    public void The_rightmost_of_dot_and_comma_is_taken_as_the_decimal_point()
    {
        Assert.Equal("EUR1234.56", FieldNormalizers.Currency()("EUR 1.234,56").Value);
        Assert.Equal("$1234.56", FieldNormalizers.Currency()("$1,234.56").Value);
    }

    // A lone separator with exactly three digits after it is a grouping separator, not a decimal point.
    [Theory]
    [InlineData("$1,234", "$1234.00")]
    [InlineData("$1.234", "$1234.00")]
    [InlineData("$1.5", "$1.50")]
    [InlineData("$1,5", "$1.50")]
    public void A_lone_separator_is_disambiguated_by_the_digits_that_follow_it(string input, string expected)
    {
        Assert.Equal(expected, FieldNormalizers.Currency()(input).Value);
    }

    [Fact]
    public void Letters_misread_for_digits_inside_an_amount_are_repaired()
    {
        Assert.Equal("$1500.00", FieldNormalizers.Currency()("$1,S00.00").Value);
    }

    [Fact]
    public void Parentheses_and_a_leading_minus_both_mean_a_negative_amount()
    {
        Assert.Equal("-£1000.00", FieldNormalizers.Currency()("(£1,00O.00)").Value);

        var alreadyCanonical = FieldNormalizers.Currency()("-$50.00");
        Assert.True(alreadyCanonical.IsValid);
        Assert.False(alreadyCanonical.Repaired);
        Assert.Equal("-$50.00", alreadyCanonical.Value);
    }

    [Fact]
    public void The_currency_marker_can_be_dropped_from_the_output()
    {
        Assert.Equal("1234.50", FieldNormalizers.Currency("0.00", keepSymbol: false)("$1,234.50").Value);
    }

    [Fact]
    public void A_bare_number_with_no_currency_marker_is_not_claimed()
    {
        var currency = FieldNormalizers.Currency();

        Assert.False(currency("1,234.50").Handled);
        Assert.False(currency("42").Handled);
    }

    [Fact]
    public void Junk_that_is_not_an_amount_is_not_claimed()
    {
        var currency = FieldNormalizers.Currency();

        Assert.False(currency("$abc").Handled);
        Assert.False(currency("$").Handled);
        Assert.False(currency("").Handled);
        Assert.False(currency("$OO").Handled);   // digit-like letters, but no actual digit
    }

    [Fact]
    public void A_null_number_format_is_rejected_when_the_normalizer_is_built()
    {
        Assert.Throws<ArgumentNullException>(() => FieldNormalizers.Currency(null!));
    }

    // ================================================================ FieldNormalizers.Iban

    [Theory]
    [InlineData("GB82 WEST 1234 5698 7654 32")]
    [InlineData("DE89 3704 0044 0532 0130 00")]
    [InlineData("FR14 2004 1010 0505 0001 3M02 606")]
    [InlineData("NL91ABNA0417164300")]
    [InlineData("nl91abna0417164300")]
    public void Genuinely_valid_ibans_pass_the_mod_ninety_seven_check(string iban)
    {
        Assert.True(FieldNormalizers.IsValidIban(iban));
    }

    [Theory]
    [InlineData("GB82WEST12345698765431")]  // last figure corrupted
    [InlineData("GB82WEST1234569876543")]   // a figure dropped
    [InlineData("1B82WEST12345698765432")]  // country code is not two letters
    [InlineData("")]
    [InlineData("GB82WEST")]                // too short to be an IBAN at all
    public void Corrupted_or_malformed_ibans_are_rejected(string iban)
    {
        Assert.False(FieldNormalizers.IsValidIban(iban));
    }

    [Fact]
    public void Iban_validation_rejects_a_null_input()
    {
        Assert.Throws<ArgumentNullException>(() => FieldNormalizers.IsValidIban(null!));
    }

    [Fact]
    public void A_valid_grouped_iban_is_compacted()
    {
        var outcome = FieldNormalizers.Iban()("GB82 WEST 1234 5698 7654 32");

        Assert.True(outcome.IsValid);
        Assert.True(outcome.Repaired);
        Assert.Equal(BritishIban, outcome.Value);
    }

    [Fact]
    public void An_already_compact_iban_is_valid_without_being_reported_as_repaired()
    {
        var outcome = FieldNormalizers.Iban()(BritishIban);

        Assert.True(outcome.IsValid);
        Assert.False(outcome.Repaired);
        Assert.Equal(BritishIban, outcome.Value);
    }

    [Fact]
    public void An_iban_can_be_emitted_in_groups_of_four()
    {
        Assert.Equal("GB82 WEST 1234 5698 7654 32", FieldNormalizers.Iban(grouped: true)(BritishIban).Value);
    }

    // mod-97 catches every single-character error, so a repair that then validates is provably the repair.
    [Theory]
    [InlineData("DE8937O400440532013000", GermanIban)]   // 'O' read for '0'
    [InlineData("GB82WE5T12345698765432", BritishIban)]  // '5' read for 'S'
    public void A_single_character_misread_is_repaired_from_the_checksum(string damaged, string expected)
    {
        var outcome = FieldNormalizers.Iban()(damaged);

        Assert.True(outcome.Handled);
        Assert.True(outcome.IsValid);
        Assert.True(outcome.Repaired);
        Assert.Equal(expected, outcome.Value);
        Assert.True(FieldNormalizers.IsValidIban(outcome.Value));
    }

    [Fact]
    public void An_iban_no_single_edit_can_rescue_is_handled_but_left_alone()
    {
        var outcome = FieldNormalizers.Iban()("GB82WEST12345698765431");

        Assert.True(outcome.Handled);
        Assert.False(outcome.IsValid);
        Assert.Equal("GB82WEST12345698765431", outcome.Value);
    }

    [Fact]
    public void Ordinary_words_are_never_claimed_as_a_broken_iban()
    {
        var iban = FieldNormalizers.Iban();

        Assert.False(iban("invoice").Handled);                  // far too short
        Assert.False(iban("ABCDEFGHIJKLMNOPQRST").Handled);     // no check digits where they belong
        Assert.False(iban("GB82-WEST-1234-5698-7654-32").Handled); // punctuation is not IBAN alphabet
        Assert.False(iban("").Handled);
    }

    // ================================================================ FieldNormalizers.Mrz

    [Fact]
    public void A_valid_td3_lower_line_validates_on_its_own()
    {
        var outcome = FieldNormalizers.Mrz()(Td3Lower);

        Assert.True(outcome.Handled);
        Assert.True(outcome.IsValid);
        Assert.False(outcome.Repaired);
        Assert.Equal(Td3Lower, outcome.Value);
    }

    [Fact]
    public void A_valid_two_line_td3_zone_validates()
    {
        var upper = "P<UTOERIKSSON<<ANNA<MARIA".PadRight(44, '<');
        var outcome = FieldNormalizers.Mrz()(upper + "\n" + Td3Lower);

        Assert.True(outcome.IsValid);
        Assert.Equal(upper + "\n" + Td3Lower, outcome.Value);
    }

    [Fact]
    public void A_lone_td1_middle_line_is_checked_against_the_two_date_check_digits_it_carries()
    {
        var outcome = FieldNormalizers.Mrz()(Td1Middle);

        Assert.True(outcome.IsValid);
        Assert.Equal(Td1Middle, outcome.Value);
    }

    // A letter read for a digit inside an all-numeric date field is repaired outright.
    [Fact]
    public void A_letter_misread_inside_a_date_field_is_repaired()
    {
        var damaged = WithCharAt(Td3Lower, 15, 'O');

        var outcome = FieldNormalizers.Mrz()(damaged);

        Assert.True(outcome.IsValid);
        Assert.True(outcome.Repaired);
        Assert.Equal(Td3Lower, outcome.Value);
    }

    [Fact]
    public void A_space_read_for_the_filler_character_is_folded_back()
    {
        var damaged = WithCharAt(Td3Lower, 38, ' ');

        var outcome = FieldNormalizers.Mrz()(damaged);

        Assert.True(outcome.IsValid);
        Assert.True(outcome.Repaired);
        Assert.Equal(Td3Lower, outcome.Value);
    }

    // A digit changed inside a date cannot be rescued (only letter-for-digit confusions are allowed
    // there), so the zone is reported invalid and returned untouched rather than half-corrected.
    [Fact]
    public void A_tampered_date_digit_is_reported_invalid_and_left_alone()
    {
        var damaged = WithCharAt(Td3Lower, 16, '9');

        var outcome = FieldNormalizers.Mrz()(damaged);

        Assert.True(outcome.Handled);
        Assert.False(outcome.IsValid);
        Assert.Equal(damaged, outcome.Value);
    }

    [Fact]
    public void A_composite_check_digit_that_disagrees_makes_the_whole_zone_invalid()
    {
        var damaged = WithCharAt(Td3Lower, 43, '5');

        var outcome = FieldNormalizers.Mrz()(damaged);

        Assert.True(outcome.Handled);
        Assert.False(outcome.IsValid);
        Assert.Equal(damaged, outcome.Value);
    }

    [Fact]
    public void Text_that_is_not_a_machine_readable_zone_is_not_claimed()
    {
        var mrz = FieldNormalizers.Mrz();

        Assert.False(mrz("hello world").Handled);            // wrong width
        Assert.False(mrz(new string('A', 44)).Handled);      // right width, no check digits
        Assert.False(mrz("P<UTOERIKSSON!").Handled);         // outside the MRZ alphabet
        Assert.False(mrz("").Handled);
    }

    [Theory]
    [InlineData("L898902C3", 6)]
    [InlineData("740812", 2)]
    [InlineData("120415", 9)]
    [InlineData("ZE184226B<<<<<", 1)]
    [InlineData("", 0)]
    [InlineData("AB!", -1)]
    public void The_icao_check_digit_weights_characters_seven_three_one(string field, int expected)
    {
        Assert.Equal(expected, FieldNormalizers.MrzCheckDigit(field));
    }

    [Fact]
    public void The_check_digit_helper_rejects_a_null_field()
    {
        Assert.Throws<ArgumentNullException>(() => FieldNormalizers.MrzCheckDigit(null!));
    }

    // ================================================================ normalizers inside correction

    [Fact]
    public void A_whole_line_is_offered_to_the_normalizers_before_it_is_split_into_tokens()
    {
        var options = new CorrectionOptions
        {
            Dictionary = ["invoice"],
            Normalizers = [_ => FieldNormalizationResult.Valid("CLAIMED")],
        };

        Assert.Equal("CLAIMED", "invoce".CorrectText(options));
    }

    [Fact]
    public void A_line_a_normalizer_claims_but_cannot_validate_is_left_exactly_as_recognized()
    {
        var options = new CorrectionOptions
        {
            Dictionary = ["invoice"],
            Normalizers = [_ => FieldNormalizationResult.Invalid("ignored")],
        };

        Assert.Equal("invoce", "invoce".CorrectText(options));
    }

    [Fact]
    public void A_normalizer_that_does_not_recognize_the_text_hands_it_on_to_the_lexicon()
    {
        var options = new CorrectionOptions
        {
            Dictionary = ["invoice"],
            Normalizers = [_ => FieldNormalizationResult.NotApplicable],
        };

        Assert.Equal("invoice", "invoce".CorrectText(options));
    }

    [Fact]
    public void Normalizers_are_tried_in_order_and_the_first_to_claim_the_text_wins()
    {
        var options = new CorrectionOptions
        {
            Normalizers =
            [
                _ => FieldNormalizationResult.Valid("first"),
                _ => FieldNormalizationResult.Valid("second"),
            ],
        };

        Assert.Equal("first", "anything".CorrectText(options));
    }

    // Normalizers are not gated by confidence: a field that satisfies its own grammar needs no second
    // opinion, so it is repaired even on a token the recognizer was certain about.
    [Fact]
    public void A_token_normalizer_fires_regardless_of_confidence()
    {
        var options = new CorrectionOptions { Normalizers = [FieldNormalizers.Date()] };

        Assert.Equal("Date: 2026-03-12", "Date: 12/03/2026".CorrectText(options, confidence: 1.0));
    }

    [Fact]
    public void A_token_claimed_but_rejected_by_a_normalizer_is_never_offered_to_the_lexicon()
    {
        var options = new CorrectionOptions
        {
            Dictionary = ["invoice"],
            Normalizers = [FieldNormalizers.Date()],
        };

        Assert.Equal("32/13/2026 invoice", "32/13/2026 invoce".CorrectText(options));
    }

    [Fact]
    public void An_iban_token_inside_a_line_is_repaired_while_the_rest_of_the_line_is_untouched()
    {
        var options = new CorrectionOptions { Normalizers = [FieldNormalizers.Iban()] };

        Assert.Equal("IBAN " + BritishIban, ("IBAN GB82WE5T12345698765432").CorrectText(options));
    }

    [Fact]
    public void Normalizers_run_across_a_whole_result_without_touching_the_original()
    {
        var lines = new[] { Line("12/03/2026"), Line("total") };
        var result = Result(lines);
        var options = new CorrectionOptions
        {
            Dictionary = ["total"],
            Normalizers = [FieldNormalizers.Date()],
        };

        var corrected = result.Correct(options);

        Assert.Equal("2026-03-12", corrected.Lines[0].Text);
        Assert.Same(lines[1], corrected.Lines[1]);
        Assert.Equal("12/03/2026", result.Lines[0].Text);
        Assert.Equal("2026-03-12\ntotal", corrected.FullText);
    }
}
