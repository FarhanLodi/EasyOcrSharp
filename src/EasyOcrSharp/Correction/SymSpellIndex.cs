using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using EasyOcrSharp.Evaluation;

namespace EasyOcrSharp.Correction;

/// <summary>
/// One candidate replacement produced by <see cref="SymSpellIndex.Lookup(string, int)"/>.
/// </summary>
/// <param name="Term">The lexicon term, in the casing it was added with.</param>
/// <param name="Distance">The plain Levenshtein distance from the queried token to <paramref name="Term"/>.</param>
/// <param name="Score">
/// The OCR-weighted distance (see <see cref="SymSpellIndex.OcrWeightedDistance(string, string)"/>) used
/// for ranking: lower is better, and it is usually well below <paramref name="Distance"/> when the
/// difference is a classic misread such as <c>0</c> for <c>O</c>.
/// </param>
public readonly record struct SymSpellSuggestion(string Term, int Distance, double Score);

/// <summary>
/// A symmetric-delete spelling index (the "SymSpell" algorithm) over a lexicon: it answers "which known
/// words are within <i>k</i> edits of this token?" without scanning the lexicon.
/// </summary>
/// <remarks>
/// <para>
/// The trick is that two strings within edit distance <i>k</i> always share a common variant obtained by
/// deleting at most <i>k</i> characters from each. Building the index therefore precomputes every
/// delete-variant of every lexicon term; a lookup generates the delete-variants of the <em>query</em>
/// only — no candidate generation over the alphabet, no lexicon scan — and then verifies each hit with a
/// real edit-distance check, since the shared-deletion test is a superset filter.
/// </para>
/// <para>
/// <b>Cost.</b> Time and memory to build are O(terms × Σ<sub>i≤k</sub> C(len, i)): roughly
/// <c>len + 1</c> entries per term at <c>k = 1</c>, but <c>len²/2</c> at <c>k = 2</c> and <c>len³/6</c> at
/// <c>k = 3</c>. A 50 000-word English lexicon costs a few MB at <c>k = 1</c> and tens of MB at
/// <c>k = 2</c>. Lookups are O(query delete-variants) hash probes and are unaffected by lexicon size.
/// That asymmetry is the whole point: <b>build the index once and reuse it</b> — never per token, never
/// per line. <see cref="CorrectionOptions"/> does this for you, caching one index per lexicon instance.
/// </para>
/// <para>
/// Matching is case-insensitive (invariant lower case); suggestions come back in the casing the lexicon
/// used. Delete-variants are generated over UTF-16 code units, so a variant may split a surrogate pair —
/// harmless, because both sides of the comparison use the same rule and every hit is verified afterwards.
/// </para>
/// <para>Instances are immutable and safe to share across threads.</para>
/// </remarks>
public sealed class SymSpellIndex
{
    /// <summary>
    /// The largest edit distance an index can be built for. Beyond 3 the delete neighbourhood explodes
    /// and a lookup returns so many candidates that ranking stops being meaningful.
    /// </summary>
    public const int MaxSupportedEditDistance = 3;

    private readonly FrozenDictionary<string, int[]> _deletes;
    private readonly FrozenDictionary<string, int> _termIndex;
    private readonly string[] _terms;
    private readonly string[] _lowered;

    private SymSpellIndex(
        FrozenDictionary<string, int[]> deletes,
        FrozenDictionary<string, int> termIndex,
        string[] terms,
        string[] lowered,
        int maxEditDistance)
    {
        _deletes = deletes;
        _termIndex = termIndex;
        _terms = terms;
        _lowered = lowered;
        MaxEditDistance = maxEditDistance;
    }

    /// <summary>The maximum edit distance this index was built for; lookups cannot exceed it.</summary>
    public int MaxEditDistance { get; }

    /// <summary>The number of distinct terms in the lexicon (case-insensitive duplicates collapsed).</summary>
    public int TermCount => _terms.Length;

    /// <summary>
    /// The number of delete-variant keys stored. Useful for sizing: it is the memory driver, and it grows
    /// steeply with <see cref="MaxEditDistance"/>.
    /// </summary>
    public int VariantCount => _deletes.Count;

    /// <summary>
    /// Builds an index over <paramref name="lexicon"/>. Null, empty and whitespace-only entries are
    /// ignored, entries are trimmed, and terms that differ only in casing collapse onto the first
    /// spelling seen — so order your lexicon by descending frequency to get the most likely spelling and
    /// the better tie-break out of <see cref="Lookup(string, int)"/>.
    /// </summary>
    /// <param name="lexicon">The known-good words.</param>
    /// <param name="maxEditDistance">Edit-distance radius to index, 0 to <see cref="MaxSupportedEditDistance"/>. Defaults to 1.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lexicon"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEditDistance"/> is negative or above <see cref="MaxSupportedEditDistance"/>.</exception>
    public static SymSpellIndex Build(IEnumerable<string> lexicon, int maxEditDistance = 1)
    {
        ArgumentNullException.ThrowIfNull(lexicon);
        ArgumentOutOfRangeException.ThrowIfNegative(maxEditDistance);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxEditDistance, MaxSupportedEditDistance);

