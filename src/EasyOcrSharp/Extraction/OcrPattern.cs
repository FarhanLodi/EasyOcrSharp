using System.Text.RegularExpressions;

namespace EasyOcrSharp.Extraction;

/// <summary>
/// A named value pattern for <see cref="OcrMatchExtensions.FindMatches(EasyOcrSharp.Models.OcrResult, OcrPattern)"/>:
/// a regular expression plus an optional validator (reject regex hits that are not real values) and an
/// optional normalizer (produce <see cref="OcrMatch.Normalized"/>). Use the ready-made instances in
/// <see cref="OcrPatterns"/>, or build your own.
/// </summary>
/// <remarks>
/// Patterns are matched against each <see cref="EasyOcrSharp.Models.OcrLine.Text"/> separately, so a value
/// split across two recognized lines is not found. Give your expression a match timeout (the built-in
/// patterns use one second) when it may run over untrusted text.
/// </remarks>
public sealed class OcrPattern
{
    private readonly Func<Match, MatchResolution?> _resolver;

    /// <summary>
    /// Creates a pattern.
    /// </summary>
    /// <param name="regex">The expression matched against each line's text.</param>
    /// <param name="kind">The kind reported on each match. Defaults to <see cref="OcrMatchKind.Custom"/>.</param>
    /// <param name="validator">Optional predicate over the matched text; a hit is dropped when it returns <see langword="false"/>.</param>
    /// <param name="normalizer">Optional function producing <see cref="OcrMatch.Normalized"/> from the matched text.</param>
    /// <exception cref="ArgumentNullException"><paramref name="regex"/> is <see langword="null"/>.</exception>
    public OcrPattern(
        Regex regex,
        OcrMatchKind kind = OcrMatchKind.Custom,
        Func<string, bool>? validator = null,
        Func<string, string?>? normalizer = null)
    {
        ArgumentNullException.ThrowIfNull(regex);
        Regex = regex;
        Kind = kind;
        _resolver = m => validator is null || validator(m.Value)
            ? new MatchResolution(m.Length, normalizer?.Invoke(m.Value))
            : null;
    }

    /// <summary>
    /// Built-in patterns: the resolver may also shorten a hit from the end (e.g. drop a trailing group that
    /// broke an IBAN's checksum because it belongs to the next word) before accepting it.
    /// </summary>
    internal OcrPattern(Regex regex, OcrMatchKind kind, Func<Match, MatchResolution?> resolver)
    {
        Regex = regex;
        Kind = kind;
        _resolver = resolver;
    }

    /// <summary>The regular expression matched against each line's text.</summary>
    public Regex Regex { get; }

    /// <summary>The kind reported on matches of this pattern.</summary>
    public OcrMatchKind Kind { get; }

    /// <summary>Validates a raw regex hit, returning the accepted length and normalized form, or <see langword="null"/> to reject.</summary>
    internal MatchResolution? Resolve(Match match) => _resolver(match);
}

/// <summary>An accepted regex hit: its (possibly shortened) length and normalized form.</summary>
internal readonly record struct MatchResolution(int Length, string? Normalized);
