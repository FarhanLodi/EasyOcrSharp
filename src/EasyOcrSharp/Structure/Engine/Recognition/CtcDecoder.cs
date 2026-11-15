using System.Numerics.Tensors;
using System.Text;

namespace EasyOcrSharp.Structure.Engine.Recognition;

/// <summary>
/// CTC greedy decoder for the recognizer's per-timestep logits. Takes the argmax at each timestep,
/// collapses runs of repeated indices, drops the blank (index 0 in the Paddle vocab convention), and
/// maps the surviving indices through the vocabulary to characters. The confidence is the mean of the
/// per-timestep max probabilities over the non-blank, non-repeat timesteps that contributed a character.
/// <para>
/// CRITICAL — Paddle CTC convention: the blank label is index <b>0</b>. PaddleOCR prepends a dedicated
/// blank class before the dictionary, so decoding must <em>skip class 0</em> and additionally collapse
/// consecutive equal indices (the standard CTC best-path rule). Note this is the same numeric index used
/// for the "none" class in EasyOCR/RapidOCR-style vocabularies, but here it is genuinely the CTC blank
/// rather than a placeholder.
/// </para>
/// <para>
/// Without a character filter the per-timestep argmax is the vectorized
/// <see cref="TensorPrimitives.IndexOfMax(ReadOnlySpan{float})"/>, which — like the scalar strict
/// <c>&gt;</c> scan used when a filter is active — returns the first index of the maximum.
/// </para>
/// </summary>
internal static class CtcDecoder
{
    /// <summary>
    /// Greedily decodes a single sequence of logits.
    /// </summary>
    /// <param name="logits">
    /// Row-major logits/probabilities of shape <c>[timeSteps, numClasses]</c> (timestep <c>t</c>, class
    /// <c>c</c> at index <c>t * numClasses + c</c>). PaddleOCR's recognition head already applies softmax,
    /// so these are typically probabilities in [0,1]; the confidence is computed directly from the
    /// per-timestep max value (no extra normalization is applied).
    /// </param>
    /// <param name="timeSteps">Number of timesteps <c>T</c> in <paramref name="logits"/>.</param>
    /// <param name="numClasses">Number of classes <c>C</c> per timestep (must equal <c>vocab.Count</c>).</param>
    /// <param name="vocab">
    /// The ordered CTC label set in the Paddle convention: index 0 is the blank token, the remaining
    /// indices map to characters (see <see cref="CharacterDictionary"/>).
    /// </param>
    /// <param name="selectable">
    /// Optional per-class allow mask (parallel to <paramref name="vocab"/>) implementing a recognition
    /// <c>Allowlist</c>/<c>Blocklist</c>. When supplied, a class whose entry is <c>false</c> is treated as if
    /// its logit were −∞ at every timestep, so it can never win the argmax and is never emitted; the blank
    /// must remain selectable (index 0) or decoding breaks. Build it once per call with
    /// <see cref="CharacterDictionary.BuildSelectableMask"/>. <c>null</c> (the default) disables masking and
    /// reproduces the unfiltered result byte-for-byte. Confidence is still the mean of the chosen (masked)
    /// classes' probabilities, consistent with the unmasked path.
    /// </param>
    /// <returns>The decoded <c>Text</c> and its mean per-character <c>Confidence</c> (0–1).</returns>
    public static (string Text, float Confidence) GreedyDecode(
        ReadOnlySpan<float> logits, int timeSteps, int numClasses, IReadOnlyList<string> vocab,
        bool[]? selectable = null)
        => GreedyDecode(logits, timeSteps, numClasses, vocab, selectable, characters: null);