        var terms = new List<string>();
        var lowered = new List<string>();
        var termIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var raw in lexicon)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var term = raw.Trim();
            var lower = term.ToLowerInvariant();
            if (!termIndex.TryAdd(lower, terms.Count)) continue;
            terms.Add(term);
            lowered.Add(lower);
        }

        var deletes = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var variants = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new List<string>();
        var next = new List<string>();

        for (int i = 0; i < lowered.Count; i++)
        {
            CollectDeleteVariants(lowered[i], maxEditDistance, variants, frontier, next);
            foreach (var variant in variants)
            {
                if (!deletes.TryGetValue(variant, out var bucket))
                {
                    bucket = new List<int>(1);
                    deletes[variant] = bucket;
                }
                bucket.Add(i);
            }
        }

        return new SymSpellIndex(
            deletes.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal),
            termIndex.ToFrozenDictionary(StringComparer.Ordinal),
            [.. terms],
            [.. lowered],
            maxEditDistance);
    }

    /// <summary>
    /// Whether the lexicon contains <paramref name="term"/>, ignoring case and surrounding whitespace.
    /// A token that is already a known word must never be "corrected".
    /// </summary>
    /// <param name="term">The token to test.</param>
    /// <exception cref="ArgumentNullException"><paramref name="term"/> is <see langword="null"/>.</exception>
    public bool Contains(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        return _termIndex.ContainsKey(term.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// Returns the lexicon terms within <paramref name="maxEditDistance"/> edits of
    /// <paramref name="term"/>, best first. Ranking is by <see cref="SymSpellSuggestion.Score"/> (the
    /// OCR-weighted distance), then by plain distance, then by position in the lexicon — so a candidate
    /// that differs only by a classic misread such as <c>rn</c> for <c>m</c> outranks an unrelated word
    /// at the same edit distance.
    /// </summary>
    /// <param name="term">The token to look up.</param>
    /// <param name="maxEditDistance">Radius for this query; -1 (the default) or any larger value uses <see cref="MaxEditDistance"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="term"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<SymSpellSuggestion> Lookup(string term, int maxEditDistance = -1)
    {
        ArgumentNullException.ThrowIfNull(term);
        int radius = maxEditDistance < 0 ? MaxEditDistance : Math.Min(maxEditDistance, MaxEditDistance);
        var query = term.Trim().ToLowerInvariant();
        if (query.Length == 0 || _terms.Length == 0) return Array.Empty<SymSpellSuggestion>();

        var variants = new HashSet<string>(StringComparer.Ordinal);
        CollectDeleteVariants(query, radius, variants, new List<string>(), new List<string>());

        var candidates = new HashSet<int>();
        foreach (var variant in variants)
        {
            if (_deletes.TryGetValue(variant, out var bucket))
            {
                foreach (int index in bucket) candidates.Add(index);
            }
        }
        if (candidates.Count == 0) return Array.Empty<SymSpellSuggestion>();

        var ranked = new List<(double Score, int Distance, int Index)>(candidates.Count);
        foreach (int index in candidates)
        {
            int distance = TextAccuracyMetrics.LevenshteinDistance(query, _lowered[index]);
            if (distance > radius) continue;
            ranked.Add((OcrWeightedDistance(query, _lowered[index]), distance, index));
        }
        if (ranked.Count == 0) return Array.Empty<SymSpellSuggestion>();

        ranked.Sort(static (x, y) =>
        {
            int cmp = x.Score.CompareTo(y.Score);
            if (cmp != 0) return cmp;
            cmp = x.Distance.CompareTo(y.Distance);
            return cmp != 0 ? cmp : x.Index.CompareTo(y.Index);
        });

        var suggestions = new SymSpellSuggestion[ranked.Count];
        for (int i = 0; i < ranked.Count; i++)
        {
            suggestions[i] = new SymSpellSuggestion(_terms[ranked[i].Index], ranked[i].Distance, ranked[i].Score);
        }
        return suggestions;
    }

    /// <summary>
    /// An edit distance that knows what OCR gets wrong. It is a Levenshtein variant, case-insensitive,
    /// in which a substitution between two glyphs that recognizers routinely confuse
    /// (<c>0</c>/<c>O</c>, <c>1</c>/<c>l</c>/<c>I</c>, <c>5</c>/<c>S</c>, <c>8</c>/<c>B</c>,
    /// <c>2</c>/<c>Z</c>, <c>6</c>/<c>G</c>, <c>9</c>/<c>q</c>, <c>u</c>/<c>v</c>, <c>c</c>/<c>e</c>)
    /// costs <c>0.3</c> instead of <c>1</c>, and in which a two-for-one merge or split that the shape of
    /// the glyphs invites (<c>rn</c>↔<c>m</c>, <c>cl</c>↔<c>d</c>, <c>vv</c>↔<c>w</c>, <c>ri</c>↔<c>n</c>)
    /// costs <c>0.4</c> instead of the <c>2</c> a plain edit distance would charge.
    /// </summary>
    /// <param name="a">The first string.</param>
    /// <param name="b">The second string.</param>
    /// <returns>A non-negative cost; <c>0</c> exactly when the strings are equal ignoring case.</returns>
    /// <remarks>
    /// Used for ranking only — candidate <em>admission</em> still uses the plain integer edit distance, so
    /// a cheap confusion can never smuggle in a candidate that is genuinely too far away. Computed with
    /// three rolling rows (the merge rules reach back two positions), so memory stays O(min(n, m)).
    /// </remarks>
    /// <exception cref="ArgumentNullException">Either string is <see langword="null"/>.</exception>
    public static double OcrWeightedDistance(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var x = a.ToLowerInvariant();
        var y = b.ToLowerInvariant();
        int n = x.Length, m = y.Length;
        if (n == 0) return m;
        if (m == 0) return n;

        var row0 = new double[m + 1];   // row i-2
        var row1 = new double[m + 1];   // row i-1
        var row2 = new double[m + 1];   // row i
        for (int j = 0; j <= m; j++) row1[j] = j;

        for (int i = 1; i <= n; i++)
        {
            row2[0] = i;
            for (int j = 1; j <= m; j++)
            {
                double best = row1[j - 1] + SubstitutionCost(x[i - 1], y[j - 1]);

                double deletion = row1[j] + 1.0;
                if (deletion < best) best = deletion;

                double insertion = row2[j - 1] + 1.0;
                if (insertion < best) best = insertion;

                if (i >= 2)
                {
                    double merge = row0[j - 1] + MergeCost(x[i - 2], x[i - 1], y[j - 1]);
                    if (merge < best) best = merge;
                }
                if (j >= 2)
                {
                    double split = row1[j - 2] + MergeCost(y[j - 2], y[j - 1], x[i - 1]);
                    if (split < best) best = split;
                }

                row2[j] = best;
            }

            var scratch = row0;
            row0 = row1;
            row1 = row2;
            row2 = scratch;
        }

        return row1[m];
    }

    /// <summary>
    /// Whether two characters are a well-known OCR confusion pair, compared case-insensitively.
    /// </summary>
    /// <param name="first">The first character.</param>
    /// <param name="second">The second character.</param>
    public static bool IsConfusable(char first, char second)
    {
        char x = char.ToLowerInvariant(first);
        char y = char.ToLowerInvariant(second);
        if (x == y) return false;
        if (x > y) (x, y) = (y, x);
        return (x, y) switch
        {
            ('0', 'o') => true,
            ('0', 'q') => true,
            ('1', 'i') => true,
            ('1', 'l') => true,
            ('2', 'z') => true,
            ('5', 's') => true,
            ('6', 'g') => true,
            ('8', 'b') => true,
            ('9', 'q') => true,
            ('c', 'e') => true,
            ('i', 'l') => true,
            ('u', 'v') => true,
            _ => false,
        };
    }

    private static double SubstitutionCost(char a, char b)
    {
        if (a == b) return 0.0;
        return IsConfusable(a, b) ? 0.3 : 1.0;
    }

    /// <summary>Cost of reading the two characters <paramref name="first"/><paramref name="second"/> as the single character <paramref name="merged"/>.</summary>
    private static double MergeCost(char first, char second, char merged)
    {
        char a = char.ToLowerInvariant(first);
        char b = char.ToLowerInvariant(second);
        char c = char.ToLowerInvariant(merged);
        return (a, b, c) switch
        {
            ('r', 'n', 'm') => 0.4,
            ('c', 'l', 'd') => 0.4,
            ('v', 'v', 'w') => 0.4,
            ('r', 'i', 'n') => 0.4,
            _ => 2.0,   // never cheaper than the substitution + deletion this move replaces
        };
    }

    /// <summary>
    /// Fills <paramref name="into"/> with <paramref name="term"/> and every string obtained by deleting up
    /// to <paramref name="maxEditDistance"/> characters from it. The scratch lists are passed in so a
    /// whole-lexicon build does not allocate two lists per term.
    /// </summary>
    private static void CollectDeleteVariants(
        string term, int maxEditDistance, HashSet<string> into, List<string> frontier, List<string> next)
    {
        into.Clear();
        frontier.Clear();
        into.Add(term);
        frontier.Add(term);

        for (int depth = 0; depth < maxEditDistance; depth++)
        {
            next.Clear();
            foreach (var candidate in frontier)
            {
                for (int i = 0; i < candidate.Length; i++)
                {
                    // Delete a whole scalar, not one UTF-16 code unit. Removing half a surrogate pair
                    // produced index keys containing a lone surrogate, so a lexicon term with an astral
                    // character could never be reached at edit distance 1 by deleting *the character*.
                    int len = char.IsHighSurrogate(candidate[i]) && i + 1 < candidate.Length
                              && char.IsLowSurrogate(candidate[i + 1]) ? 2 : 1;
                    var shorter = candidate.Remove(i, len);
                    if (into.Add(shorter)) next.Add(shorter);
                    i += len - 1;
                }
            }
            if (next.Count == 0) break;
            frontier.Clear();
            frontier.AddRange(next);
        }
    }
}
