using EasyOcrSharp.Models;

namespace EasyOcrSharp.Internal;

/// <summary>
/// CTC decoding of recognizer logits into text + confidence, independent of ONNX so it is unit
/// testable. Supports greedy decoding (EasyOCR's default) and prefix beam search (optionally lexicon
/// constrained for word beam search). Class 0 is the CTC blank; class index k≥1 maps to
/// <c>characters[k-1]</c>; vocabulary positions exported as U+0000 are EasyOCR's word-segmentation
/// separators and are treated like a blank.
/// </summary>
internal static class CtcDecoder
{
    /// <summary>
    /// Builds a per-vocabulary-position emit mask from an allow/block list, or null when neither is set.
    /// Allowlist takes precedence: positions are emit-able only if their character is in the allowlist;
    /// otherwise everything except blocklisted characters is emit-able.
    /// </summary>
    public static bool[]? BuildAllowedMask(string characters, string? allowlist, string? blocklist)
    {
        if (string.IsNullOrEmpty(allowlist) && string.IsNullOrEmpty(blocklist)) return null;

        var allowed = new bool[characters.Length];
        if (!string.IsNullOrEmpty(allowlist))
        {
            var set = new HashSet<char>(allowlist);
            for (int i = 0; i < characters.Length; i++) allowed[i] = set.Contains(characters[i]);
        }
        else
        {
            var set = new HashSet<char>(blocklist!);
            for (int i = 0; i < characters.Length; i++) allowed[i] = !set.Contains(characters[i]);
        }
        return allowed;
    }

    public static (string Text, double Confidence) Decode(
        float[,] logits, int steps, int classes, string characters, bool[]? allowed, DecoderType decoder, int beamWidth, WordTrie? trie)
        => decoder switch
        {
            DecoderType.BeamSearch => BeamSearchDecode(logits, steps, classes, characters, allowed, beamWidth, trie: null),
            DecoderType.WordBeamSearch => BeamSearchDecode(logits, steps, classes, characters, allowed, beamWidth, trie),
            _ => GreedyDecode(logits, steps, classes, characters, allowed),
        };

    /// <summary>
    /// Same decode as <see cref="Decode"/>, additionally reporting which CTC timesteps produced each
    /// emitted character. The text and confidence are bit-for-bit what <see cref="Decode"/> returns —
    /// the alignment is a by-product, never an influence.
    /// </summary>
    public static CtcDecodeResult DecodeWithAlignment(
        float[,] logits, int steps, int classes, string characters, bool[]? allowed, DecoderType decoder, int beamWidth, WordTrie? trie)
        => decoder switch
        {
            DecoderType.BeamSearch => BeamSearchDecodeWithAlignment(logits, steps, classes, characters, allowed, beamWidth, trie: null),
            DecoderType.WordBeamSearch => BeamSearchDecodeWithAlignment(logits, steps, classes, characters, allowed, beamWidth, trie),
            _ => GreedyDecodeWithAlignment(logits, steps, classes, characters, allowed),
        };

    /// <summary>
    /// Whether class <paramref name="cc"/> may be emitted. Class 0 is the CTC blank and is always selectable.
    /// <para>
    /// The bounds check is load-bearing: <paramref name="allowed"/> is sized from the vocabulary, while
    /// <c>cc</c> ranges over the model's output class count. A custom recognizer whose dictionary is shorter
    /// than its network's class count would otherwise read past the end and throw
    /// <see cref="IndexOutOfRangeException"/> from inside the recognizer's <c>Parallel.For</c>, failing the
    /// whole page. Classes with no vocabulary entry are simply not selectable — the same outcome as the
    /// unfiltered path, which already ignores them.
    /// </para>
    /// </summary>
    private static bool IsSelectable(int cc, bool[]? allowed)
        => cc == 0 || allowed is null || (cc - 1 < allowed.Length && allowed[cc - 1]);

    private static bool IsSeparator(string characters, int charIdx)
        => charIdx >= 0 && charIdx < characters.Length && characters[charIdx] == '\0';

    /// <summary>
    /// Greedy CTC decode mirroring EasyOCR: per-timestep softmax + argmax, collapse consecutive
    /// duplicates, drop the blank (index 0). Confidence is EasyOCR's <c>custom_mean</c> — the
    /// geometric-style mean <c>(∏ p)^(2/√n)</c> over the max softmax probability at every non-blank
    /// timestep.
    /// </summary>
    public static (string Text, double Confidence) GreedyDecode(float[,] logits, int steps, int classes, string characters, bool[]? allowed)
    {
        var result = GreedyCore(logits, steps, classes, characters, allowed, alignment: null);
        return (result.Text, result.Confidence);
    }

