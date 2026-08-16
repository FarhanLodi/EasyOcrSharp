using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Evaluation;

/// <summary>
/// The unit character-level accuracy is counted in. OCR ground truth is usually compared per
/// user-perceived character, which is <em>not</em> the same as per UTF-16 <see cref="char"/> once
/// emoji, CJK extension B, or combining marks appear.
/// </summary>
public enum CharacterUnit
{
    /// <summary>
    /// Unicode grapheme clusters — what a reader would call "a character" (<c>é</c> written as
    /// <c>e</c> + U+0301 counts once, and a surrogate pair counts once). The default: it is the only
    /// unit for which a CER over accented or non-BMP text means what people expect.
    /// </summary>
    Grapheme,

    /// <summary>
    /// Unicode scalar values (<see cref="System.Text.Rune"/>). Surrogate pairs count once, but a base
    /// letter and its combining mark count separately. Cheaper than <see cref="Grapheme"/> and a good
    /// fit for scripts that never combine.
    /// </summary>
    Rune,

    /// <summary>
    /// Raw UTF-16 code units. Fastest, and the unit most published CER numbers were computed in, but a
    /// single emoji then counts as two errors. Use it only to reproduce an existing benchmark.
    /// </summary>
    Utf16CodeUnit,
}

/// <summary>
/// Text normalization applied to both strings before an accuracy comparison, plus the guard rails for
/// the underlying edit-distance computation. Every normalization step is <b>off by default</b> so the
/// stock metric is a strict, literal comparison; turn individual steps on to score the way your
/// benchmark scores. Use <see cref="Relaxed"/> for the usual "don't punish case and punctuation"
/// preset.
/// </summary>
/// <remarks>
/// Steps are applied in a fixed order — Unicode normalization, case folding, punctuation stripping,
/// whitespace collapsing — so the result does not depend on which combination you enable.
/// </remarks>
public sealed record TextComparisonOptions
{
    /// <summary>
    /// Fold both strings to lower case with <see cref="string.ToLowerInvariant"/> before comparing, so
    /// <c>"Hello"</c> and <c>"HELLO"</c> score as identical. Off by default.
    /// </summary>
    public bool IgnoreCase { get; init; }

    /// <summary>
    /// Replace every run of whitespace with a single space and trim the ends, so line-wrapping and
    /// double spaces do not count as errors. Off by default.
    /// </summary>
    public bool CollapseWhitespace { get; init; }

    /// <summary>
    /// Remove every character in a Unicode punctuation category (<c>Pc, Pd, Ps, Pe, Pi, Pf, Po</c> — the
    /// set <see cref="char.IsPunctuation(char)"/> reports). Mathematical and currency symbols are
    /// <em>not</em> removed, because dropping <c>$</c> or <c>%</c> usually hides a real recognition
    /// error. Off by default.
    /// </summary>
    public bool StripPunctuation { get; init; }

    /// <summary>
    /// Unicode normalization form applied to both strings first, which is what makes precomposed
    /// <c>é</c> (U+00E9) compare equal to decomposed <c>e</c> + U+0301. <see langword="null"/> (the
    /// default) leaves both strings exactly as given. <see cref="NormalizationForm.FormKC"/> also folds
    /// compatibility variants such as full-width Latin digits onto their ASCII forms. Malformed input
    /// that cannot be normalized (a lone surrogate) is left untouched rather than throwing.
    /// </summary>
    public NormalizationForm? UnicodeNormalization { get; init; }

    /// <summary>
    /// The unit <see cref="TextAccuracyMetrics.CharacterErrorRate(string, string, TextComparisonOptions?)"/>
    /// counts in. Defaults to <see cref="Evaluation.CharacterUnit.Grapheme"/>. Word-level metrics are
    /// unaffected — words are always whitespace-delimited tokens.
    /// </summary>
    public CharacterUnit CharacterUnit { get; init; } = CharacterUnit.Grapheme;

    /// <summary>
    /// Upper bound on the size of the edit-distance table, in cells (<c>(|reference|+1) × (|hypothesis|+1)</c>
    /// after common prefix/suffix trimming). Edit distance is inherently O(n·m) in time, so comparing two
    /// megabyte-sized strings would hang rather than fail; exceeding this limit throws instead. The
    /// default of 25 million cells covers a ~5 000 × 5 000 character comparison — far more than a page of
    /// OCR — and costs well under a second. Raise it deliberately.
    /// </summary>
    public long MaxComparisonCells { get; init; } = 25_000_000;

