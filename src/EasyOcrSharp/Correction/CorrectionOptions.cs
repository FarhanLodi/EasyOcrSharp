using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Correction;

/// <summary>
/// Controls how <see cref="OcrCorrectionExtensions.Correct(OcrResult, CorrectionOptions)"/> repairs
/// recognized text. Everything is opt-in: with the defaults and no lexicon, correction is a no-op that
/// hands back the result it was given.
/// </summary>
/// <remarks>
/// <para>
/// The design principle is <b>do not touch text the recognizer was sure about</b>. A modern CRNN is
/// right far more often than a dictionary is, so lexicon-driven rewriting is gated behind
/// <see cref="MinConfidenceToCorrect"/> and only ever fires on tokens the model itself flagged as shaky.
/// Explicit <see cref="CustomReplacements"/> and the checksum-backed <see cref="Normalizers"/> are not
/// gated — those are your knowledge, not a guess.
/// </para>
/// <para>
/// One <see cref="SymSpellIndex"/> is built per <see cref="Dictionary"/> instance, cached against that
/// instance, and shared by every options value derived from it — so building options in a loop, or
/// deriving them with <c>with</c>, does not rebuild the index. Hold the options (or the lexicon) in a
/// field rather than constructing a fresh lexicon per call, and indexing happens exactly once.
/// </para>
/// </remarks>
public sealed record CorrectionOptions
{
    // Keyed on the lexicon instance, so `with`-derived options share one index and a collected lexicon
    // takes its index with it.
    private static readonly ConditionalWeakTable<IReadOnlyCollection<string>, SymSpellIndex> IndexCache = new();

    private readonly int _maxEditDistance = 1;
    private readonly double _minConfidenceToCorrect = 0.75;

    /// <summary>
    /// The lexicon of known-good words. Correction only ever replaces a token with a word from here, so
    /// an empty or absent lexicon disables lexicon-based correction entirely (field
    /// <see cref="Normalizers"/> and <see cref="CustomReplacements"/> still apply). Order it by
    /// descending frequency: ties between equally close candidates are broken in lexicon order.
    /// </summary>
    public IReadOnlyCollection<string>? Dictionary { get; init; }

    /// <summary>
    /// A pre-built index to use instead of indexing <see cref="Dictionary"/>. Set this when you want to
    /// control when the (potentially large) index is built, share one index across differently
    /// configured options, or load a lexicon once at startup. Takes precedence over
    /// <see cref="Dictionary"/> when both are set.
    /// </summary>
    public SymSpellIndex? DictionaryIndex { get; init; }

    /// <summary>
    /// How far a token may be from a lexicon word and still be corrected to it, in edit operations.
    /// Default 1 — conservative on purpose, because distance 2 starts turning real words into other real
    /// words. Raising it multiplies the index build cost (see <see cref="SymSpellIndex"/>). Clamped to
    /// 0…<see cref="SymSpellIndex.MaxSupportedEditDistance"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or above <see cref="SymSpellIndex.MaxSupportedEditDistance"/>.</exception>
    public int MaxEditDistance
    {
        get => _maxEditDistance;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, SymSpellIndex.MaxSupportedEditDistance);
            _maxEditDistance = value;
        }
    }

    /// <summary>
    /// The confidence at which the recognizer is trusted over the lexicon. A token is considered for
    /// lexicon correction only when its confidence is <b>below</b> this value; at or above it the token
    /// is left exactly as recognized. Default 0.75.
    /// </summary>
    /// <remarks>
    /// Token confidence comes from the most specific evidence available: the <em>minimum</em>
    /// <see cref="OcrChar.Confidence"/> across the token's characters when
    /// <see cref="OcrLine.Characters"/> is populated (one shaky glyph makes the whole token suspect),
    /// otherwise the matching <see cref="OcrWord.Confidence"/>, otherwise the line's confidence. Set to
    /// <c>0</c> to disable lexicon correction, or to <c>1</c> to consider every token.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside the 0–1 range.</exception>
    public double MinConfidenceToCorrect
    {
        get => _minConfidenceToCorrect;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0.0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1.0);
            _minConfidenceToCorrect = value;
        }
    }

    /// <summary>
    /// Whether a replacement adopts the casing of the token it replaces (default). An ALL-CAPS token
    /// stays all caps, a Capitalized token stays capitalized, and a lower-case token comes back lower
    /// case; anything else keeps the lexicon's own spelling. Turn it off to always emit the lexicon
    /// entry verbatim.
    /// </summary>
    public bool PreserveCase { get; init; } = true;

    /// <summary>
    /// Literal token replacements applied before anything else and <b>regardless of confidence</b> — the
    /// place for domain fixes a lexicon cannot express ("this scanner always reads our product code
    /// <c>RX-l00</c> as <c>RX-100</c>"). Matching is on the token with leading and trailing punctuation
    /// stripped, using the dictionary's own comparer, so pass a
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> dictionary for case-insensitive keys. The
    /// replacement is emitted verbatim, ignoring <see cref="PreserveCase"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CustomReplacements { get; init; }

    /// <summary>
    /// Structured-field normalizers (see <see cref="FieldNormalizers"/>), tried in order — first on the
    /// whole line, then on each token — and applied regardless of confidence, because a field that
    /// satisfies its own checksum needs no second opinion. A line or token claimed by a normalizer is
    /// not offered to the lexicon afterwards.
    /// </summary>
    public IReadOnlyList<FieldNormalizer>? Normalizers { get; init; }

    /// <summary>
    /// The shortest token the lexicon is allowed to rewrite. Default 3: at edit distance 1 almost every
    /// two-letter token is one edit from some dictionary word, so correcting them is noise. Tokens
    /// shorter than this are still eligible for <see cref="CustomReplacements"/> and
    /// <see cref="Normalizers"/>.
    /// </summary>
    public int MinTokenLength { get; init; } = 3;

    /// <summary>
    /// Whether tokens containing a digit may be corrected against the lexicon. Off by default: part
    /// numbers, amounts and dates are not dictionary words, and "correcting" them corrupts data. Use
    /// <see cref="Normalizers"/> for those instead.
    /// </summary>
    public bool CorrectTokensWithDigits { get; init; }

    /// <summary>The default options: no lexicon, edit distance 1, correcting only below 0.75 confidence.</summary>
    public static CorrectionOptions Default { get; } = new();

    /// <summary>
    /// The index backing lexicon lookups, or <see langword="null"/> when no lexicon was configured.
    /// Built on first use and cached against the <see cref="Dictionary"/> instance.
    /// </summary>
    internal SymSpellIndex? ResolveIndex()
    {
        if (DictionaryIndex is not null) return DictionaryIndex;

        var lexicon = Dictionary;
        if (lexicon is null || lexicon.Count == 0) return null;

        if (IndexCache.TryGetValue(lexicon, out var cached) && cached.MaxEditDistance == MaxEditDistance)
        {
            return cached;
        }

        var built = SymSpellIndex.Build(lexicon, MaxEditDistance);
        IndexCache.AddOrUpdate(lexicon, built);
        return built;
    }
}
