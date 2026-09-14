using EasyOcrSharp.Structure.Engine.Recognition;
using Xunit;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for <see cref="RtlTextReorder"/> — the
/// dependency-free equivalent of Python's <c>bidi.algorithm.get_display</c> that the engine applies to
/// the arabic recognizer pack's outputs. Pins: pure-RTL strings are character-reversed into display
/// order, embedded Latin/digit runs stay in logical order, paired brackets are mirrored so they keep
/// facing their content, and LTR-only strings pass through untouched.
/// </summary>
public class RtlTextReorderTests
{
    [Fact]
    public void Pure_arabic_string_is_reversed_into_display_order()
    {
        // "salam" (س ل ا م) in logical (emission) order; display order is the exact reverse.
        Assert.Equal("مالس", RtlTextReorder.ApplyDisplayOrder("سلام"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("Price: 42 (USD)")]
    public void Strings_without_rtl_characters_pass_through_unchanged(string input)
        => Assert.Equal(input, RtlTextReorder.ApplyDisplayOrder(input));

    [Fact]
    public void Ltr_majority_string_reverses_only_the_arabic_run_in_place()
    {
        // 3 strong-LTR vs 2 strong-RTL characters -> LTR paragraph: run order is kept, the Arabic
        // run is character-reversed where it stands.
        Assert.Equal("ABC حم", RtlTextReorder.ApplyDisplayOrder("ABC مح"));
    }

    [Fact]
    public void Digit_sequence_inside_arabic_text_stays_in_logical_order()
    {
        // "ab 123 jd" in Arabic letters: RTL paragraph -> the runs are laid out right-to-left
        // (so the trailing Arabic run comes first on screen) but "123" keeps its digit order,
        // matching the UBA's left-to-right rendering of numbers inside RTL text.
        string display = RtlTextReorder.ApplyDisplayOrder("اب 123 جد");

        Assert.Equal("دج 123 با", display);
        Assert.Contains("123", display);
    }

    [Fact]
    public void Latin_word_inside_arabic_text_keeps_its_letter_order()
    {
        string display = RtlTextReorder.ApplyDisplayOrder("اب OCR جد");

        Assert.Contains("OCR", display);
        Assert.DoesNotContain("RCO", display);
    }

    [Fact]
    public void Paired_brackets_are_mirrored_to_keep_facing_their_content()
    {
        // "abj (d)" in Arabic letters — everything resolves RTL, so the whole line reverses and
        // each bracket is swapped for its mirror so it still encloses the Arabic character.
        Assert.Equal(
            "(د) جبا",
            RtlTextReorder.ApplyDisplayOrder("ابج (د)"));
    }
}