    /// <summary>
    /// Greedily decodes a single sequence of logits and, when <paramref name="characters"/> is supplied,
    /// records every emitted character together with the first and last timestep of its argmax run.
    /// The decoded text and confidence are identical to the overload without <paramref name="characters"/>.
    /// </summary>
    /// <param name="logits">Row-major <c>[timeSteps, numClasses]</c> probabilities.</param>
    /// <param name="timeSteps">Number of timesteps <c>T</c> in <paramref name="logits"/>.</param>
    /// <param name="numClasses">Number of classes <c>C</c> per timestep.</param>
    /// <param name="vocab">The ordered CTC label set (index 0 is the blank).</param>
    /// <param name="selectable">Optional per-class allow mask; <c>null</c> disables filtering.</param>
    /// <param name="characters">Receives one <see cref="CtcCharacter"/> per emitted character, in emission order; <c>null</c> to skip.</param>
    /// <returns>The decoded <c>Text</c> and its mean per-character <c>Confidence</c> (0–1).</returns>
    public static (string Text, float Confidence) GreedyDecode(
        ReadOnlySpan<float> logits, int timeSteps, int numClasses, IReadOnlyList<string> vocab,
        bool[]? selectable, List<CtcCharacter>? characters)
        => GreedyDecode(logits, timeSteps, numClasses, vocab, selectable, characters, spaceClass: -1, spaceRecoveryThreshold: 0f);

    /// <summary>
    /// Greedily decodes a single sequence of logits, then — when <paramref name="spaceRecoveryThreshold"/> is
    /// positive — inserts a space between two emitted characters whose separating blank timesteps gave the
    /// space class at least that probability (see <see cref="Models.RecognitionOptions.SpaceRecoveryThreshold"/>).
    /// <para>
    /// Between two consecutive emitted characters every timestep's argmax is the blank, so a recovered space
    /// only ever replaces a blank that narrowly beat the space: it is never inserted at either end of the
    /// line, next to a space the model already emitted, inside a number (between two digits, or in
    /// <c>3838.17</c>, <c>1,280</c>, <c>12:30</c>), inside or before a bracket where typography forbids one,
    /// before trailing punctuation, around <c>@</c>, after an apostrophe or next to CJK/fullwidth
    /// punctuation (see <see cref="MayInsertSpace"/>). The confidence is still the mean over the
    /// argmax-emitted characters, so recovery changes the text and nothing else.
    /// </para>
    /// </summary>
    /// <param name="logits">Row-major <c>[timeSteps, numClasses]</c> probabilities.</param>
    /// <param name="timeSteps">Number of timesteps <c>T</c> in <paramref name="logits"/>.</param>
    /// <param name="numClasses">Number of classes <c>C</c> per timestep.</param>
    /// <param name="vocab">The ordered CTC label set (index 0 is the blank).</param>
    /// <param name="selectable">Optional per-class allow mask; <c>null</c> disables filtering. A masked-out space class disables recovery.</param>
    /// <param name="characters">Receives one <see cref="CtcCharacter"/> per emitted character (recovered spaces included), in emission order; <c>null</c> to skip.</param>
    /// <param name="spaceClass">The vocab index of the space class (see <see cref="CharacterDictionary.FindSpaceClass"/>); negative disables recovery.</param>
    /// <param name="spaceRecoveryThreshold">Minimum space probability in a gap to insert a space; ≤ 0 disables recovery.</param>
    /// <returns>The decoded <c>Text</c> and its mean per-character <c>Confidence</c> (0–1).</returns>
    public static (string Text, float Confidence) GreedyDecode(
        ReadOnlySpan<float> logits, int timeSteps, int numClasses, IReadOnlyList<string> vocab,
        bool[]? selectable, List<CtcCharacter>? characters, int spaceClass, float spaceRecoveryThreshold)
    {
        ArgumentNullException.ThrowIfNull(vocab);

        // A mask whose length disagrees with the class count can't be safely indexed in the hot loop; ignore
        // it (decode unfiltered) rather than risk an out-of-range read mid-batch.
        if (selectable is not null && selectable.Length != numClasses)
            selectable = null;
        if (timeSteps <= 0 || numClasses <= 0)
            return (string.Empty, 0f);

        bool recoverSpaces = spaceRecoveryThreshold > 0f
            && spaceClass > 0 && spaceClass < numClasses && spaceClass < vocab.Count
            && (selectable is null || selectable[spaceClass]);
        // Recovery works on the emitted characters' timestep runs, so it needs them even when the caller doesn't.
        List<CtcCharacter>? emitted = characters ?? (recoverSpaces ? new List<CtcCharacter>() : null);
        int firstEmitted = emitted?.Count ?? 0;

        var sb = new StringBuilder(timeSteps);
        double confidenceSum = 0d;
        int keptCount = 0;
        // The argmax index of the previous timestep, used to collapse consecutive repeats (CTC best path).
        // -1 is a sentinel that can never equal a real class index, so the first timestep is never treated
        // as a repeat.
        int previousIndex = -1;
        // Whether the previous timestep's run emitted a character (so a repeat extends that character).
        bool previousEmitted = false;

        for (int t = 0; t < timeSteps; t++)
        {
            ReadOnlySpan<float> row = logits.Slice(t * numClasses, numClasses);

            // Per-timestep argmax over the C classes, tracking the max probability for the confidence.
            int bestIndex;
            float bestProb;
            if (selectable is null)
            {
                bestIndex = TensorPrimitives.IndexOfMax(row);
                bestProb = row[bestIndex];
            }
            else
            {
                bestIndex = ArgMaxSelectable(row, selectable, out bestProb);
            }

            if (bestIndex == previousIndex)
            {
                // CTC best-path collapse: a repeat of the previous class emits nothing, but it widens the
                // character that run produced (when it produced one).
                if (previousEmitted && emitted is not null)
                    emitted[^1] = emitted[^1] with { LastStep = t };
            }
            else
            {
                previousEmitted = false;
                // Skip the blank (index 0). Only the *kept* timesteps (those that emit a character)
                // contribute to the confidence — this matches PaddleOCR's CTCLabelDecode, which averages the
                // selected probs. Guard against a vocab/model class-count mismatch rather than throwing.
                if (bestIndex != 0 && bestIndex < vocab.Count)
                {
                    string token = vocab[bestIndex];
                    sb.Append(token);
                    confidenceSum += bestProb;
                    keptCount++;
                    emitted?.Add(new CtcCharacter(token, bestProb, t, t));
                    previousEmitted = true;
                }
            }

            previousIndex = bestIndex;
        }

        // An empty decode (all-blank sequence) has no characters to average; report confidence 0.
        float confidence = keptCount > 0 ? (float)(confidenceSum / keptCount) : 0f;

        if (recoverSpaces && RecoverSpaces(logits, numClasses, emitted!, firstEmitted, spaceClass, vocab[spaceClass], spaceRecoveryThreshold))
        {
            sb.Clear();
            for (int i = firstEmitted; i < emitted!.Count; i++) sb.Append(emitted[i].Token);
        }
        return (sb.ToString(), confidence);
    }

