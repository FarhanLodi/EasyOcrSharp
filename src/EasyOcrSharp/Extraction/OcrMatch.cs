using EasyOcrSharp.Models;

namespace EasyOcrSharp.Extraction;

/// <summary>
/// The kind of value an <see cref="OcrMatch"/> represents. The built-in kinds correspond to the
/// patterns in <see cref="OcrPatterns"/>.
/// </summary>
public enum OcrMatchKind
{
    /// <summary>A match from a caller-supplied <see cref="System.Text.RegularExpressions.Regex"/> or <see cref="OcrPattern"/>.</summary>
    Custom = 0,

    /// <summary>An e-mail address (<see cref="OcrPatterns.Email"/>).</summary>
    Email,

    /// <summary>A web URL (<see cref="OcrPatterns.Url"/>).</summary>
    Url,

    /// <summary>A telephone number (<see cref="OcrPatterns.Phone"/>).</summary>
    Phone,

    /// <summary>A calendar date (<see cref="OcrPatterns.Date"/>).</summary>
    Date,

    /// <summary>A monetary amount with a currency symbol or ISO 4217 code (<see cref="OcrPatterns.Amount"/>).</summary>
    Amount,

    /// <summary>An IBAN that passed its mod-97 check (<see cref="OcrPatterns.Iban"/>).</summary>
    Iban,

    /// <summary>A payment card number that passed the Luhn check (<see cref="OcrPatterns.PaymentCard"/>).</summary>
    PaymentCard,

    /// <summary>A percentage (<see cref="OcrPatterns.Percentage"/>).</summary>
    Percentage,
}

/// <summary>
/// A value found in an <see cref="OcrResult"/> by
/// <see cref="OcrMatchExtensions.FindMatches(OcrResult, OcrPattern)"/>, with its location on the page.
/// </summary>
public sealed record OcrMatch
{
    /// <summary>
    /// The matched text exactly as recognized — never altered, even when a validator repaired an OCR
    /// confusion such as <c>O</c> for <c>0</c> in order to accept it. The repaired form, when there is
    /// one, is <see cref="Normalized"/>.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// A canonical form of the value when one is well defined, otherwise <see langword="null"/>: the
    /// compact upper-case IBAN, the card number digits, an ISO <c>yyyy-MM-dd</c> date (only when the
    /// day/month order is unambiguous, unless the pattern was built with
    /// <see cref="OcrPatterns.CreateDate(EasyOcrSharp.Correction.DateFieldOrder)"/>), <c>"EUR 1234.56"</c>
    /// for amounts, <c>+15551234567</c> for phones, and so on.
    /// </summary>
    public string? Normalized { get; init; }

    /// <summary>The kind of value matched.</summary>
    public OcrMatchKind Kind { get; init; }

    /// <summary>The OCR line the value was found in.</summary>
    public required OcrLine Line { get; init; }

    /// <summary>Zero-based index of <see cref="Line"/> in <see cref="OcrResult.Lines"/>.</summary>
    public int LineIndex { get; init; }

    /// <summary>Zero-based character offset of the match within <see cref="OcrLine.Text"/>.</summary>
    public int Index { get; init; }

    /// <summary>Length of the match in characters (equals <c>Value.Length</c>).</summary>
    public int Length { get; init; }

    /// <summary>The recognition confidence (0–1) of the containing line.</summary>
    public double Confidence { get; init; }

    /// <summary>
    /// The box of the matched characters, in source-image pixels. When the line carries per-character
    /// geometry (<see cref="RecognitionOptions.WordLevelDetail"/> set to
    /// <see cref="WordLevelDetail.Characters"/>) it is the union of the matched characters' real boxes;
    /// otherwise it is estimated by narrowing the line's box in proportion to the matched range's share of
    /// the line's display width (full-width characters count double, and right-to-left lines are measured
    /// from their right edge).
    /// </summary>
    public OcrBoundingBox BoundingBox { get; init; }
}