    /// <summary>The default options: no normalization at all, grapheme-cluster characters.</summary>
    public static TextComparisonOptions Default { get; } = new();

    /// <summary>
    /// The common "score the words, not the typography" preset: case folded, whitespace collapsed,
    /// punctuation stripped and compatibility-normalized (<see cref="NormalizationForm.FormKC"/>).
    /// </summary>
    public static TextComparisonOptions Relaxed { get; } = new()
    {
        IgnoreCase = true,
        CollapseWhitespace = true,
        StripPunctuation = true,
        UnicodeNormalization = NormalizationForm.FormKC,
    };
}

/// <summary>
/// The breakdown of one optimal alignment between a reference and a hypothesis: how many units matched
/// and how many of each edit operation were needed to turn the reference into the hypothesis.
/// </summary>
/// <param name="Hits">Units that matched exactly (correct recognitions).</param>
/// <param name="Substitutions">Reference units the hypothesis replaced with a different unit.</param>
/// <param name="Insertions">Units the hypothesis added that the reference does not contain.</param>
/// <param name="Deletions">Reference units missing from the hypothesis.</param>
public readonly record struct ErrorCounts(int Hits, int Substitutions, int Insertions, int Deletions)
{
    /// <summary>An alignment of two empty sequences: no hits and no errors.</summary>
    public static ErrorCounts Empty { get; } = new(0, 0, 0, 0);

    /// <summary>
    /// The length of the reference, recovered from the alignment: every reference unit was either hit,
    /// substituted or deleted.
    /// </summary>
    public int ReferenceLength => Hits + Substitutions + Deletions;

    /// <summary>
    /// The length of the hypothesis, recovered from the alignment: every hypothesis unit was either a
    /// hit, a substitution or an insertion.
    /// </summary>
    public int HypothesisLength => Hits + Substitutions + Insertions;

    /// <summary>The edit distance — substitutions plus insertions plus deletions.</summary>
    public int TotalErrors => Substitutions + Insertions + Deletions;

    /// <summary>
    /// Errors divided by reference length, the standard CER/WER definition. Note that this can exceed
    /// 1.0 when the hypothesis is much longer than the reference. An empty reference scores 0.0 against
    /// an empty hypothesis and 1.0 against any non-empty one.
    /// </summary>
    public double ErrorRate => ReferenceLength == 0
        ? (TotalErrors == 0 ? 0.0 : 1.0)
        : (double)TotalErrors / ReferenceLength;

    /// <summary>
    /// <c>1 - </c><see cref="ErrorRate"/>, clamped to the 0–1 range so it reads as a percentage even
    /// when the recognizer hallucinated more text than the reference contains.
    /// </summary>
    public double Accuracy => Math.Clamp(1.0 - ErrorRate, 0.0, 1.0);
}

/// <summary>
/// The result of <see cref="TextAccuracyMetrics.Compare(string, string, TextComparisonOptions?)"/>: the
/// character- and word-level alignments of the same pair of strings, so a single pass reports both
/// rates together with the operation counts behind them.
/// </summary>
public sealed record TextComparisonReport
{
    /// <summary>The character-level alignment, counted in the configured <see cref="CharacterUnit"/>.</summary>
    public required ErrorCounts Characters { get; init; }

    /// <summary>The word-level alignment over whitespace-delimited tokens.</summary>
    public required ErrorCounts Words { get; init; }

    /// <summary>The character error rate — <see cref="ErrorCounts.ErrorRate"/> of <see cref="Characters"/>.</summary>
    public double CharacterErrorRate => Characters.ErrorRate;

    /// <summary>The word error rate — <see cref="ErrorCounts.ErrorRate"/> of <see cref="Words"/>.</summary>
    public double WordErrorRate => Words.ErrorRate;
}

