using EasyOcrSharp.Structure.Engine.Recognition;
using Xunit;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model, CI-safe) for the space recovery in
/// <see cref="CtcDecoder.GreedyDecode(ReadOnlySpan{float}, int, int, IReadOnlyList{string}, bool[], List{CtcCharacter}, int, float)"/>
/// behind <see cref="Models.RecognitionOptions.SpaceRecoveryThreshold"/>: a space goes into a blank gap
/// between two characters when the space class reached the threshold there, never at the ends, next to an
/// existing space, inside a number or next to CJK punctuation, and the confidence never changes.
/// </summary>
public class CtcSpaceRecoveryTests
{
    private static readonly string[] Vocab = { CharacterDictionary.Blank, "a", "b", "1", "2", ".", ",", "，", "结", " " };
    private const int Space = 9;
    private const float Threshold = 0.15f;

    /// <summary>
    /// One timestep per entry: <c>(argmax class, space probability)</c>. The argmax gets 0.8 (or 0.99 − space
    /// probability − a small remainder when the space is high), the space class gets the given probability,
    /// the rest is spread evenly — so each row sums to 1 and the argmax is unambiguous.
    /// </summary>
    private static float[] Steps(params (int Argmax, float Space)[] steps)
    {
        int c = Vocab.Length;
        var buf = new float[steps.Length * c];
        for (int t = 0; t < steps.Length; t++)
        {
            var (argmax, space) = steps[t];
            float peak = argmax == Space ? 0.8f : Math.Min(0.8f, 0.99f - space);
            float rest = (1f - peak - (argmax == Space ? 0f : space)) / (c - (argmax == Space ? 1 : 2));
            for (int k = 0; k < c; k++)
                buf[t * c + k] = k == argmax ? peak : k == Space && argmax != Space ? space : rest;
        }
        return buf;
    }

    private static (string Text, float Confidence) Decode(float[] logits, float threshold = Threshold, bool[]? mask = null, List<CtcCharacter>? chars = null)
        => CtcDecoder.GreedyDecode(logits, logits.Length / Vocab.Length, Vocab.Length, Vocab, mask, chars, Space, threshold);

    [Fact]
    public void Inserts_a_space_where_the_blank_narrowly_beat_it()
    {
        // 结, blank (space 0.49), a  — the issue #8 shape: the space lost to the blank by a couple of percent.
        var logits = Steps((8, 0.01f), (0, 0.49f), (1, 0.01f));
        Assert.Equal("结a", CtcDecoder.GreedyDecode(logits, 3, Vocab.Length, Vocab).Text);
        Assert.Equal("结 a", Decode(logits).Text);
    }

    [Fact]
    public void Uses_the_strongest_step_of_a_multi_step_gap()
    {
        var logits = Steps((1, 0f), (0, 0.02f), (0, 0.2f), (0, 0.05f), (2, 0f));
        Assert.Equal("a b", Decode(logits).Text);
    }

    [Fact]
    public void Leaves_gaps_below_the_threshold_alone()
    {
        var logits = Steps((1, 0f), (0, 0.14f), (2, 0f));
        Assert.Equal("ab", Decode(logits).Text);
    }

    [Fact]
    public void Threshold_zero_is_the_plain_greedy_decode()
    {
        var logits = Steps((8, 0f), (0, 0.49f), (1, 0f), (0, 0.3f), (2, 0f));
        var plain = CtcDecoder.GreedyDecode(logits, 5, Vocab.Length, Vocab);
        Assert.Equal(plain, Decode(logits, threshold: 0f));
        Assert.Equal("结ab", plain.Text);
    }

    [Fact]
    public void Never_adds_a_space_at_either_end()
    {
        var logits = Steps((0, 0.45f), (1, 0f), (0, 0.45f), (2, 0f), (0, 0.45f));
        Assert.Equal("a b", Decode(logits).Text);
    }

    [Fact]
    public void Never_doubles_a_space_the_model_emitted()
    {
        // a, space (argmax), blank (space 0.5), b — the gap sits next to a real space.
        var logits = Steps((1, 0f), (Space, 0f), (0, 0.45f), (2, 0f));
        Assert.Equal("a b", Decode(logits).Text);
    }

    [Fact]
    public void Never_splits_a_number_after_its_separator()
    {
        // 1 . [gap] 2 and 1 , [gap] 2 stay whole; a gap before the separator is not special.
        Assert.Equal("1.2", Decode(Steps((3, 0f), (5, 0f), (0, 0.45f), (4, 0f))).Text);
        Assert.Equal("1,2", Decode(Steps((3, 0f), (6, 0f), (0, 0.45f), (4, 0f))).Text);
        // Not a number: a letter before the period ("a. b" is a sentence break).
        Assert.Equal("a. 2", Decode(Steps((1, 0f), (5, 0f), (0, 0.45f), (4, 0f))).Text);
    }

