using System;
using System.Collections.Generic;
using System.Text;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Correction;

/// <summary>
/// Post-OCR correction: a pure, model-free pass over an <see cref="OcrResult"/> that fixes what the
/// recognizer was unsure about, using a lexicon, explicit replacements and structured-field normalizers.
/// </summary>
/// <remarks>
/// <para>
/// Correction never mutates its input. Every method returns a new value, and lines that came back
/// unchanged are reused by reference, so correcting a clean page costs almost nothing and allocates
/// almost nothing.
/// </para>
/// <para>
/// The order of operations for each token is deliberate:
/// <list type="number">
/// <item><description>
/// <see cref="CorrectionOptions.CustomReplacements"/> — your explicit knowledge, applied unconditionally.
/// </description></item>
/// <item><description>
/// <see cref="CorrectionOptions.Normalizers"/> — dates, amounts, IBANs, MRZs; applied unconditionally
/// because they validate what they change against the field's own checksum or grammar. A whole line is
/// offered to the normalizers first, so multi-token fields still work.
/// </description></item>
/// <item><description>
/// The confidence gate — anything the recognizer scored at or above
/// <see cref="CorrectionOptions.MinConfidenceToCorrect"/> stops here, untouched.
/// </description></item>
/// <item><description>
/// The lexicon — the token is looked up in the <see cref="SymSpellIndex"/> and, if it is not a known
/// word, replaced by the closest one under an OCR-aware distance that treats <c>0</c>/<c>O</c>,
/// <c>1</c>/<c>l</c>, <c>rn</c>/<c>m</c> and friends as near-free edits.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// When <see cref="OcrLine.Characters"/> is populated (recognition ran with
/// <see cref="WordLevelDetail.Characters"/>), correction gets sharper in two ways: a token's confidence
/// becomes the <em>minimum</em> of its characters' confidences rather than the whole line's, and
/// candidates that would rewrite a character the model was <em>certain</em> about are penalized — so the
/// correction lands on the glyph that was actually doubtful. Without it, everything falls back to
/// word-level and then line-level confidence, which is the default and works fine.
/// </para>
/// </remarks>
public static class OcrCorrectionExtensions
{
    /// <summary>
    /// Returns a corrected copy of the result. Lines that need no change are carried over unchanged; if
    /// no line changes at all, the original instance is returned (it is immutable, so there is nothing to
    /// copy). When at least one line changes, <see cref="OcrResult.FullText"/> is rebuilt from the
    /// corrected lines exactly as recognition builds it — non-empty lines joined by a newline.
    /// </summary>
    /// <param name="result">The result to correct. Never modified.</param>
    /// <param name="options">What to correct and how aggressively.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public static OcrResult Correct(this OcrResult result, CorrectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        if (result.Lines.Count == 0) return result;

        OcrLine[]? corrected = null;
        for (int i = 0; i < result.Lines.Count; i++)
        {
            var line = result.Lines[i];
            var fixedLine = line.Correct(options);
            if (ReferenceEquals(fixedLine, line)) continue;
            corrected ??= [.. result.Lines];
            corrected[i] = fixedLine;
        }