/// <summary>
/// Character- and word-error-rate metrics for scoring OCR output against ground truth. Everything here
/// is a pure function — no models, no I/O — so it is safe to call from tests, benchmarks and CI gates.
/// </summary>
/// <remarks>
/// <para>
/// CER and WER are both <c>(substitutions + insertions + deletions) / reference length</c>, where the
/// operation counts come from a minimal Levenshtein alignment. The alignment is computed with two
/// rolling rows, so memory is O(min(n, m)) regardless of how long the strings are; the smaller of the
/// two sequences always becomes the inner dimension.
/// </para>
/// <para>
/// An edit distance has many optimal alignments and they do not all split the same total into the same
/// substitution/insertion/deletion mix. <see cref="Compare(string, string, TextComparisonOptions?)"/>
/// resolves ties deterministically by preferring the diagonal (match or substitution), then a deletion,
/// then an insertion — so the counts are stable across runs, but do not read anything into a particular
/// split beyond its total.
/// </para>
/// </remarks>
public static class TextAccuracyMetrics
{
    /// <summary>
    /// Computes the character error rate of <paramref name="hypothesis"/> against
    /// <paramref name="reference"/>: edit distance divided by the reference length, counted in the
    /// <see cref="TextComparisonOptions.CharacterUnit"/> the options select (grapheme clusters by
    /// default). 0.0 is a perfect match; values above 1.0 are possible when the hypothesis is far longer
    /// than the reference.
    /// </summary>
    /// <param name="reference">The ground-truth text.</param>
    /// <param name="hypothesis">The recognized text being scored.</param>
    /// <param name="options">Normalization and guard-rail options; <see langword="null"/> uses <see cref="TextComparisonOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException">Either string is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The comparison would exceed <see cref="TextComparisonOptions.MaxComparisonCells"/>.</exception>
    public static double CharacterErrorRate(string reference, string hypothesis, TextComparisonOptions? options = null)
        => CompareCharacters(reference, hypothesis, options).ErrorRate;

    /// <summary>
    /// Computes the word error rate of <paramref name="hypothesis"/> against
    /// <paramref name="reference"/>: edit distance over whitespace-delimited tokens divided by the
    /// number of reference tokens. Tokens are compared whole — a one-letter slip costs a full word.
    /// </summary>
    /// <param name="reference">The ground-truth text.</param>
    /// <param name="hypothesis">The recognized text being scored.</param>
    /// <param name="options">Normalization and guard-rail options; <see langword="null"/> uses <see cref="TextComparisonOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException">Either string is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The comparison would exceed <see cref="TextComparisonOptions.MaxComparisonCells"/>.</exception>
    public static double WordErrorRate(string reference, string hypothesis, TextComparisonOptions? options = null)
        => CompareWords(reference, hypothesis, options).ErrorRate;

    /// <summary>
    /// Scores <paramref name="hypothesis"/> against <paramref name="reference"/> at both character and
    /// word level in one call, reporting hits, substitutions, insertions and deletions alongside the two
    /// rates. Use this when you need to know <i>how</i> the recognizer failed, not just how much.
    /// </summary>
    /// <param name="reference">The ground-truth text.</param>
    /// <param name="hypothesis">The recognized text being scored.</param>
    /// <param name="options">Normalization and guard-rail options; <see langword="null"/> uses <see cref="TextComparisonOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException">Either string is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The comparison would exceed <see cref="TextComparisonOptions.MaxComparisonCells"/>.</exception>
    public static TextComparisonReport Compare(string reference, string hypothesis, TextComparisonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(hypothesis);
        var o = options ?? TextComparisonOptions.Default;
        var r = Normalize(reference, o);
        var h = Normalize(hypothesis, o);
        return new TextComparisonReport
        {
            Characters = AlignCharacters(r, h, o),
            Words = AlignWords(r, h, o),
        };
    }

    /// <summary>
    /// Scores an OCR result's <see cref="OcrResult.FullText"/> against the expected text at character
    /// level. Lines are joined exactly as the recognizer produced them, so enable
    /// <see cref="TextComparisonOptions.CollapseWhitespace"/> when your ground truth wraps differently.
    /// </summary>
    /// <param name="result">The OCR result to score.</param>
    /// <param name="expectedText">The ground-truth text.</param>
    /// <param name="options">Normalization and guard-rail options; <see langword="null"/> uses <see cref="TextComparisonOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> or <paramref name="expectedText"/> is <see langword="null"/>.</exception>
    public static double CharacterErrorRate(this OcrResult result, string expectedText, TextComparisonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return CharacterErrorRate(expectedText, result.FullText, options);
    }