    /// <summary>
    /// Inserts a recovered space into <paramref name="emitted"/> (from <paramref name="first"/> on) wherever the
    /// blank timesteps between two emitted characters reached <paramref name="threshold"/> on the space class,
    /// subject to the guards listed on the public overload.
    /// </summary>
    /// <returns>True when at least one space was inserted.</returns>
    private static bool RecoverSpaces(
        ReadOnlySpan<float> logits, int numClasses, List<CtcCharacter> emitted, int first,
        int spaceClass, string spaceToken, float threshold)
    {
        List<CtcCharacter>? result = null;
        for (int i = first; i < emitted.Count; i++)
        {
            if (i > first)
            {
                CtcCharacter left = emitted[i - 1], right = emitted[i];
                int gapStart = left.LastStep + 1, gapEnd = right.FirstStep; // gap is [gapStart, gapEnd)
                if (gapEnd > gapStart && MayInsertSpace(emitted, first, i))
                {
                    int bestStep = gapStart;
                    float best = logits[gapStart * numClasses + spaceClass];
                    for (int t = gapStart + 1; t < gapEnd; t++)
                    {
                        float p = logits[t * numClasses + spaceClass];
                        if (p > best)
                        {
                            best = p;
                            bestStep = t;
                        }
                    }
                    if (best >= threshold)
                    {
                        if (result is null)
                        {
                            result = new List<CtcCharacter>(emitted.Count - first + 4);
                            for (int k = first; k < i; k++) result.Add(emitted[k]);
                        }
                        result.Add(new CtcCharacter(spaceToken, best, bestStep, bestStep));
                    }
                }
            }
            result?.Add(emitted[i]);
        }

        if (result is null) return false;
        emitted.RemoveRange(first, emitted.Count - first);
        emitted.AddRange(result);
        return true;
    }