    [Fact]
    public void Never_splits_a_digit_run()
    {
        // Account numbers and IDs in boxed or handwritten fields show wide digit gaps.
        Assert.Equal("12", Decode(Steps((3, 0f), (0, 0.45f), (4, 0f))).Text);
        // A letter next to a digit is fine ("Pay 21190", "Mode 1").
        Assert.Equal("a 1", Decode(Steps((1, 0f), (0, 0.45f), (3, 0f))).Text);
    }

    [Fact]
    public void Never_pads_the_inside_of_brackets()
    {
        var vocab = new[] { CharacterDictionary.Blank, "a", "(", ")", " " };
        float[] Row(int argmax, float space)
        {
            var row = new float[vocab.Length];
            row[4] = space;
            row[argmax] = 0.99f - space;
            return row;
        }
        var logits = new[] { Row(1, 0f), Row(0, 0.45f), Row(2, 0f), Row(0, 0.45f), Row(1, 0f), Row(0, 0.45f), Row(3, 0f) }
            .SelectMany(r => r).ToArray();
        var (text, _) = CtcDecoder.GreedyDecode(logits, 7, vocab.Length, vocab, null, null, 4, Threshold);
        Assert.Equal("a (a)", text);
    }

    [Fact]
    public void Never_separates_a_cjk_character_from_a_bracket()
    {
        // medal_table.png: 中国（CHN） — the model reads the fullwidth bracket as "(" and its spacing as a gap.
        var vocab = new[] { CharacterDictionary.Blank, "国", "(", ")", "C", " " };
        float[] Row(int argmax, float space)
        {
            var row = new float[vocab.Length];
            row[5] = space;
            row[argmax] = 0.99f - space;
            return row;
        }
        var logits = new[] { Row(1, 0f), Row(0, 0.45f), Row(2, 0f), Row(4, 0f), Row(3, 0f), Row(0, 0.45f), Row(1, 0f) }
            .SelectMany(r => r).ToArray();
        var (text, _) = CtcDecoder.GreedyDecode(logits, 7, vocab.Length, vocab, null, null, 5, Threshold);
        Assert.Equal("国(C)国", text);
    }

    [Fact]
    public void Never_touches_cjk_punctuation()
    {
        Assert.Equal("结，a", Decode(Steps((8, 0f), (7, 0f), (0, 0.45f), (1, 0f))).Text);
        Assert.Equal("结，a", Decode(Steps((8, 0f), (0, 0.45f), (7, 0f), (1, 0f))).Text);
    }

    [Fact]
    public void Keeps_the_confidence_of_the_argmax_characters()
    {
        var logits = Steps((8, 0.01f), (0, 0.49f), (1, 0.01f));
        Assert.Equal(CtcDecoder.GreedyDecode(logits, 3, Vocab.Length, Vocab).Confidence, Decode(logits).Confidence);
    }

    [Fact]
    public void Reports_the_recovered_space_as_a_character_for_word_boxes()
    {
        var logits = Steps((1, 0f), (1, 0f), (0, 0.05f), (0, 0.3f), (2, 0f));
        var chars = new List<CtcCharacter>();
        Assert.Equal("a b", Decode(logits, chars: chars).Text);
        Assert.Equal(new[] { "a", " ", "b" }, chars.Select(c => c.Token));
        Assert.Equal((0, 1), (chars[0].FirstStep, chars[0].LastStep));
        Assert.Equal((3, 3), (chars[1].FirstStep, chars[1].LastStep));
        Assert.Equal(0.3f, chars[1].Probability, 5);
        Assert.Equal((4, 4), (chars[2].FirstStep, chars[2].LastStep));
    }

    [Fact]
    public void A_blocked_space_class_is_never_recovered()
    {
        var mask = CharacterDictionary.BuildSelectableMask(Vocab, null, new[] { " " });
        Assert.Equal("结a", Decode(Steps((8, 0f), (0, 0.49f), (1, 0f)), mask: mask).Text);
    }

    [Fact]
    public void FindSpaceClass_returns_the_last_space_or_minus_one()
    {
        Assert.Equal(Space, CharacterDictionary.FindSpaceClass(Vocab));
        Assert.Equal(-1, CharacterDictionary.FindSpaceClass(new[] { CharacterDictionary.Blank, "a" }));
    }
}
