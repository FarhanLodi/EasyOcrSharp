using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Pure-function tests for <see cref="BidiDisplay"/>, the port of the <c>bidi.algorithm.get_display</c> call Python
/// EasyOCR applies to its arabic pack. Every expected value in <see cref="Golden"/> was produced by running
/// python-bidi 0.6.10's <c>get_display</c> (the version EasyOCR 1.6.2 pulls in) over the same input, so these pin
/// the port to the reference rather than to our reading of UAX #9.
/// </summary>
public class BidiDisplayTests
{
    /// <summary>Input in recognizer (painted, left-to-right) order → python-bidi <c>get_display</c> output.</summary>
    public static TheoryData<string, string> Golden => new()
    {
        { "مالس", "سلام" },
        { "123 مالس", "سلام 123" },
        { "مالس 123", "123 سلام" },
        { "OCR ابحرم", "OCR مرحبا" },
        { "ابحرم OCR", "OCR مرحبا" },
        { "دج OCR با", "اب OCR جد" },
        { "دج 123 با", "اب 123 جد" },
        { "(د) جبا", "ابج (د)" },
        { "ابج (د)", "(د) جبا" },
        { "١٢٣ مالس", "سلام ١٢٣" },
        { "۱۲۳ مالس", "سلام ۱۲۳" },
        { "12.50 رالود", "دولار 12.50" },
        { "مالس.", ".سلام" },
        { ".مالس", "سلام." },
        { "hello world", "hello world" },
        { "ABC مح", "ABC حم" },
        { "Price: 42 (USD)", "Price: 42 (USD)" },
        { "مح ABC DEF", "ABC DEF حم" },
        { "aمb", "aمb" },
        { "2024-05-01 خيرات", "تاريخ 2024-05-01" },
        { "םולש", "שלום" },
        { "مالس test.user@example.com", "test.user@example.com سلام" },
        { "مالس ,ابحرم", "مرحبا, سلام" },
        { "", "" },
        { " مالس ", " سلام " },
        { "[مالس]", "[سلام]" },
        { "مالس -5", "5- سلام" },
        { "%50 مالس", "سلام %50" },
        { "مالس 12 34", "34 12 سلام" },
        { "AB 12 مالس", "AB 12 سلام" },
        { "مالس ab 12", "ab 12 سلام" },
        { "مالس 12 ab", "ab 12 سلام" },
        { "مالس 1,234.5", "1,234.5 سلام" },
        { "مالس +1", "1+ سلام" },
        { "مالس $5", "5$ سلام" },
        { "مالس ١٢ با", "اب ١٢ سلام" },
    };

    [Theory]
    [MemberData(nameof(Golden))]
    public void Matches_python_bidi_get_display(string input, string expected)
        => Assert.Equal(expected, BidiDisplay.GetDisplay(input));

    [Fact]
    public void Line_without_rtl_text_is_returned_as_the_same_instance()
    {
        var line = new OcrLine { Text = "Invoice 42" };
        Assert.Same(line, BidiDisplay.Apply(line));
    }

    [Fact]
    public void Characters_and_words_follow_the_reordered_text()
    {
        // Recognizer output for a painted "سعر 123": digits first (left), then the Arabic word reversed.
        const string painted = "123 رعس";
        var characters = painted.Select((c, i) => new OcrChar
        {
            Value = c.ToString(),
            BoundingBox = new OcrBoundingBox(i * 10, 0, i * 10 + 10, 10),
        }).ToArray();
        var digits = new OcrWord { Text = "123", BoundingBox = new OcrBoundingBox(0, 0, 30, 10) };
        var word = new OcrWord { Text = "رعس", BoundingBox = new OcrBoundingBox(40, 0, 70, 10) };
        var line = new OcrLine { Text = painted, Characters = characters, Words = [digits, word] };

        var reordered = BidiDisplay.Apply(line);

        Assert.Equal("سعر 123", reordered.Text);
        Assert.Equal(reordered.Text, string.Concat(reordered.Characters.Select(c => c.Value)));
        Assert.Equal(["سعر", "123"], reordered.Words.Select(w => w.Text));
        // Geometry travels with its glyph: the Arabic word keeps its right-hand box, the first reading-order
        // character (س) keeps the right-most pixels.
        Assert.Equal(40, reordered.Words[0].BoundingBox.MinX);
        Assert.Equal(60, reordered.Characters[0].BoundingBox.MinX);
    }
}