    /// <summary>
    /// Whether a recovered space may go between <c>emitted[right − 1]</c> and <c>emitted[right]</c>. It may not
    /// when either side is already whitespace; inside a number — between two digits (account numbers,
    /// amounts and IDs in boxed or handwritten fields show wide digit gaps) or after a separator that follows
    /// a digit (<c>3838.17</c>, <c>1,280</c>, <c>12:30</c> — a narrow separator leaves a space-like gap); just
    /// inside a bracket, or between a CJK character and a bracket; before trailing punctuation
    /// (<c>, . : ; ! ? %</c>), around <c>@</c> or after an apostrophe; or next to CJK/fullwidth punctuation,
    /// which carries its own spacing.
    /// </summary>
    private static bool MayInsertSpace(List<CtcCharacter> emitted, int first, int right)
    {
        string before = emitted[right - 1].Token, after = emitted[right].Token;
        if (string.IsNullOrWhiteSpace(before) || string.IsNullOrWhiteSpace(after)) return false;
        if (IsCjkPunctuation(before) || IsCjkPunctuation(after)) return false;
        if (before is "(" or "[" or "{" || after is ")" or "]" or "}") return false;
        // No space before trailing punctuation, around '@' (e-mail addresses) or after an apostrophe
        // ("it's", "d'un").
        if (after is "," or "." or ":" or ";" or "!" or "?" or "%" or "@" || before is "@" or "'") return false;
        // The model reads fullwidth brackets as ASCII ones, and their built-in spacing looks like a space:
        // 中国（CHN） must not become 中国 (CHN).
        if ((after is "(" or "[" or "{" && WordBoxBuilder.IsCjk(before))
            || (before is ")" or "]" or "}" && WordBoxBuilder.IsCjk(after)))
            return false;
        if (IsDigit(after) && (IsDigit(before)
                || (before is "." or "," or ":" && right - 2 >= first && IsDigit(emitted[right - 2].Token))))
            return false;
        return true;

        static bool IsDigit(string token) => token.Length == 1 && char.IsAsciiDigit(token[0]);
    }

    /// <summary>
    /// True for CJK symbols and punctuation (U+3000–U+303F) and the fullwidth/halfwidth punctuation and symbol
    /// forms (U+FF01–U+FF65) — not fullwidth letters or digits.
    /// </summary>
    private static bool IsCjkPunctuation(string token)
    {
        if (token.Length != 1) return false;
        char c = token[0];
        return c is >= '　' and <= '〿'
            || (c is >= '！' and <= '･' && !char.IsLetterOrDigit(c));
    }

    /// <summary>
    /// Argmax over the selectable classes of one timestep. Non-selectable classes are skipped (their logit is
    /// treated as −∞). Class 0 (blank) is always selectable, so seeding the search with it is always valid;
    /// the strict <c>&gt;</c> keeps the first index of the maximum.
    /// </summary>
    private static int ArgMaxSelectable(ReadOnlySpan<float> row, bool[] selectable, out float bestProb)
    {
        int bestIndex = 0;
        bestProb = row[0];
        for (int c = 1; c < row.Length; c++)
        {
            if (!selectable[c])
                continue;
            float p = row[c];
            if (p > bestProb)
            {
                bestProb = p;
                bestIndex = c;
            }
        }
        return bestIndex;
    }
}
