namespace EasyOcrSharp.Extraction;

/// <summary>
/// The ICAO Doc 9303 machine-readable-zone layout of a travel document.
/// </summary>
public enum MrzFormat
{
    /// <summary>TD1: three lines of 30 characters (ID cards).</summary>
    Td1,

    /// <summary>TD2: two lines of 36 characters (older ID cards; MRV-B visas share the size).</summary>
    Td2,

    /// <summary>TD3: two lines of 44 characters (passports; MRV-A visas share the size).</summary>
    Td3,
}

/// <summary>
/// Per-field ICAO 9303 check-digit outcomes for an <see cref="MrzResult"/>. <see langword="null"/> means
/// the document format has no such check digit.
/// </summary>
/// <param name="DocumentNumber">Whether the document-number check digit matches.</param>
/// <param name="BirthDate">Whether the date-of-birth check digit matches.</param>
/// <param name="ExpiryDate">Whether the date-of-expiry check digit matches.</param>
/// <param name="OptionalData">Whether the personal-number check digit matches (TD3 passports only; an unused field may print <c>&lt;</c> or <c>0</c>).</param>
/// <param name="Composite">Whether the composite check digit matches (absent on MRV visas).</param>
public sealed record MrzCheckResults(
    bool DocumentNumber,
    bool BirthDate,
    bool ExpiryDate,
    bool? OptionalData,
    bool? Composite)
{
    /// <summary>True when every applicable check digit matches.</summary>
    public bool AllPassed => DocumentNumber && BirthDate && ExpiryDate && OptionalData != false && Composite != false;

    /// <summary>Number of applicable check digits that match.</summary>
    internal int PassedCount =>
        (DocumentNumber ? 1 : 0) + (BirthDate ? 1 : 0) + (ExpiryDate ? 1 : 0)
        + (OptionalData == true ? 1 : 0) + (Composite == true ? 1 : 0);
}

/// <summary>
/// The fields decoded from a travel document's machine-readable zone by <see cref="MrzParser"/>. Field
/// values have filler characters (<c>&lt;</c>) removed and OCR corrections applied; the lines as located
/// are kept in <see cref="RawLines"/> and the corrected lines the fields were read from in
/// <see cref="CorrectedLines"/>.
/// </summary>
public sealed record MrzResult
{
    /// <summary>The MRZ layout (TD1, TD2 or TD3).</summary>
    public MrzFormat Format { get; init; }

    /// <summary>The document code, e.g. <c>P</c> (passport), <c>I</c> or <c>ID</c> (identity card), <c>V</c> (visa).</summary>
    public required string DocumentType { get; init; }

    /// <summary>The issuing state or organization (ICAO three-letter code, e.g. <c>UTO</c>; Germany is <c>D</c>).</summary>
    public required string IssuingCountry { get; init; }

    /// <summary>The primary identifier (surname), words separated by spaces.</summary>
    public required string Surname { get; init; }

    /// <summary>The secondary identifier (given names), words separated by spaces; empty when absent.</summary>
    public required string GivenNames { get; init; }

    /// <summary>The document number (including a TD1 long-number extension when present).</summary>
    public required string DocumentNumber { get; init; }

    /// <summary>The holder's nationality (ICAO three-letter code).</summary>
    public required string Nationality { get; init; }

    /// <summary>
    /// The date of birth, or <see langword="null"/> when unreadable. Two-digit years resolve to the most
    /// recent year not in the future.
    /// </summary>
    public DateOnly? BirthDate { get; init; }

    /// <summary>The sex field: <c>M</c>, <c>F</c>, <c>X</c>, or <c>&lt;</c> (unspecified).</summary>
    public required string Sex { get; init; }

    /// <summary>
    /// The date of expiry, or <see langword="null"/> when unreadable. Two-digit years resolve to 2000–2099
    /// unless that is more than 50 years ahead.
    /// </summary>
    public DateOnly? ExpiryDate { get; init; }

    /// <summary>The optional data element (personal number on TD3; line-1 optional data on TD1).</summary>
    public required string OptionalData { get; init; }

    /// <summary>The second optional data element (TD1 line 2 only); <see langword="null"/> for other formats.</summary>
    public string? OptionalData2 { get; init; }

    /// <summary>The per-field check-digit outcomes, computed on <see cref="CorrectedLines"/>.</summary>
    public required MrzCheckResults Checks { get; init; }

    /// <summary>True when every applicable check digit, including the composite, matches.</summary>
    public bool IsValid => Checks.AllPassed;

    /// <summary>
    /// The MRZ lines as located and normalized (spaces removed, filler look-alikes restored, padded or
    /// trimmed to the format's width), before any field correction.
    /// </summary>
    public required IReadOnlyList<string> RawLines { get; init; }

    /// <summary>
    /// The lines the fields were decoded from: <see cref="RawLines"/> after positional corrections (digit
    /// look-alikes in numeric fields, letter look-alikes in alphabetic fields) and any check-digit-guided
    /// repair. Compare with <see cref="RawLines"/> to see exactly what was changed.
    /// </summary>
    public required IReadOnlyList<string> CorrectedLines { get; init; }
}
