using System.Text.RegularExpressions;

namespace EasyOcrSharp.Extraction;

/// <summary>
/// Where, relative to its anchor (label), a field's value is allowed to sit. Combinable — a definition
/// normally allows several directions and lets the score decide which candidate wins.
/// </summary>
[Flags]
public enum FieldDirection
{
    /// <summary>No direction: the definition can never match. Useful only as a starting value.</summary>
    None = 0,

    /// <summary>
    /// The value follows the anchor inside the <em>same</em> recognized line — the classic
    /// <c>"Total: 42.00"</c> shape. This needs no geometry at all, so it keeps working when the result
    /// carries no usable bounding boxes.
    /// </summary>
    SameLine = 1 << 0,

    /// <summary>The value is a separate line to the right of the anchor, on roughly the same baseline.</summary>
    Right = 1 << 1,

    /// <summary>The value is a separate line below the anchor, horizontally overlapping it (a column header).</summary>
    Below = 1 << 2,

    /// <summary>The value is a separate line to the left of the anchor, on roughly the same baseline.</summary>
    Left = 1 << 3,

    /// <summary>The value is a separate line above the anchor, horizontally overlapping it.</summary>
    Above = 1 << 4,

    /// <summary>The two same-baseline directions: <see cref="Left"/> and <see cref="Right"/>.</summary>
    Horizontal = Left | Right,

    /// <summary>The two stacked directions: <see cref="Above"/> and <see cref="Below"/>.</summary>
    Vertical = Above | Below,

    /// <summary>
    /// The layout of virtually every printed form: value inline, immediately to the right, or on the
    /// next line under the label. This is <see cref="FieldDefinition.Direction"/>'s default.
    /// </summary>
    Standard = SameLine | Right | Below,

    /// <summary>Every direction. Widest net; also the easiest way to pick up a neighbouring field's value.</summary>
    All = SameLine | Right | Below | Left | Above,
}

/// <summary>
/// Which candidate wins when a document contains an anchor more than once (a "Total" in the line-item
/// table and a "Total" in the summary box, say).
/// </summary>
public enum FieldOccurrence
{
    /// <summary>Take the candidate whose anchor appears first in reading order.</summary>
    First,

    /// <summary>
    /// Take the candidate whose anchor appears last in reading order. The right choice for invoice
    /// totals, which are repeated and where the last one is the payable amount.
    /// </summary>
    Last,

    /// <summary>Take the highest-scoring candidate regardless of position (the default).</summary>
    Best,
}

/// <summary>
/// One field to pull out of an <see cref="Models.OcrResult"/>: the label text to look for (the
/// <see cref="Anchors"/>), where its value lives relative to that label, and what the value has to look
/// like. Everything is expressed in multiples of the <em>anchor line's height</em> rather than pixels, so
/// a definition written against a 150-DPI scan works unchanged on a 600-DPI one.
/// </summary>
/// <remarks>
/// Anchors are matched fuzzily (see <see cref="FieldExtractionOptions.MinAnchorSimilarity"/>) because OCR
/// mangles labels — <c>"Totai"</c>, <c>"lnvoice"</c>, <c>"Amount Ðue"</c> all still hit. Matching is
/// case-insensitive and ignores punctuation, so <c>"TOTAL:"</c> matches the anchor <c>"total"</c>.
/// Ready-made definitions for invoices and receipts live in <see cref="FieldPresets"/>.
/// </remarks>
public sealed record FieldDefinition
{
    /// <summary>
    /// The field's name, used as the key of the extracted result. Must be unique within one extraction
    /// call — later definitions with the same name simply produce a second <see cref="ExtractedField"/>.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The label texts to look for, e.g. <c>["Total", "Amount Due", "Balance Due"]</c>. Compared
    /// case-insensitively, punctuation-insensitively and fuzzily against every window of words on every
    /// line. Empty is legal only when <see cref="AnchorPatterns"/> is non-empty.
    /// </summary>
    public IReadOnlyList<string> Anchors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Regular expressions that identify the anchor when a word list cannot. A match is treated as a
    /// perfect (similarity 1.0) anchor hit and the value is read from just after the match. Prefer
    /// <c>[GeneratedRegex]</c>-produced instances so the definition stays trim/AOT friendly, and prefer
    /// patterns with a match timeout — user-supplied patterns are run as-is.
    /// </summary>
    public IReadOnlyList<Regex> AnchorPatterns { get; init; } = Array.Empty<Regex>();