    /// <summary>
    /// Scores an OCR result's <see cref="OcrResult.FullText"/> against the expected text at word level.
    /// </summary>
    /// <param name="result">The OCR result to score.</param>
    /// <param name="expectedText">The ground-truth text.</param>
    /// <param name="options">Normalization and guard-rail options; <see langword="null"/> uses <see cref="TextComparisonOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> or <paramref name="expectedText"/> is <see langword="null"/>.</exception>
    public static double WordErrorRate(this OcrResult result, string expectedText, TextComparisonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return WordErrorRate(expectedText, result.FullText, options);
    }

    /// <summary>
    /// Scores an OCR result's <see cref="OcrResult.FullText"/> against the expected text at both
    /// character and word level, with the full operation breakdown.
    /// </summary>
    /// <param name="result">The OCR result to score.</param>
    /// <param name="expectedText">The ground-truth text.</param>
    /// <param name="options">Normalization and guard-rail options; <see langword="null"/> uses <see cref="TextComparisonOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> or <paramref name="expectedText"/> is <see langword="null"/>.</exception>
    public static TextComparisonReport Compare(this OcrResult result, string expectedText, TextComparisonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Compare(expectedText, result.FullText, options);
    }

    /// <summary>
    /// Applies the normalization steps of <paramref name="options"/> to a single string — Unicode
    /// normalization, then case folding, then punctuation stripping, then whitespace collapsing. Exposed
    /// so you can normalize once and compare many times, and so tests can see exactly what the metrics
    /// compared.
    /// </summary>
    /// <param name="text">The text to normalize.</param>
    /// <param name="options">The normalization options; <see langword="null"/> uses <see cref="TextComparisonOptions.Default"/> and returns the text unchanged.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string Normalize(string text, TextComparisonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var o = options ?? TextComparisonOptions.Default;

        if (o.UnicodeNormalization is { } form)
        {
            try
            {
                text = text.Normalize(form);
            }
            catch (ArgumentException)
            {
                // Unpaired surrogates cannot be normalized; comparing the raw text is better than throwing.
            }
        }

