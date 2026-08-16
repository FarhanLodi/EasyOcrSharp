using System.Globalization;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Extraction;

/// <summary>
/// How an <see cref="ExtractedField"/>'s value was found — the geometric relationship that won, or
/// <see cref="Inline"/> when the value came from the anchor's own line.
/// </summary>
public enum FieldMatchKind
{
    /// <summary>The value followed the label inside the same recognized line (<c>"Total: 42.00"</c>).</summary>
    Inline,

    /// <summary>The value was a separate line to the right of the label.</summary>
    Right,

    /// <summary>The value was a separate line below the label.</summary>
    Below,

    /// <summary>The value was a separate line to the left of the label.</summary>
    Left,

    /// <summary>The value was a separate line above the label.</summary>
    Above,
}

/// <summary>
/// One field pulled out of an <see cref="OcrResult"/> by
/// <see cref="FieldExtractionExtensions.ExtractFields(OcrResult, IEnumerable{FieldDefinition}, FieldExtractionOptions?)"/>:
/// the cleaned-up <see cref="Value"/>, the geometry it came from, and enough of the scoring to explain
/// why this candidate beat the others.
/// </summary>
public sealed record ExtractedField
{
    /// <summary>The <see cref="FieldDefinition.Name"/> this field was extracted for.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The extracted value, trimmed and with the label and its separator removed. Never empty — a
    /// definition that only finds blank candidates produces no field at all.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// Recognition confidence (0–1) of the value: the mean confidence of the lines it came from. This is
    /// the recognizer's opinion of the characters, not this extractor's opinion of the match — for that,
    /// see <see cref="Score"/>.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// How good the match is (0–1): the weighted blend of anchor similarity, distance, alignment and
    /// value-pattern coverage described on <see cref="FieldExtractionOptions"/>. The candidate with the
    /// highest score wins unless <see cref="FieldDefinition.Occurrence"/> says otherwise.
    /// </summary>
    public double Score { get; init; }

    /// <summary>Which relationship to the label produced the value.</summary>
    public FieldMatchKind MatchKind { get; init; }

    /// <summary>
    /// The anchor text that matched, exactly as it was written in the definition (or, for a
    /// <see cref="FieldDefinition.AnchorPatterns"/> hit, the matched substring of the line).
    /// </summary>
    public string? MatchedAnchor { get; init; }

    /// <summary>
    /// How closely the label on the page resembled <see cref="MatchedAnchor"/>, 0–1. Below 1 means OCR
    /// mangled the label and the fuzzy matcher recovered it.
    /// </summary>
    public double AnchorSimilarity { get; init; }

    /// <summary>
    /// The edge-to-edge gap between label and value, in multiples of the label line's height. Always 0
    /// for <see cref="FieldMatchKind.Inline"/>.
    /// </summary>
    public double Distance { get; init; }

    /// <summary>The recognized line the label was found on. Null only if the result carried no lines.</summary>
    public OcrLine? AnchorLine { get; init; }

    /// <summary>
    /// The recognized line(s) the value was read from — the anchor line itself for an inline match, one
    /// line for the usual geometric match, or several when <see cref="FieldDefinition.MaxValueLines"/>
    /// allowed a multi-line value.
    /// </summary>
    public IReadOnlyList<OcrLine> ValueLines { get; init; } = Array.Empty<OcrLine>();

    /// <summary>
    /// The union of the value lines' boxes, in source-image pixels — where to draw the highlight.
    /// <see cref="OcrBoundingBox.Empty"/> when the result carried no geometry.
    /// </summary>
    public OcrBoundingBox BoundingBox { get; init; } = OcrBoundingBox.Empty;

    /// <summary>
    /// A one-line, human-readable account of why this candidate won — anchor, similarity, direction,
    /// distance and score. Meant for logs and for the "why did it pick that?" moment; the format is
    /// diagnostic and may change between versions.
    /// </summary>
    public string Explanation => string.Create(
        CultureInfo.InvariantCulture,
        $"'{MatchedAnchor ?? "(none)"}' matched at {AnchorSimilarity:0.00} similarity; value taken {MatchKind} at {Distance:0.00} line-heights; score {Score:0.000}.");
}