    /// <summary>
    /// <see cref="GreedyDecode"/> plus the exact CTC alignment: every emitted character carries the
    /// contiguous timestep run whose argmax produced it, and a confidence that is the mean softmax
    /// probability over that run. Text and confidence are computed by the same code path as
    /// <see cref="GreedyDecode"/>, so they are identical.
    /// </summary>
    public static CtcDecodeResult GreedyDecodeWithAlignment(float[,] logits, int steps, int classes, string characters, bool[]? allowed)
        => GreedyCore(logits, steps, classes, characters, allowed, new List<CtcCharAlignment>());

    /// <summary>
    /// The single greedy implementation. When <paramref name="alignment"/> is supplied it is filled with
    /// one entry per emitted character; when it is null the method behaves — and costs — exactly as the
    /// original alignment-free decode.
    /// </summary>
    private static CtcDecodeResult GreedyCore(
        float[,] logits, int steps, int classes, string characters, bool[]? allowed, List<CtcCharAlignment>? alignment)
    {
        var sb = new System.Text.StringBuilder();
        double logProbSum = 0;   // Σ ln(maxProb) over non-blank timesteps
        int probCount = 0;
        int lastIdx = -1;

        // Index in `alignment` of the character the current repeat-run keeps emitting (-1 = no run open),
        // plus the running mean of that run's per-timestep probabilities.
        int runEntry = -1;
        double runProbSum = 0;
        int runProbCount = 0;

        for (int t = 0; t < steps; t++)
        {
            // Numerically stable softmax over the (selectable) class dimension.
            float max = float.NegativeInfinity;
            int argmax = 0;
            for (int cc = 0; cc < classes; cc++)
            {
                if (!IsSelectable(cc, allowed)) continue;
                if (logits[t, cc] > max) { max = logits[t, cc]; argmax = cc; }
            }
            double sumExp = 0;
            for (int cc = 0; cc < classes; cc++)
            {
                if (!IsSelectable(cc, allowed)) continue;
                sumExp += Math.Exp(logits[t, cc] - max);
            }
            double prob = 1.0 / sumExp; // exp(max-max)=1 over sumExp == softmax of the argmax class

            bool runContinues = false;
            if (argmax != 0)
            {
                int charIdx = argmax - 1;
                if (!IsSeparator(characters, charIdx))
                {
                    // EasyOCR's custom_mean uses every timestep whose argmax is a real character.
                    logProbSum += Math.Log(prob);
                    probCount++;

                    // CTC collapse: emit only when the class changes from the previous step.
                    if (argmax != lastIdx && charIdx >= 0 && charIdx < characters.Length)
                    {
                        sb.Append(characters[charIdx]);
                        if (alignment is not null)
                        {
                            alignment.Add(new CtcCharAlignment(characters[charIdx], prob, t, t));
                            runEntry = alignment.Count - 1;
                            runProbSum = prob;
                            runProbCount = 1;
                            runContinues = true;
                        }
                    }
                    else if (argmax == lastIdx && runEntry >= 0 && alignment is not null)
                    {
                        // Same class repeated: the glyph is still being emitted, so widen its span.
                        runProbSum += prob;
                        runProbCount++;
                        alignment[runEntry] = alignment[runEntry] with
                        {
                            Confidence = runProbSum / runProbCount,
                            EndStep = t,
                        };
                        runContinues = true;
                    }
                }
            }
            // A blank, a separator or an unmapped class closes the current glyph's timestep run.
            if (!runContinues) runEntry = -1;
            lastIdx = argmax;
        }

        double confidence = probCount > 0
            ? Math.Exp(2.0 / Math.Sqrt(probCount) * logProbSum)
            : 0.0;
        return new CtcDecodeResult(
            sb.ToString(),
            confidence,
            alignment is null ? Array.Empty<CtcCharAlignment>() : alignment,
            steps);
    }

