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
    {
        ArgumentNullException.ThrowIfNull(vocab);

        // A mask whose length disagrees with the class count can't be safely indexed in the hot loop; ignore
        // it (decode unfiltered) rather than risk an out-of-range read mid-batch.
        if (selectable is not null && selectable.Length != numClasses)
            selectable = null;

        var sb = new StringBuilder(timeSteps);
        double confidenceSum = 0d;
        int keptCount = 0;
        // The argmax index of the previous timestep, used to collapse consecutive repeats (CTC best path).
        // -1 is a sentinel that can never equal a real class index, so the first timestep is never treated
        // as a repeat.
        int previousIndex = -1;

        for (int t = 0; t < timeSteps; t++)
        {
            int offset = t * numClasses;

            // Per-timestep argmax over the C classes, tracking the max probability for the confidence.
            // When a selectable mask is present, non-selectable classes are skipped (their logit is treated
            // as −∞). Class 0 (blank) is always selectable, so seeding the search with it is always valid.
            int bestIndex = 0;
            float bestProb = logits[offset];
            for (int c = 1; c < numClasses; c++)
            {
                if (selectable is not null && !selectable[c])
                    continue;
                float p = logits[offset + c];
                if (p > bestProb)
                {
                    bestProb = p;
                    bestIndex = c;
                }
            }

            // CTC best-path collapse: skip the blank (index 0) and skip a class that repeats the previous
            // timestep's class. Only the *kept* timesteps (those that emit a character) contribute to the
            // confidence — this matches PaddleOCR's CTCLabelDecode, which averages the selected probs.
            if (bestIndex != 0 && bestIndex != previousIndex)
            {
                // Guard against a vocab/model class-count mismatch rather than throwing mid-batch.
                if (bestIndex < vocab.Count)
                {
                    sb.Append(vocab[bestIndex]);
                    confidenceSum += bestProb;
                    keptCount++;
                }
            }

            previousIndex = bestIndex;
        }

        // An empty decode (all-blank sequence) has no characters to average; report confidence 0.
        float confidence = keptCount > 0 ? (float)(confidenceSum / keptCount) : 0f;
        return (sb.ToString(), confidence);
    }
}
