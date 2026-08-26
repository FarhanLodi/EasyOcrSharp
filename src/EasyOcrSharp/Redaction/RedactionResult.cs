using EasyOcrSharp.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Redaction;

/// <summary>Which kind of selector produced a <see cref="RedactionMatch"/>.</summary>
public enum RedactionMatchKind
{
    /// <summary>A validated <see cref="RedactionRule"/> from <see cref="RedactionOptions.Rules"/>.</summary>
    Rule,

    /// <summary>A raw regular expression from <see cref="RedactionOptions.Patterns"/>.</summary>
    Pattern,

    /// <summary>A literal keyword from <see cref="RedactionOptions.Keywords"/>.</summary>
    Keyword,

    /// <summary>The <see cref="RedactionOptions.LinePredicate"/> escape hatch, which always covers the whole line.</summary>
    Predicate,
}

/// <summary>
/// One reason a line was redacted: what matched, which selector found it, and where it sits in the
/// line's text. Keep these for the audit trail — they are the record of what was removed, and unlike
/// the redacted image they still contain the sensitive value, so treat them accordingly.
/// </summary>
public sealed record RedactionMatch
{
    /// <summary>Gets the selector kind that fired.</summary>
    public required RedactionMatchKind Kind { get; init; }

    /// <summary>
    /// Gets the name of what fired: the <see cref="RedactionRule.Name"/>, the regular expression's
    /// pattern text, the keyword, or <c>"Predicate"</c>.
    /// </summary>
    public required string Rule { get; init; }

    /// <summary>Gets the matched text — the whole line's text for <see cref="RedactionMatchKind.Predicate"/>.</summary>
    public required string Text { get; init; }

    /// <summary>Gets the start index of the match within <see cref="OcrLine.Text"/>.</summary>
    public int Index { get; init; }

    /// <summary>Gets the length of the match in characters.</summary>
    public int Length { get; init; }
}

/// <summary>
/// One redacted line: the recognized line, why it was redacted, and the quads that were actually
/// painted over on the raster.
/// </summary>
public sealed record RedactedLine
{
    /// <summary>Gets the recognized line, exactly as OCR produced it (never rewritten).</summary>
    public required OcrLine Line { get; init; }

    /// <summary>Gets every match found in this line, in the order the selectors were evaluated.</summary>
    public required IReadOnlyList<RedactionMatch> Matches { get; init; }

    /// <summary>
    /// Gets the quadrilaterals painted for this line, in source-image pixel coordinates and already
    /// padded by <see cref="RedactionOptions.Padding"/>. One entry for whole-line redaction; one per
    /// covered word under <see cref="RedactionScope.MatchedWords"/>.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<OcrPoint>> RedactedPolygons { get; init; }

    /// <summary>
    /// Gets a value indicating whether the whole line was covered — either because
    /// <see cref="RedactionOptions.Scope"/> asked for it, or because word-level scope was requested
    /// and this line had no usable per-word geometry to fall back on.
    /// </summary>
    public required bool WholeLineRedacted { get; init; }

    /// <summary>
    /// Gets the line's text with every matched span replaced by
    /// <see cref="RedactionOptions.MaskCharacter"/>, safe to log or persist.
    /// </summary>
    public required string SanitizedText { get; init; }
}

/// <summary>
/// The outcome of a redaction pass: the obscured image, the OCR it was derived from, and the audit
/// trail of what was covered.
/// </summary>
/// <remarks>
/// Owns <see cref="Image"/>. Dispose the result (or the image itself) when finished — it is a fresh
/// allocation, never the caller's input, which is left untouched.
/// </remarks>
public sealed record RedactionResult : IDisposable
{
    /// <summary>
    /// Gets the redacted raster. The obscured pixels are gone from this image: redaction is destructive
    /// painting, not an overlay or an annotation, so flattening or re-encoding cannot reveal them.
    /// </summary>
    public required Image<Rgb24> Image { get; init; }

    /// <summary>Gets the OCR result the redaction decisions were made from, unmodified.</summary>
    public required OcrResult Ocr { get; init; }

    /// <summary>Gets one entry per redacted line, in the OCR result's reading order. Empty when nothing matched.</summary>
    public required IReadOnlyList<RedactedLine> Redactions { get; init; }

    /// <summary>
    /// Gets the full recognized text with every match masked by
    /// <see cref="RedactionOptions.MaskCharacter"/> — the transcript you can keep alongside the
    /// redacted image without re-introducing what you just removed.
    /// </summary>
    public required string SanitizedText { get; init; }

    /// <summary>Gets a value indicating whether anything was redacted.</summary>
    public bool AnyRedactions => Redactions.Count > 0;

    /// <summary>Gets the total number of quads painted onto the image.</summary>
    public int RedactedRegionCount
    {
        get
        {
            int total = 0;
            for (int i = 0; i < Redactions.Count; i++) total += Redactions[i].RedactedPolygons.Count;
            return total;
        }
    }

    /// <summary>Disposes the redacted <see cref="Image"/>.</summary>
    public void Dispose() => Image.Dispose();
}

/// <summary>The redaction outcome for a single rendered PDF page.</summary>
public sealed record PdfPageRedaction
{
    /// <summary>Gets the 1-based page number.</summary>
    public required int PageNumber { get; init; }

    /// <summary>Gets the OCR result for the page, unmodified.</summary>
    public required OcrResult Ocr { get; init; }

    /// <summary>Gets one entry per redacted line on the page.</summary>
    public required IReadOnlyList<RedactedLine> Redactions { get; init; }

    /// <summary>Gets the page's masked transcript (see <see cref="RedactionResult.SanitizedText"/>).</summary>
    public required string SanitizedText { get; init; }

    /// <summary>Gets the rendered page width in pixels (at the configured DPI).</summary>
    public int PixelWidth { get; init; }

    /// <summary>Gets the rendered page height in pixels (at the configured DPI).</summary>
    public int PixelHeight { get; init; }

    /// <summary>Gets the total number of quads painted onto the page.</summary>
    public int RedactedRegionCount
    {
        get
        {
            int total = 0;
            for (int i = 0; i < Redactions.Count; i++) total += Redactions[i].RedactedPolygons.Count;
            return total;
        }
    }
}

/// <summary>
/// The redaction outcome for a whole PDF: the rebuilt document plus the per-page audit trail.
/// </summary>
/// <remarks>
/// The rebuilt PDF is <b>image-only</b>. Every page is the rasterized, painted-over bitmap, and the
/// original page objects — text runs, fonts, annotations, embedded files, metadata — are discarded
/// rather than copied. That is the point: a PDF that keeps its text layer keeps the redacted words in
/// machine-readable form underneath the black boxes, which is how redaction failures usually happen. The
/// cost is the usual raster trade: the output is not selectable, not searchable, and larger than the
/// original. Feed it back through <c>CreateSearchablePdfAsync</c> if you need a text layer built from
/// the redacted pixels.
/// </remarks>
public sealed record PdfRedactionResult
{
    /// <summary>Gets the per-page results in document order.</summary>
    public required IReadOnlyList<PdfPageRedaction> Pages { get; init; }

    /// <summary>Gets the rebuilt, image-only PDF with the redactions burned into every page.</summary>
    public required byte[] Pdf { get; init; }

    /// <summary>Gets every page's masked transcript, separated by blank lines.</summary>
    public string SanitizedText => string.Join("\n\n", Pages.Select(p => p.SanitizedText));

    /// <summary>Gets the total number of quads painted across the document.</summary>
    public int RedactedRegionCount => Pages.Sum(p => p.RedactedRegionCount);
}