    /// <summary>
    /// CTC prefix beam search (Maas et al.). Explores up to <paramref name="beamWidth"/> hypotheses,
    /// summing over CTC alignments. When <paramref name="trie"/> is supplied (word beam search)
    /// extensions are constrained so every in-progress word stays a prefix of a dictionary word.
    /// Confidence is the per-character geometric mean of the winning hypothesis's alignment probability
    /// (an approximation, comparable across rounds because every pass uses the same method).
    /// </summary>
    public static (string Text, double Confidence) BeamSearchDecode(
        float[,] logits, int steps, int classes, string characters, bool[]? allowed, int beamWidth, WordTrie? trie)
    {
        beamWidth = Math.Max(1, beamWidth);

        // Per-timestep softmax over selectable classes, with blank + separators folded into one blank
        // probability and real characters aggregated by glyph.
        var stepBlank = new double[steps];
        var stepChars = new Dictionary<char, double>[steps];
        int keepCharsPerStep = Math.Max(beamWidth * 2, 8);

        for (int t = 0; t < steps; t++)
        {
            float max = float.NegativeInfinity;
            for (int cc = 0; cc < classes; cc++)
            {
                if (!IsSelectable(cc, allowed)) continue;
                if (logits[t, cc] > max) max = logits[t, cc];
            }
            double sumExp = 0;
            for (int cc = 0; cc < classes; cc++)
            {
                if (!IsSelectable(cc, allowed)) continue;
                sumExp += Math.Exp(logits[t, cc] - max);
            }

            double blank = 0;
            var chars = new Dictionary<char, double>();
            for (int cc = 0; cc < classes; cc++)
            {
                if (!IsSelectable(cc, allowed)) continue;
                double p = Math.Exp(logits[t, cc] - max) / sumExp;
                if (cc == 0) { blank += p; continue; }
                int charIdx = cc - 1;
                if (IsSeparator(characters, charIdx)) { blank += p; continue; }
                if (charIdx < 0 || charIdx >= characters.Length) { blank += p; continue; }
                char ch = characters[charIdx];
                chars[ch] = chars.TryGetValue(ch, out var cur) ? cur + p : p;
            }

            if (chars.Count > keepCharsPerStep)
            {
                chars = chars.OrderByDescending(kv => kv.Value).Take(keepCharsPerStep)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }
            stepBlank[t] = blank;
            stepChars[t] = chars;
        }

        // beam: prefix -> (pBlank, pNonBlank). Start with the empty prefix ending in blank.
        var beam = new Dictionary<string, (double pb, double pnb)> { [string.Empty] = (1.0, 0.0) };

        for (int t = 0; t < steps; t++)
        {
            var next = new Dictionary<string, (double pb, double pnb)>();
            double blank = stepBlank[t];
            var chars = stepChars[t];

            foreach (var (prefix, probs) in beam)
            {
                double total = probs.pb + probs.pnb;

                // 1) blank -> same prefix (ends in blank).
                Add(next, prefix, blank * total, 0);

                // 2) repeat the last character -> same prefix (ends in non-blank).
                if (prefix.Length > 0 && chars.TryGetValue(prefix[^1], out var pLast))
                {
                    Add(next, prefix, 0, pLast * probs.pnb);
                }

                // 3) extend by each candidate character.
                foreach (var (ch, p) in chars)
                {
                    if (trie is not null && !trie.CanExtend(prefix, ch)) continue;
                    bool sameAsLast = prefix.Length > 0 && ch == prefix[^1];
                    string np = prefix + ch;
                    if (sameAsLast)
                    {
                        // A new identical glyph can only follow a blank, else it collapses (handled in 2).
                        Add(next, np, 0, p * probs.pb);
                    }
                    else
                    {
                        Add(next, np, 0, p * total);
                    }
                }
            }

            beam = next.Count <= beamWidth
                ? next
                : next.OrderByDescending(kv => kv.Value.pb + kv.Value.pnb).Take(beamWidth)
                      .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        if (beam.Count == 0) return (string.Empty, 0.0);
        var best = beam.OrderByDescending(kv => kv.Value.pb + kv.Value.pnb).First();
        double pBest = best.Value.pb + best.Value.pnb;
        int len = best.Key.Length;
        double confidence = pBest > 0 && len > 0
            ? Math.Clamp(Math.Exp(Math.Log(pBest) / len), 0.0, 1.0)
            : pBest > 0 ? Math.Clamp(pBest, 0.0, 1.0) : 0.0;
        return (best.Key, confidence);

        static void Add(Dictionary<string, (double pb, double pnb)> map, string key, double pb, double pnb)
        {
            if (map.TryGetValue(key, out var cur)) map[key] = (cur.pb + pb, cur.pnb + pnb);
            else map[key] = (pb, pnb);
        }
    }

    /// <summary>
    /// <see cref="BeamSearchDecode"/> with an alignment attached. The prefix beam sums over <em>all</em>
    /// alignments of a hypothesis rather than tracking a single winning path, so there is no exact
    /// timestep span to report: the returned spans divide the timesteps uniformly across the decoded
    /// characters, i.e. the same proportional approximation the exporters used to make, only now applied
    /// in timestep space. Text and confidence are unaffected. Use
    /// <see cref="DecoderType.Greedy"/> when character geometry has to be exact.
    /// </summary>
    public static CtcDecodeResult BeamSearchDecodeWithAlignment(
        float[,] logits, int steps, int classes, string characters, bool[]? allowed, int beamWidth, WordTrie? trie)
    {
        var (text, confidence) = BeamSearchDecode(logits, steps, classes, characters, allowed, beamWidth, trie);
        return new CtcDecodeResult(text, confidence, UniformAlignment(text, confidence, steps), steps);
    }

    /// <summary>Spreads <paramref name="text"/> evenly over <paramref name="steps"/> timesteps.</summary>
    private static IReadOnlyList<CtcCharAlignment> UniformAlignment(string text, double confidence, int steps)
    {
        if (text.Length == 0 || steps <= 0) return Array.Empty<CtcCharAlignment>();

        var alignment = new CtcCharAlignment[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            int start = (int)(i * (long)steps / text.Length);
            int end = (int)((i + 1) * (long)steps / text.Length) - 1;
            if (end < start) end = start;
            if (end > steps - 1) end = steps - 1;
            alignment[i] = new CtcCharAlignment(text[i], confidence, start, end);
        }
        return alignment;
    }
}

/// <summary>
/// One decoded character together with the inclusive CTC timestep span <c>[StartStep, EndStep]</c> that
/// produced it, and the mean probability of the winning class over that span. Timesteps are the
/// recognizer's horizontal feature columns, so the span converts directly into a horizontal extent
/// across the recognized patch.
/// </summary>
internal readonly record struct CtcCharAlignment(char Value, double Confidence, int StartStep, int EndStep);

/// <summary>
/// A decode result carrying the per-character alignment alongside the text and confidence, plus the
/// total number of timesteps the spans are relative to.
/// </summary>
internal readonly record struct CtcDecodeResult(
    string Text,
    double Confidence,
    IReadOnlyList<CtcCharAlignment> Alignment,
    int Steps);

/// <summary>
/// Prefix index over a lexicon used by word beam search: answers "can the current in-progress word,
/// once extended by <c>ch</c>, still become a dictionary word?". Whitespace ends a word, after which a
/// fresh word starts. Matching is case-insensitive and falls back to permissive when the dictionary is
/// empty (then <see cref="Build"/> returns null and plain beam search is used).
/// </summary>
internal sealed class WordTrie
{
    private sealed class Node
    {
        public readonly Dictionary<char, Node> Next = new();
    }

