using EasyOcrSharp.Structure.Engine.Geometry;
using EasyOcrSharp.Structure.Engine.Recognition;
using EasyOcrSharp.Structure.Engine.Models;
using Xunit;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;
using OcrWord = EasyOcrSharp.Models.OcrWord;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model download) for word-level boxes: splitting decoded characters into words,
/// measuring their crop-space intervals from CTC timesteps, and mapping those intervals back onto the page
/// through the 180° flip, crop padding, the 90° vertical-text rotation and the rectification quad.
/// </summary>
public class RecognitionWordBoxTests
{
    private const int Precision = 6;

    private static CtcCharacter C(string token, int first, int last, float p = 0.9f) => new(token, p, first, last);

    // An axis-aligned 200×40 crop whose top-left sits at (100, 50) on the page.
    private static readonly CropGeometry AxisAligned = new(
        new OcrPoint(100, 50), new OcrPoint(300, 50), new OcrPoint(300, 90), new OcrPoint(100, 90), 200, 40, false);

    [Fact]
    public void Splits_at_spaces_and_spans_first_to_last_timestep()
    {
        var chars = new[] { C("a", 0, 1, 0.8f), C("b", 2, 2, 0.6f), C(" ", 4, 4), C("c", 6, 6), C("d", 7, 8) };

        var words = WordBoxBuilder.SplitWords(chars, stepWidth: 10, contentWidth: 200);

        Assert.Equal(2, words.Count);
        Assert.Equal(("ab", 0d, 30d), (words[0].Text, words[0].Start, words[0].End));
        Assert.Equal(0.7, words[0].Confidence, Precision);
        Assert.Equal(("cd", 60d, 90d), (words[1].Text, words[1].Start, words[1].End));
    }

    [Fact]
    public void Each_cjk_character_is_its_own_word_centred_on_its_timestep()
    {
        // Two consecutive Han characters at steps 2 and 6: PaddleOCR's average width is
        // (6 - 2 + 1) * 10 / (2 - 1) = 50, so each box is its timestep centre ± 25.
        var chars = new[] { C("中", 2, 3), C("文", 6, 6) };

        var words = WordBoxBuilder.SplitWords(chars, stepWidth: 10, contentWidth: 100);

        Assert.Equal(new[] { "中", "文" }, words.Select(w => w.Text));
        Assert.Equal((0d, 50d), (words[0].Start, words[0].End));
        Assert.Equal((40d, 90d), (words[1].Start, words[1].End));
    }

    [Fact]
    public void Mixed_latin_and_cjk_split_at_the_script_boundary()
    {
        // No multi-character CJK run: the width falls back to contentWidth / characterCount = 90 / 3.
        var chars = new[] { C("a", 0, 0), C("b", 1, 1), C("中", 3, 3) };

        var words = WordBoxBuilder.SplitWords(chars, stepWidth: 10, contentWidth: 90);

        Assert.Equal(new[] { "ab", "中" }, words.Select(w => w.Text));
        Assert.Equal((0d, 20d), (words[0].Start, words[0].End));
        Assert.Equal((20d, 50d), (words[1].Start, words[1].End));
    }

    [Theory]
    [InlineData("中", true)]
    [InlineData("の", true)]
    [InlineData("，", true)]
    [InlineData("한", false)]
    [InlineData("a", false)]
    [InlineData("", false)]
    public void Cjk_classification(string token, bool expected) => Assert.Equal(expected, WordBoxBuilder.IsCjk(token));

    [Fact]
    public void Maps_an_upright_crop_onto_the_page()
    {
        var reading = Reading(10, C("a", 0, 1), C("b", 2, 2), C(" ", 4, 4), C("c", 6, 6), C("d", 7, 8));

        var words = WordBoxBuilder.Build(reading, AxisAligned, cropWidth: 200, cropHeight: 40, cropPadding: 0, flipped: false);

        AssertBox(new OcrBoundingBox(100, 50, 130, 90), words[0].BoundingBox);
        AssertBox(new OcrBoundingBox(160, 50, 190, 90), words[1].BoundingBox);
    }

    [Fact]
    public void A_flipped_reading_maps_right_to_left_across_the_crop()
    {
        // The crop was recognized rotated 180°: an interval [0, 30] on the rotated crop is [170, 200] upright.
        var reading = Reading(10, C("a", 0, 1), C("b", 2, 2));

        var words = WordBoxBuilder.Build(reading, AxisAligned, 200, 40, 0, flipped: true);

        AssertBox(new OcrBoundingBox(270, 50, 300, 90), words[0].BoundingBox);
    }

    [Fact]
    public void Crop_padding_is_removed_before_mapping()
    {
        // A 220×60 crop = the 200×40 rectified crop with a 10px border. Steps 1..2 cover x 10..30 on the
        // padded crop, i.e. 0..20 on the rectified one; the vertical extent excludes the border.
        var reading = Reading(10, C("a", 1, 2));

        var words = WordBoxBuilder.Build(reading, AxisAligned, 220, 60, cropPadding: 10, flipped: false);

        AssertBox(new OcrBoundingBox(100, 50, 120, 90), words[0].BoundingBox);
    }