        if (o.IgnoreCase) text = text.ToLowerInvariant();
        if (o.StripPunctuation) text = StripPunctuationCore(text);
        if (o.CollapseWhitespace) text = CollapseWhitespaceCore(text);
        return text;
    }

    /// <summary>
    /// The Levenshtein edit distance between two character sequences, computed with two rolling rows so
    /// memory is O(min(n, m)). Counts UTF-16 code units; use
    /// <see cref="Compare(string, string, TextComparisonOptions?)"/> when you need grapheme-aware counts.
    /// </summary>
    /// <param name="a">The first sequence.</param>
    /// <param name="b">The second sequence.</param>
    public static int LevenshteinDistance(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
        => Align(a, b, long.MaxValue).TotalErrors;

    // ---- internals ----

    private static ErrorCounts CompareCharacters(string reference, string hypothesis, TextComparisonOptions? options)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(hypothesis);
        var o = options ?? TextComparisonOptions.Default;
        return AlignCharacters(Normalize(reference, o), Normalize(hypothesis, o), o);
    }

    private static ErrorCounts CompareWords(string reference, string hypothesis, TextComparisonOptions? options)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(hypothesis);
        var o = options ?? TextComparisonOptions.Default;
        return AlignWords(Normalize(reference, o), Normalize(hypothesis, o), o);
    }

    private static ErrorCounts AlignCharacters(string reference, string hypothesis, TextComparisonOptions o)
        => o.CharacterUnit switch
        {
            CharacterUnit.Utf16CodeUnit => Align<char>(reference.AsSpan(), hypothesis.AsSpan(), o.MaxComparisonCells),
            CharacterUnit.Rune => Align<Rune>(SplitRunes(reference), SplitRunes(hypothesis), o.MaxComparisonCells),
            _ => Align<string>(SplitGraphemes(reference), SplitGraphemes(hypothesis), o.MaxComparisonCells),
        };

    private static ErrorCounts AlignWords(string reference, string hypothesis, TextComparisonOptions o)
        => Align<string>(SplitWords(reference), SplitWords(hypothesis), o.MaxComparisonCells);

    /// <summary>Splits on any Unicode whitespace, dropping empty tokens.</summary>
    private static string[] SplitWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Enumerates user-perceived characters (grapheme clusters).</summary>
    private static string[] SplitGraphemes(string text)
    {
        if (text.Length == 0) return [];
        var elements = new List<string>(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
        return [.. elements];
    }

    /// <summary>Enumerates Unicode scalar values; unpaired surrogates surface as U+FFFD.</summary>
    private static Rune[] SplitRunes(string text)
    {
        if (text.Length == 0) return [];
        var runes = new List<Rune>(text.Length);
        foreach (var rune in text.EnumerateRunes()) runes.Add(rune);
        return [.. runes];
    }

    private static string StripPunctuationCore(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (!char.IsPunctuation(c)) sb.Append(c);
        }
        return sb.Length == text.Length ? text : sb.ToString();
    }

    private static string CollapseWhitespaceCore(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }
            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Levenshtein alignment carrying the operation breakdown in each cell, over two rolling rows. The
    /// shorter sequence becomes the inner (column) dimension, so the working set is O(min(n, m)); when
    /// the two are swapped to achieve that, insertions and deletions are swapped back on the way out.
    /// A common prefix and suffix are matched off first — for near-identical OCR text that removes
    /// almost the whole table.
    /// </summary>
    private static ErrorCounts Align<T>(ReadOnlySpan<T> reference, ReadOnlySpan<T> hypothesis, long maxCells)
        where T : notnull, IEquatable<T>
    {
        int shared = Math.Min(reference.Length, hypothesis.Length);

        int prefix = 0;
        while (prefix < shared && reference[prefix].Equals(hypothesis[prefix])) prefix++;

        int suffix = 0;
        while (suffix < shared - prefix
               && reference[reference.Length - 1 - suffix].Equals(hypothesis[hypothesis.Length - 1 - suffix]))
        {
            suffix++;
        }

        int forcedHits = prefix + suffix;
        var a = reference[prefix..(reference.Length - suffix)];
        var b = hypothesis[prefix..(hypothesis.Length - suffix)];

        if (a.Length == 0) return new ErrorCounts(forcedHits, 0, b.Length, 0);
        if (b.Length == 0) return new ErrorCounts(forcedHits, 0, 0, a.Length);

        // Keep the smaller sequence in the columns so the two rolling rows stay O(min(n, m)).
        // (Spans are ref structs and cannot be swapped through a tuple, hence the explicit temporary.)
        bool swapped = b.Length > a.Length;
        if (swapped)
        {
            var temp = a;
            a = b;
            b = temp;
        }

        long cells = ((long)a.Length + 1) * (b.Length + 1);
        if (cells > maxCells)
        {
            throw new ArgumentException(
                $"Comparing {reference.Length} against {hypothesis.Length} units needs {cells} edit-distance cells, " +
                $"which exceeds the configured limit of {maxCells}. Raise TextComparisonOptions.MaxComparisonCells " +
                "if the comparison really is this large.");
        }

        var previous = new ErrorCounts[b.Length + 1];
        var current = new ErrorCounts[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = new ErrorCounts(0, 0, j, 0);

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = new ErrorCounts(0, 0, 0, i);
            var ai = a[i - 1];
            for (int j = 1; j <= b.Length; j++)
            {
                var diagonal = previous[j - 1];
                if (ai.Equals(b[j - 1]))
                {
                    // A match on the diagonal is always optimal; no other move can beat an unchanged cost.
                    current[j] = diagonal with { Hits = diagonal.Hits + 1 };
                    continue;
                }

                var best = diagonal with { Substitutions = diagonal.Substitutions + 1 };

                var deletion = previous[j];
                deletion = deletion with { Deletions = deletion.Deletions + 1 };
                if (deletion.TotalErrors < best.TotalErrors) best = deletion;

                var insertion = current[j - 1];
                insertion = insertion with { Insertions = insertion.Insertions + 1 };
                if (insertion.TotalErrors < best.TotalErrors) best = insertion;

                current[j] = best;
            }
            (previous, current) = (current, previous);
        }

        var result = previous[b.Length];
        if (swapped) result = result with { Insertions = result.Deletions, Deletions = result.Insertions };
        return result with { Hits = result.Hits + forcedHits };
    }
}