    /// <summary>
    /// Where the value may sit relative to the anchor. Defaults to <see cref="FieldDirection.Standard"/>
    /// (inline, right, or below).
    /// </summary>
    public FieldDirection Direction { get; init; } = FieldDirection.Standard;

    /// <summary>
    /// An optional pattern the value must satisfy — candidates that fail it are discarded outright, which
    /// is by far the strongest way to keep a neighbouring label out of a numeric field. When the pattern
    /// declares a group named <c>value</c>, that group is taken as the value; otherwise the whole match is.
    /// A match that covers only part of the candidate line scores slightly lower than one that covers all
    /// of it. Null (default) accepts any non-empty text.
    /// </summary>
    public Regex? ValuePattern { get; init; }

    /// <summary>
    /// How far from the anchor the value may be, as a multiple of the anchor line's height (so it is
    /// resolution-independent). The gap is measured edge-to-edge: right/left along x, above/below along y.
    /// Default 6 — roughly a third of a page-width gap horizontally, or a handful of rows vertically.
    /// Candidates beyond it are dropped; closer candidates score higher.
    /// </summary>
    public double MaxDistance { get; init; } = 6.0;

    /// <summary>
    /// How far the value may drift off the anchor's axis, again as a multiple of the anchor line's height.
    /// For <see cref="FieldDirection.Right"/>/<see cref="FieldDirection.Left"/> this is the baseline
    /// (centre-y) offset; for <see cref="FieldDirection.Below"/>/<see cref="FieldDirection.Above"/> it is
    /// the horizontal gap between the two boxes' x-ranges, which is zero whenever they overlap at all.
    /// Default 0.75.
    /// </summary>
    public double SearchBand { get; init; } = 0.75;

    /// <summary>Which candidate wins when the anchor occurs several times. Defaults to
    /// <see cref="FieldOccurrence.Best"/>.</summary>
    public FieldOccurrence Occurrence { get; init; } = FieldOccurrence.Best;

    /// <summary>
    /// Overrides <see cref="FieldExtractionOptions.MinAnchorSimilarity"/> for this definition only.
    /// Raise it for short, collision-prone labels (<c>"No"</c>, <c>"Tax"</c>); lower it for long ones that
    /// OCR reliably garbles. Null (default) uses the option value.
    /// </summary>
    public double? MinAnchorSimilarity { get; init; }

    /// <summary>
    /// How many consecutive lines may be joined into one value. Only applies to
    /// <see cref="FieldDirection.Below"/> matches with no <see cref="ValuePattern"/> — the multi-line case
    /// that matters in practice is a postal address under a "Bill To" label. Default 1 (single line).
    /// </summary>
    public int MaxValueLines { get; init; } = 1;

    /// <summary>
    /// Creates a definition from a name and a set of literal anchors, leaving every other knob at its
    /// default. The five-second way to describe a field: <c>FieldDefinition.Create("Total", "Total",
    /// "Amount Due")</c>.
    /// </summary>
    /// <param name="name">The field name (the key of the extracted result).</param>
    /// <param name="anchors">The label texts to look for.</param>
    /// <returns>A definition with <see cref="FieldDirection.Standard"/> geometry.</returns>
    public static FieldDefinition Create(string name, params string[] anchors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(anchors);
        return new FieldDefinition { Name = name, Anchors = anchors };
    }
}