        if (corrected is null) return result;
        return result with { Lines = corrected, FullText = BuildFullText(corrected) };
    }

    /// <summary>
    /// Returns a corrected copy of a single line, or the same instance when nothing needed fixing.
    /// </summary>
    /// <param name="line">The line to correct. Never modified.</param>
    /// <param name="options">What to correct and how aggressively.</param>
    /// <remarks>
    /// Geometry survives correction as far as it honestly can. <see cref="OcrLine.BoundingPolygon"/> and
    /// <see cref="OcrLine.BoundingBox"/> are always kept — the ink did not move. <see cref="OcrLine.Words"/>
    /// keep their boxes and take the corrected text when the word count is unchanged (the usual case,
    /// since correction is token-for-token), and are dropped otherwise.
    /// <see cref="OcrLine.Characters"/> are dropped whenever the text changes: per-glyph extents cannot
    /// survive a substitution, and stale ones would be worse than none.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public static OcrLine Correct(this OcrLine line, CorrectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(line.Text)) return line;

        var charConfidences = BuildCharacterConfidences(line);
        var corrected = CorrectCore(line.Text, line.Confidence, charConfidences, line.Words, options);
        return string.Equals(corrected, line.Text, StringComparison.Ordinal) ? line : RebuildLine(line, corrected);
    }

    /// <summary>
    /// Corrects a bare string with no geometry attached — the entry point for text that did not come from
    /// an <see cref="OcrResult"/>, and the easiest way to unit-test a set of options.
    /// </summary>
    /// <param name="text">The text to correct.</param>
    /// <param name="options">What to correct and how aggressively.</param>
    /// <param name="confidence">
    /// The confidence to assume for every token, checked against
    /// <see cref="CorrectionOptions.MinConfidenceToCorrect"/>. Defaults to <c>0</c> — "assume nothing was
    /// certain", so every token is eligible.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public static string CorrectText(this string text, CorrectionOptions options, double confidence = 0.0)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        return CorrectCore(text, confidence, charConfidences: null, words: null, options);
    }

    // ---- core ----

    private static string CorrectCore(
        string text,
        double fallbackConfidence,
        double[]? charConfidences,
        IReadOnlyList<OcrWord>? words,
        CorrectionOptions options)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // A structured field can span several tokens (an MRZ line, "12 / 03 / 2026"), so the whole line
        // is offered to the normalizers before it is broken up.
        if (options.Normalizers is { Count: > 0 } lineNormalizers)
        {
            var whole = text.Trim();
            foreach (var normalizer in lineNormalizers)
            {
                var outcome = normalizer(whole);
                if (!outcome.Handled) continue;
                return outcome.IsValid ? outcome.Value : text;
            }
        }

        var index = options.ResolveIndex();
        var builder = new StringBuilder(text.Length);
        int position = 0;
        int tokenOrdinal = 0;

        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position]))
            {
                builder.Append(text[position]);
                position++;
                continue;
            }

            int start = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position])) position++;

            var token = text[start..position];
            double confidence = TokenConfidence(charConfidences, words, start, position, tokenOrdinal, fallbackConfidence);
            builder.Append(CorrectToken(token, confidence, charConfidences, start, index, options));
            tokenOrdinal++;
        }

        return builder.ToString();
    }

    private static string CorrectToken(
        string token,
        double confidence,
        double[]? charConfidences,
        int tokenStart,
        SymSpellIndex? index,
        CorrectionOptions options)
    {
        int lead = 0;
        while (lead < token.Length && !char.IsLetterOrDigit(token[lead])) lead++;
        int trail = token.Length;
        while (trail > lead && !char.IsLetterOrDigit(token[trail - 1])) trail--;
        if (lead >= trail) return token;   // pure punctuation, nothing to correct

        var prefix = token[..lead];
        var core = token[lead..trail];
        var suffix = token[trail..];

        if (options.CustomReplacements is { Count: > 0 } replacements &&
            replacements.TryGetValue(core, out var literal))
        {
            return prefix + literal + suffix;
        }

        if (options.Normalizers is { Count: > 0 } normalizers)
        {
            foreach (var normalizer in normalizers)
            {
                var outcome = normalizer(core);
                if (!outcome.Handled) continue;
                return outcome.IsValid ? prefix + outcome.Value + suffix : token;
            }
        }

        // The gate: text the recognizer was confident about is never second-guessed by a dictionary.
        if (index is null || confidence >= options.MinConfidenceToCorrect) return token;
        if (core.Length < options.MinTokenLength) return token;
        if (!options.CorrectTokensWithDigits && ContainsDigit(core)) return token;
        if (index.Contains(core)) return token;

        var suggestions = index.Lookup(core, options.MaxEditDistance);
        var best = PickSuggestion(suggestions, core, charConfidences, tokenStart + lead, options.MinConfidenceToCorrect);
        if (best is null) return token;

        return prefix + ApplyCase(core, best.Value.Term, options.PreserveCase) + suffix;
    }

    /// <summary>
    /// Chooses among equally admissible candidates. With per-character confidences available, a candidate
    /// is penalized for every position where it disagrees with a character the recognizer was certain
    /// about — which pushes the correction onto the glyphs that were actually doubtful.
    /// </summary>
    private static SymSpellSuggestion? PickSuggestion(
        IReadOnlyList<SymSpellSuggestion> suggestions,
        string core,
        double[]? charConfidences,
        int coreStart,
        double confidenceThreshold)
    {
        if (suggestions.Count == 0) return null;
        if (charConfidences is null) return suggestions[0];

        SymSpellSuggestion best = suggestions[0];
        double bestScore = double.PositiveInfinity;
        foreach (var suggestion in suggestions)
        {
            double score = suggestion.Score;
            if (suggestion.Term.Length == core.Length)
            {
                for (int k = 0; k < core.Length; k++)
                {
                    int index = coreStart + k;
                    if (index >= charConfidences.Length) break;
                    if (charConfidences[index] >= confidenceThreshold &&
                        char.ToLowerInvariant(core[k]) != char.ToLowerInvariant(suggestion.Term[k]))
                    {
                        score += 0.5;
                    }
                }
            }
            if (score < bestScore)
            {
                bestScore = score;
                best = suggestion;
            }
        }
        return best;
    }

    private static double TokenConfidence(
        double[]? charConfidences,
        IReadOnlyList<OcrWord>? words,
        int start,
        int end,
        int tokenOrdinal,
        double fallback)
    {
        if (charConfidences is not null && end <= charConfidences.Length)
        {
            double minimum = double.PositiveInfinity;
            for (int i = start; i < end; i++)
            {
                if (charConfidences[i] < minimum) minimum = charConfidences[i];
            }
            if (!double.IsPositiveInfinity(minimum)) return minimum;
        }

        if (words is not null && tokenOrdinal < words.Count) return words[tokenOrdinal].Confidence;
        return fallback;
    }

    /// <summary>
    /// Expands <see cref="OcrLine.Characters"/> into a confidence per <see cref="OcrLine.Text"/> position,
    /// or <see langword="null"/> when the characters are absent or do not line up with the text (in which
    /// case correction falls back to word- and line-level confidence rather than trusting bad offsets).
    /// </summary>
    private static double[]? BuildCharacterConfidences(OcrLine line)
    {
        var characters = line.Characters;
        if (characters.Count == 0) return null;

        int total = 0;
        foreach (var character in characters) total += character.Value.Length;
        if (total != line.Text.Length) return null;

        var confidences = new double[total];
        int position = 0;
        foreach (var character in characters)
        {
            for (int k = 0; k < character.Value.Length; k++) confidences[position++] = character.Confidence;
        }
        return confidences;
    }

    private static OcrLine RebuildLine(OcrLine line, string correctedText)
    {
        IReadOnlyList<OcrWord> words = Array.Empty<OcrWord>();
        if (line.Words.Count > 0)
        {
            var before = line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var after = correctedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (before.Length == after.Length && after.Length == line.Words.Count)
            {
                var rebuilt = new OcrWord[after.Length];
                for (int i = 0; i < after.Length; i++) rebuilt[i] = line.Words[i] with { Text = after[i] };
                words = rebuilt;
            }
        }

        return line with
        {
            Text = correctedText,
            Words = words,
            Characters = Array.Empty<OcrChar>(),
        };
    }

    private static bool ContainsDigit(string text)
    {
        foreach (char c in text)
        {
            if (char.IsDigit(c)) return true;
        }
        return false;
    }

    /// <summary>
    /// Re-cases a replacement to match the token it replaces: ALL CAPS stays all caps, Capitalized stays
    /// capitalized, lower case stays lower case, and mixed casing keeps the lexicon's own spelling.
    /// </summary>
    private static string ApplyCase(string original, string replacement, bool preserveCase)
    {
        if (!preserveCase || replacement.Length == 0) return replacement;

        bool hasLetter = false, allUpper = true, allLower = true;
        foreach (char c in original)
        {
            if (!char.IsLetter(c)) continue;
            hasLetter = true;
            if (char.IsUpper(c)) allLower = false;
            else if (char.IsLower(c)) allUpper = false;
        }
        if (!hasLetter) return replacement;

        if (allUpper && original.Length > 1) return replacement.ToUpperInvariant();
        if (allLower) return replacement.ToLowerInvariant();
        if (char.IsUpper(original[0]))
        {
            return string.Concat(
                char.ToUpperInvariant(replacement[0]).ToString(),
                replacement.AsSpan(1).ToString().ToLowerInvariant());
        }
        return replacement;
    }

    /// <summary>Joins non-empty line texts with a newline, matching how recognition builds <see cref="OcrResult.FullText"/>.</summary>
    private static string BuildFullText(IReadOnlyList<OcrLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line.Text)) continue;
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(line.Text);
        }
        return sb.ToString();
    }
}