    private readonly Node _root = new();

    public static WordTrie? Build(IReadOnlyCollection<string>? words)
    {
        if (words is null || words.Count == 0) return null;
        var trie = new WordTrie();
        foreach (var word in words)
        {
            if (string.IsNullOrEmpty(word)) continue;
            var node = trie._root;
            foreach (var ch in word)
            {
                var key = char.ToLowerInvariant(ch);
                if (!node.Next.TryGetValue(key, out var child))
                {
                    child = new Node();
                    node.Next[key] = child;
                }
                node = child;
            }
        }
        return trie._root.Next.Count == 0 ? null : trie;
    }

    /// <summary>True if appending <paramref name="ch"/> to the in-progress word of <paramref name="prefix"/>
    /// keeps it a valid dictionary-word prefix. Whitespace is always allowed (it separates words); a word
    /// already off the lexicon is not further constrained.</summary>
    public bool CanExtend(string prefix, char ch)
    {
        if (char.IsWhiteSpace(ch)) return true;

        // Current in-progress word = characters after the last whitespace.
        int wordStart = prefix.Length;
        while (wordStart > 0 && !char.IsWhiteSpace(prefix[wordStart - 1])) wordStart--;

        var node = _root;
        for (int i = wordStart; i < prefix.Length; i++)
        {
            if (!node.Next.TryGetValue(char.ToLowerInvariant(prefix[i]), out node!)) return true; // already off-lexicon
        }
        return node.Next.ContainsKey(char.ToLowerInvariant(ch));
    }
}
