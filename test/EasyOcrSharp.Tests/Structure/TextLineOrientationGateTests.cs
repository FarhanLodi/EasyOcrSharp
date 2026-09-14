using EasyOcrSharp.Structure.Engine;
using EasyOcrSharp.Structure.Engine.Models;
using Xunit;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// The text-line and document orientation classifiers (PP-LCNet_*_ori) misfire on upright text, and
/// acting on a wrong verdict replaces a clean reading with transliterated gibberish. Measured on the
/// bundled corpus: an upright multi-column page read at mean confidence 0.977 with the classifier off
/// but 0.783 with PaddleX 3.x's ungated argmax (5 of 17 lines destroyed), and a Devanagari page dropped
/// from 0.970 to 0.827. These tests pin the two guards that recover it: a confidence gate, and
/// confirmation of the flip by recognition confidence.
/// </summary>
public class TextLineOrientationGateTests
{
    [Fact]
    public void Orientation_defaults_are_guarded_rather_than_raw_argmax()
    {
        var options = RecognitionOptions.Default;

        // PaddleOCR 2.x's cls_thresh; PaddleX 3.x dropped it and rotates on plain argmax.
        Assert.Equal(0.9, options.TextLineOrientationThreshold);
        Assert.True(options.VerifyOrientationByRecognition);

        // Still on by default — the guards make it safe to leave on, they do not disable it.
        Assert.True(options.UseTextLineOrientation);
    }

    [Fact]
    public void Parity_with_paddlex_is_still_reachable()
    {
        var parity = RecognitionOptions.Default with
        {
            TextLineOrientationThreshold = 0,
            VerifyOrientationByRecognition = false,
        };

        Assert.Equal(0, parity.TextLineOrientationThreshold);
        Assert.False(parity.VerifyOrientationByRecognition);
    }

    [Theory]
    // A genuine 180° line: upright decodes as gibberish, the flip reads cleanly — take the flip.
    [InlineData("ussaooud", 0.34f, "processing", 0.98f, "processing")]
    // A misfire on upright text: the flip is the gibberish one — keep the upright reading.
    [InlineData("processing", 0.98f, "6uissaooud", 0.31f, "processing")]
    // Equal confidence is not evidence for flipping.
    [InlineData("same", 0.80f, "flip", 0.80f, "same")]
    public void Higher_confidence_orientation_wins(
        string uprightText, float uprightScore, string flippedText, float flippedScore, string expected)
    {
        var chosen = StructureTextEngine.ChooseOrientation((uprightText, uprightScore), (flippedText, flippedScore));

        Assert.Equal(expected, chosen.Text);
    }

    [Fact]
    public void Blank_readings_never_win_but_always_lose()
    {
        // A blank flip carries no evidence, even with a nominally higher score.
        Assert.Equal("text", StructureTextEngine.ChooseOrientation(("text", 0.4f), ("   ", 0.99f)).Text);

        // A blank upright reading loses to any real one.
        Assert.Equal("text", StructureTextEngine.ChooseOrientation(("", 0.99f), ("text", 0.2f)).Text);
    }
}