    [Fact]
    public void A_vertical_line_rotated_for_recognition_maps_back_down_the_column()
    {
        // Upright crop 30 wide × 100 tall at (10, 20); Rectify rotated it 90° CCW into a 100×30 crop, so
        // crop x runs down the column.
        var geometry = new CropGeometry(
            new OcrPoint(10, 20), new OcrPoint(40, 20), new OcrPoint(40, 120), new OcrPoint(10, 120), 30, 100, RotatedVertical: true);
        var reading = Reading(10, C("a", 0, 4), C(" ", 5, 5), C("b", 6, 9));

        var words = WordBoxBuilder.Build(reading, geometry, cropWidth: 100, cropHeight: 30, cropPadding: 0, flipped: false);

        AssertBox(new OcrBoundingBox(10, 20, 40, 70), words[0].BoundingBox);
        AssertBox(new OcrBoundingBox(10, 80, 40, 120), words[1].BoundingBox);
    }

    [Fact]
    public void A_word_spanning_the_whole_crop_reproduces_a_slanted_quad()
    {
        var tl = new OcrPoint(0, 0);
        var tr = new OcrPoint(100, 50);
        var br = new OcrPoint(90, 70);
        var bl = new OcrPoint(-10, 20);
        var geometry = new CropGeometry(tl, tr, br, bl, 111, 22, false);
        var reading = Reading(111 / 10.0, Enumerable.Range(0, 10).Select(i => C("x", i, i)).ToArray());

        var word = Assert.Single(WordBoxBuilder.Build(reading, geometry, 111, 22, 0, flipped: false));

        Assert.Equal(new[] { tl, tr, br, bl }.Select(Round), word.BoundingPolygon.Select(Round));
    }

    [Fact]
    public void Synthetic_logits_decode_to_page_space_word_boxes()
    {
        // A 100×20 crop resized to 240×48 in a 320-wide tensor with T = 40 timesteps: one step is 8 tensor
        // pixels, i.e. 8 · (100 / 240) crop pixels. "ab" fires at steps 0..5, the space at 7, "c" at 9..11.
        string[] vocab = { CharacterDictionary.Blank, "a", "b", "c", " " };
        int[] path = new int[40];
        int[] fired = { 1, 1, 1, 2, 2, 2, 0, 4, 0, 3, 3, 3 };
        fired.CopyTo(path, 0);
        var logits = new float[path.Length * vocab.Length];
        for (int t = 0; t < path.Length; t++) logits[t * vocab.Length + path[t]] = 1f;

        var chars = new List<CtcCharacter>();
        var (text, confidence) = CtcDecoder.GreedyDecode(logits, path.Length, vocab.Length, vocab, null, chars);
        var reading = new RecognizedText(text, confidence) { Characters = chars, StepWidth = 320 / 40.0 * (100 / 240.0) };
        var geometry = new CropGeometry(
            new OcrPoint(0, 0), new OcrPoint(100, 0), new OcrPoint(100, 20), new OcrPoint(0, 20), 100, 20, false);

        var words = WordBoxBuilder.Build(reading, geometry, 100, 20, 0, flipped: false);

        Assert.Equal("ab c", text);
        Assert.Equal(new[] { "ab", "c" }, words.Select(w => w.Text));
        double step = 320 / 40.0 * (100 / 240.0);
        AssertBox(new OcrBoundingBox(0, 0, 6 * step, 20), words[0].BoundingBox);
        AssertBox(new OcrBoundingBox(9 * step, 0, 12 * step, 20), words[1].BoundingBox);
    }

    [Fact]
    public void Right_to_left_readings_reorder_each_word()
    {
        var reading = Reading(10, C("ب", 0, 0), C("ا", 1, 1)) with { RightToLeft = true };

        var word = Assert.Single(WordBoxBuilder.Build(reading, AxisAligned, 200, 40, 0, flipped: false));

        Assert.Equal(RtlTextReorder.ApplyDisplayOrder("با"), word.Text);
    }

    [Fact]
    public void Transform_moves_polygons_and_recomputes_boxes()
    {
        var words = WordBoxBuilder.Build(Reading(10, C("a", 0, 1)), AxisAligned, 200, 40, 0, false);

        var moved = WordBoxBuilder.Transform(words, p => new OcrPoint(p.X + 5, p.Y - 5));

        AssertBox(new OcrBoundingBox(105, 45, 125, 85), moved[0].BoundingBox);
        Assert.Same(Array.Empty<OcrWord>(), WordBoxBuilder.Transform(Array.Empty<OcrWord>(), p => p));
    }

    private static RecognizedText Reading(double stepWidth, params CtcCharacter[] chars)
        => new(string.Concat(chars.Select(c => c.Token)), 0.9f) { Characters = chars, StepWidth = stepWidth };

    private static (double, double) Round(OcrPoint p) => (Math.Round(p.X, 6), Math.Round(p.Y, 6));

    private static void AssertBox(OcrBoundingBox expected, OcrBoundingBox actual)
    {
        Assert.Equal(expected.MinX, actual.MinX, Precision);
        Assert.Equal(expected.MinY, actual.MinY, Precision);
        Assert.Equal(expected.MaxX, actual.MaxX, Precision);
        Assert.Equal(expected.MaxY, actual.MaxY, Precision);
    }
}
