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

    private static bool IsSelectable(int cc, bool[]? allowed) => cc == 0 || allowed is null || allowed[cc - 1];

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
        var sb = new System.Text.StringBuilder();
        double logProbSum = 0;   // Σ ln(maxProb) over non-blank timesteps
        int probCount = 0;
        int lastIdx = -1;

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
                        sb.Append(characters[charIdx]);
                }
            }
            lastIdx = argmax;
        }

        double confidence = probCount > 0
            ? Math.Exp(2.0 / Math.Sqrt(probCount) * logProbSum)
            : 0.0;
        return (sb.ToString(), confidence);
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
}

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
