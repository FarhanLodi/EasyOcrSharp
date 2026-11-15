using EasyOcrSharp.Structure.Engine.Recognition;
using Xunit;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model download) for the character-position output of
/// <see cref="CtcDecoder.GreedyDecode(ReadOnlySpan{float}, int, int, IReadOnlyList{string}, bool[], List{CtcCharacter})"/>
/// and for the vectorized argmax: recording positions must never change the decoded text or confidence,
/// and the unfiltered (TensorPrimitives) path must pick exactly what the scalar masked path picks.
/// </summary>
public class RecognitionCtcCharacterTests
{
    private static readonly string[] Vocab = { CharacterDictionary.Blank, "a", "b", " " };

    private static float[] Probs(int numClasses, float peak, params int[] argmaxPerStep)
    {
        var buf = new float[argmaxPerStep.Length * numClasses];
        float rest = (1f - peak) / (numClasses - 1);
        for (int t = 0; t < argmaxPerStep.Length; t++)
            for (int c = 0; c < numClasses; c++)
                buf[t * numClasses + c] = c == argmaxPerStep[t] ? peak : rest;
        return buf;
    }

    [Fact]
    public void Records_first_and_last_timestep_of_each_character_run()
    {
        // a, a, blank, b, b, b, blank
        var logits = Probs(Vocab.Length, 0.9f, 1, 1, 0, 2, 2, 2, 0);
        var chars = new List<CtcCharacter>();

        var (text, _) = CtcDecoder.GreedyDecode(logits, 7, Vocab.Length, Vocab, null, chars);

        Assert.Equal("ab", text);
        Assert.Equal(new[] { new CtcCharacter("a", 0.9f, 0, 1), new CtcCharacter("b", 0.9f, 3, 5) }, chars);
    }

    [Fact]
    public void Blank_separated_repeat_yields_two_characters_with_their_own_steps()
    {
        var logits = Probs(Vocab.Length, 0.8f, 1, 0, 1);
        var chars = new List<CtcCharacter>();

        CtcDecoder.GreedyDecode(logits, 3, Vocab.Length, Vocab, null, chars);

        Assert.Equal(new[] { (0, 0), (2, 2) }, chars.Select(c => (c.FirstStep, c.LastStep)));
    }

    [Fact]
    public void Run_of_a_class_outside_the_vocab_is_not_attributed_to_the_previous_character()
    {
        // numClasses (5) exceeds the vocab (4): class 4 wins twice but has no token and must not widen "a".
        var logits = Probs(5, 0.9f, 1, 4, 4, 1);
        var chars = new List<CtcCharacter>();

        var (text, _) = CtcDecoder.GreedyDecode(logits, 4, 5, Vocab, null, chars);

        Assert.Equal("aa", text);
        Assert.Equal(new[] { (0, 0), (3, 3) }, chars.Select(c => (c.FirstStep, c.LastStep)));
    }

    [Fact]
    public void Recording_positions_does_not_change_text_or_confidence()
    {
        var rng = new Random(1234);
        const int classes = 37, steps = 60;
        for (int trial = 0; trial < 50; trial++)
        {
            var logits = RandomLogits(rng, steps, classes, quantized: false);
            var vocab = MakeVocab(classes);

            var plain = CtcDecoder.GreedyDecode(logits, steps, classes, vocab);
            var chars = new List<CtcCharacter>();
            var detailed = CtcDecoder.GreedyDecode(logits, steps, classes, vocab, null, chars);

            Assert.Equal(plain, detailed);
            Assert.Equal(plain.Text, string.Concat(chars.Select(c => c.Token)));
        }
    }

    [Fact]
    public void Vectorized_argmax_matches_the_scalar_path_including_ties()
    {
        // An all-true mask forces the scalar strict-'>' scan; null takes TensorPrimitives.IndexOfMax.
        // Quantized values make ties at the maximum frequent, pinning the first-index rule.
        var rng = new Random(42);
        foreach (int classes in new[] { 3, 16, 97, 18385 })
        {
            const int steps = 40;
            var vocab = MakeVocab(classes);
            var allSelectable = Enumerable.Repeat(true, classes).ToArray();
            for (int trial = 0; trial < 10; trial++)
            {
                var logits = RandomLogits(rng, steps, classes, quantized: true);

                var vectorChars = new List<CtcCharacter>();
                var scalarChars = new List<CtcCharacter>();
                var vector = CtcDecoder.GreedyDecode(logits, steps, classes, vocab, null, vectorChars);
                var scalar = CtcDecoder.GreedyDecode(logits, steps, classes, vocab, allSelectable, scalarChars);

                Assert.Equal(scalar, vector);
                Assert.Equal(scalarChars, vectorChars);
            }
        }
    }

    private static float[] RandomLogits(Random rng, int steps, int classes, bool quantized)
    {
        var logits = new float[steps * classes];
        for (int i = 0; i < logits.Length; i++)
            logits[i] = quantized ? rng.Next(0, 4) / 3f : (float)rng.NextDouble();
        return logits;
    }

    private static string[] MakeVocab(int classes)
    {
        var vocab = new string[classes];
        vocab[0] = CharacterDictionary.Blank;
        for (int i = 1; i < classes; i++) vocab[i] = ((char)('!' + (i % 90))).ToString();
        return vocab;
    }
}
