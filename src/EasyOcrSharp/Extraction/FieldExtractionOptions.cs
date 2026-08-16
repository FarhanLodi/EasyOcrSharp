namespace EasyOcrSharp.Extraction;

/// <summary>
/// Cross-cutting knobs for
/// <see cref="FieldExtractionExtensions.ExtractFields(Models.OcrResult, IEnumerable{FieldDefinition}, FieldExtractionOptions?)"/>:
/// how forgiving anchor matching is, how a candidate's score is composed, and how raw text is cleaned up
/// before it becomes a value. Every setting has a sane default — pass <c>null</c> and forget it exists.
/// </summary>
/// <remarks>
/// A candidate's score is the weighted mean of three signals, each normalized to 0–1 — anchor similarity,
/// closeness to the anchor (1 at zero gap, 0 at <see cref="FieldDefinition.MaxDistance"/>), and alignment
/// quality (1 dead on axis, 0 at <see cref="FieldDefinition.SearchBand"/>) — plus a pattern signal that
/// rewards a <see cref="FieldDefinition.ValuePattern"/> covering the whole candidate line. The mean is
/// then scaled by a small per-direction bias so that, all else equal, an inline value beats one to the
/// right, which beats one below, which beats one to the left or above.
/// </remarks>
public sealed record FieldExtractionOptions
{
    /// <summary>
    /// How close a line's wording must be to an anchor to count as that anchor, from 0 (anything) to 1
    /// (character-for-character). Measured as <c>1 - editDistance / length</c> over the punctuation-free,
    /// lower-cased text, so 0.72 tolerates roughly one wrong character in four. Default 0.72.
    /// </summary>
    public double MinAnchorSimilarity { get; init; } = 0.72;

    /// <summary>
    /// Candidates scoring below this are discarded, leaving the field unextracted rather than wrong.
    /// Default 0.35. Raise it when you would rather have a gap than a guess.
    /// </summary>
    public double MinScore { get; init; } = 0.35;

    /// <summary>
    /// Whether a value may be read from the remainder of the anchor's own line (<c>"Total: 42.00"</c>).
    /// Default true. This is the only path that survives a result with no bounding boxes, so turning it
    /// off makes extraction entirely geometric. Also requires <see cref="FieldDirection.SameLine"/>.
    /// </summary>
    public bool AllowInlineValues { get; init; } = true;

    /// <summary>
    /// Characters stripped from the front of an inline value, between the label and the value proper
    /// (<c>"Total : 42.00"</c> → <c>"42.00"</c>). Default <c>":=-–—#|.…"</c> plus whitespace.
    /// </summary>
    public string InlineSeparators { get; init; } = ":=-–—#|.…";

    /// <summary>
    /// Characters trimmed from both ends of every extracted value. Default is whitespace (including the
    /// non-breaking space) plus <c>.,;:|</c> — trailing sentence punctuation is noise in a field value.
    /// Applied after
    /// <see cref="FieldDefinition.ValuePattern"/> extraction, so a pattern always has the last word.
    /// </summary>
    public string ValueTrimCharacters { get; init; } = " \t.,;:|\u00A0";

    /// <summary>Weight of anchor similarity in the score. Default 1.0.</summary>
    public double AnchorWeight { get; init; } = 1.0;

    /// <summary>Weight of closeness to the anchor in the score. Default 1.0.</summary>
    public double DistanceWeight { get; init; } = 1.0;

    /// <summary>Weight of alignment quality in the score. Default 0.75.</summary>
    public double AlignmentWeight { get; init; } = 0.75;

    /// <summary>Weight of the value-pattern signal in the score. Default 0.5.</summary>
    public double PatternWeight { get; init; } = 0.5;

    /// <summary>The default options.</summary>
    public static FieldExtractionOptions Default { get; } = new();
}
