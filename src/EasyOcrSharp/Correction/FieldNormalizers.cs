using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyOcrSharp.Correction;

/// <summary>
/// A pure function that recognizes one kind of structured field (a date, an amount, an IBAN, a machine
/// readable zone) and returns it in canonical form, repairing OCR damage when the field's own checksum
/// or grammar makes the repair provable.
/// </summary>
/// <param name="value">The candidate text — a single token, or a whole line for multi-line formats.</param>
/// <returns>
/// <see cref="FieldNormalizationResult.NotApplicable"/> when the text is not of this kind at all (the
/// corrector then tries the next normalizer), otherwise a handled result carrying the canonical value
/// and whether it validated.
/// </returns>
public delegate FieldNormalizationResult FieldNormalizer(string value);

/// <summary>
/// The outcome of running a <see cref="FieldNormalizer"/> over a piece of text.
/// </summary>
public sealed record FieldNormalizationResult
{
    /// <summary>
    /// Whether the normalizer recognized the text as its kind of field. When <see langword="false"/> the
    /// other properties carry no information and the next normalizer should be tried.
    /// </summary>
    public bool Handled { get; init; }

    /// <summary>
    /// Whether the recognized field is valid — its grammar parses and any checksum it carries agrees,
    /// after repair. A handled-but-invalid field is deliberately left alone by the corrector: a value
    /// that fails its own check digit is a value nobody should silently rewrite.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Whether <see cref="Value"/> differs from the input because the normalizer repaired or reformatted
    /// it (as opposed to the input already being canonical).
    /// </summary>
    public bool Repaired { get; init; }

    /// <summary>
    /// The canonical value when <see cref="IsValid"/> is <see langword="true"/>; otherwise the input text
    /// unchanged. Never <see langword="null"/>.
    /// </summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>The shared "this is not my kind of field" result.</summary>
    public static FieldNormalizationResult NotApplicable { get; } = new();

    /// <summary>Creates a result for a field that was already canonical and valid.</summary>
    /// <param name="value">The canonical value.</param>
    public static FieldNormalizationResult Valid(string value)
        => new() { Handled = true, IsValid = true, Value = value };

    /// <summary>Creates a result for a field that was reformatted or repaired into a valid value.</summary>
    /// <param name="value">The canonical value.</param>
    public static FieldNormalizationResult Fixed(string value)
        => new() { Handled = true, IsValid = true, Repaired = true, Value = value };

    /// <summary>
    /// Creates a result for a field of the right shape that could not be validated — a date that is not a
    /// real day, an IBAN whose mod-97 fails, an MRZ with an irreparable check digit.
    /// </summary>
    /// <param name="value">The input text, returned unchanged.</param>
    public static FieldNormalizationResult Invalid(string value)
        => new() { Handled = true, IsValid = false, Value = value };
}

/// <summary>
/// How an ambiguous three-part numeric date is read.
/// </summary>
public enum DateFieldOrder
{
    /// <summary>
    /// Infer per value: a four-digit first component means year-first; otherwise a first component above
    /// 12 means day-first and a second component above 12 means month-first. Genuinely ambiguous values
    /// such as <c>03/04/2026</c> fall back to day-first. The default.
    /// </summary>
    Auto,

    /// <summary>Always day/month/year (the European reading).</summary>
    DayMonthYear,

    /// <summary>Always month/day/year (the US reading).</summary>
    MonthDayYear,

    /// <summary>Always year/month/day (the ISO reading).</summary>
    YearMonthDay,
}

/// <summary>
/// Ready-made <see cref="FieldNormalizer"/> factories for the structured fields OCR pipelines actually
/// extract: dates, monetary amounts, IBANs and passport/ID machine-readable zones. Each one validates
/// what it recognizes and, where the field carries enough redundancy (a mod-97 checksum, an ICAO check
/// digit, a calendar), <b>repairs</b> the classic misreads — <c>O</c> for <c>0</c>, <c>l</c> for
/// <c>1</c>, <c>S</c> for <c>5</c>, <c>B</c> for <c>8</c> — rather than merely rejecting them.
/// </summary>
/// <remarks>
/// <para>
/// Every factory returns a pure, thread-safe delegate you can reuse; hand them to
/// <see cref="CorrectionOptions.Normalizers"/> or call them directly. A normalizer that does not
/// recognize its input returns <see cref="FieldNormalizationResult.NotApplicable"/> and costs one regex
/// match, so listing several is cheap.
/// </para>
/// <para>
/// Nothing here is culture-sensitive: parsing and formatting go through
/// <see cref="CultureInfo.InvariantCulture"/> so the same document produces the same output on every
/// machine.
/// </para>
/// </remarks>
public static partial class FieldNormalizers
{
    /// <summary>
    /// Recognizes a three-part numeric date separated by <c>/</c>, <c>-</c> or <c>.</c> — the separator
    /// must be the same on both sides — fixes letters misread for digits, validates it as a real calendar
    /// date and re-emits it in <paramref name="outputFormat"/>.
    /// </summary>
    /// <param name="order">How to read an ambiguous component order. Defaults to <see cref="DateFieldOrder.Auto"/>.</param>
    /// <param name="outputFormat">
    /// A .NET date format string applied with the invariant culture. Defaults to ISO <c>yyyy-MM-dd</c>.
    /// </param>
    /// <remarks>
    /// To keep it from firing on things that merely look like a date, the token must contain at least
    /// three real digits and its year component must be exactly two or four characters. Two-digit years
    /// pivot at 68: <c>68</c> reads as 2068 and <c>69</c> as 1969.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="outputFormat"/> is <see langword="null"/>.</exception>
    public static FieldNormalizer Date(DateFieldOrder order = DateFieldOrder.Auto, string outputFormat = "yyyy-MM-dd")
    {
        ArgumentNullException.ThrowIfNull(outputFormat);

        return value =>
        {
            if (string.IsNullOrWhiteSpace(value)) return FieldNormalizationResult.NotApplicable;
            var text = value.Trim();

            var match = LooseDateRegex().Match(text);
            if (!match.Success) return FieldNormalizationResult.NotApplicable;

            var first = match.Groups["a"].Value;
            var second = match.Groups["b"].Value;
            var third = match.Groups["c"].Value;

            // A year is written with two or four figures; anything else is not a date we should touch.
            bool yearFirst = first.Length == 4;
            if (!yearFirst && third.Length != 2 && third.Length != 4) return FieldNormalizationResult.NotApplicable;
            if (yearFirst && third.Length > 2) return FieldNormalizationResult.NotApplicable;
            if (CountAsciiDigits(text) < 3) return FieldNormalizationResult.NotApplicable;

            if (!TryParseDigits(first, out int a) ||
                !TryParseDigits(second, out int b) ||
                !TryParseDigits(third, out int c))
            {
                return FieldNormalizationResult.Invalid(text);
            }

            var effective = order == DateFieldOrder.Auto
                ? (yearFirst ? DateFieldOrder.YearMonthDay
                    : a > 12 && b <= 12 ? DateFieldOrder.DayMonthYear
                    : b > 12 && a <= 12 ? DateFieldOrder.MonthDayYear
                    : DateFieldOrder.DayMonthYear)
                : order;

            int year, month, day;
            switch (effective)
            {
                case DateFieldOrder.YearMonthDay:
                    year = a; month = b; day = c;
                    break;
                case DateFieldOrder.MonthDayYear:
                    month = a; day = b; year = c;
                    break;
                default:
                    day = a; month = b; year = c;
                    break;
            }

            if (year < 100) year = year <= 68 ? 2000 + year : 1900 + year;

            var iso = string.Create(CultureInfo.InvariantCulture, $"{year:D4}-{month:D2}-{day:D2}");
            if (!DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return FieldNormalizationResult.Invalid(text);
            }

            var formatted = date.ToString(outputFormat, CultureInfo.InvariantCulture);
            return string.Equals(formatted, text, StringComparison.Ordinal)
                ? FieldNormalizationResult.Valid(formatted)
                : FieldNormalizationResult.Fixed(formatted);
        };
    }

    /// <summary>
    /// Recognizes a monetary amount that carries an explicit currency marker — a symbol
    /// (<c>$</c>, <c>EUR</c>, <c>GBP</c>, …) or a three-letter ISO code, before or after the number —
    /// fixes letters misread for digits, works out which of <c>.</c> and <c>,</c> is the decimal
    /// separator, and re-emits the amount in <paramref name="numberFormat"/> with the marker in its
    /// original position.
    /// </summary>
    /// <param name="numberFormat">A .NET numeric format string applied with the invariant culture. Defaults to <c>0.00</c>.</param>
    /// <param name="keepSymbol">Whether to re-emit the currency marker (default) or return the bare number.</param>
    /// <remarks>
    /// <para>
    /// The currency marker is <b>required</b>: without it this normalizer would rewrite every number in
    /// the document, including page numbers and quantities. Amounts wrapped in parentheses or prefixed
    /// with <c>-</c> are read as negative and re-emitted with a leading minus sign.
    /// </para>
    /// <para>
    /// Separator disambiguation follows the usual rule: when both <c>.</c> and <c>,</c> appear, the
    /// rightmost is the decimal point and the other is a grouping separator; when only one appears more
    /// than once it is a grouping separator; when it appears once with exactly three digits after it, it
    /// is treated as a grouping separator (so <c>1,234</c> and <c>1.234</c> are both one thousand two
    /// hundred and thirty-four), otherwise it is the decimal point.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="numberFormat"/> is <see langword="null"/>.</exception>
    public static FieldNormalizer Currency(string numberFormat = "0.00", bool keepSymbol = true)
    {
        ArgumentNullException.ThrowIfNull(numberFormat);

        return value =>
        {
            if (string.IsNullOrWhiteSpace(value)) return FieldNormalizationResult.NotApplicable;
            var text = value.Trim();

            var match = LooseAmountRegex().Match(text);
            if (!match.Success) return FieldNormalizationResult.NotApplicable;

            var prefix = match.Groups["pre"].Value;
            var suffix = match.Groups["post"].Value;
            if (prefix.Length == 0 && suffix.Length == 0) return FieldNormalizationResult.NotApplicable;

            var number = match.Groups["num"].Value;
            if (CountAsciiDigits(number) == 0) return FieldNormalizationResult.NotApplicable;

            bool negative = match.Groups["neg"].Success || match.Groups["paren"].Success;
            if (!TryParseAmount(number, out decimal amount)) return FieldNormalizationResult.Invalid(text);
            if (negative) amount = -amount;

            var formatted = amount.ToString(numberFormat, CultureInfo.InvariantCulture);
            if (keepSymbol)
            {
                formatted = prefix.Length > 0
                    ? (amount < 0 ? "-" + prefix + formatted.TrimStart('-') : prefix + formatted)
                    : formatted + " " + suffix;
            }

            return string.Equals(formatted, text, StringComparison.Ordinal)
                ? FieldNormalizationResult.Valid(formatted)
                : FieldNormalizationResult.Fixed(formatted);
        };
    }

    /// <summary>
    /// Recognizes an IBAN, validates it with the ISO 13616 mod-97 checksum and, when the checksum fails,
    /// tries every single-character OCR confusion (<c>0</c>/<c>O</c>, <c>1</c>/<c>I</c>/<c>L</c>,
    /// <c>5</c>/<c>S</c>, <c>8</c>/<c>B</c>, <c>2</c>/<c>Z</c>, <c>6</c>/<c>G</c>, <c>9</c>/<c>Q</c>,
    /// <c>U</c>/<c>V</c>) until one both fits the IBAN grammar and satisfies mod-97. Because mod-97
    /// catches every single-character error, a repair that validates is a repair you can trust.
    /// </summary>
    /// <param name="grouped">
    /// Whether to emit the human-readable form in space-separated groups of four. Defaults to
    /// <see langword="false"/>, the compact machine form.
    /// </param>
    /// <remarks>
    /// Only the checksum is verified, not the per-country BBAN layout, so a structurally odd but
    /// arithmetically valid IBAN is accepted. Input may contain spaces and lower-case letters; both are
    /// normalized away before validation.
    /// </remarks>
    public static FieldNormalizer Iban(bool grouped = false)
    {
        return value =>
        {
            if (string.IsNullOrWhiteSpace(value)) return FieldNormalizationResult.NotApplicable;
            var text = value.Trim();

            var compact = CompactIban(text);
            if (compact.Length < 15 || compact.Length > 34) return FieldNormalizationResult.NotApplicable;
            foreach (char c in compact)
            {
                if (!char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c)) return FieldNormalizationResult.NotApplicable;
            }

            // Country code then two check digits: demand that shape up front (allowing for the usual
            // misreads) so ordinary long words are never claimed as a broken IBAN.
            if (!IsLetterish(compact[0]) || !IsLetterish(compact[1]) ||
                !IsDigitish(compact[2]) || !IsDigitish(compact[3]))
            {
                return FieldNormalizationResult.NotApplicable;
            }

            if (HasIbanShape(compact) && Mod97(compact) == 1)
            {
                var canonical = grouped ? GroupInFours(compact) : compact;
                return string.Equals(canonical, text, StringComparison.Ordinal)
                    ? FieldNormalizationResult.Valid(canonical)
                    : FieldNormalizationResult.Fixed(canonical);
            }

            var repaired = TryRepairIban(compact);
            if (repaired is null) return FieldNormalizationResult.Invalid(text);
            return FieldNormalizationResult.Fixed(grouped ? GroupInFours(repaired) : repaired);
        };
    }

    /// <summary>
    /// Recognizes an ICAO 9303 machine-readable zone — TD1 (three lines of 30), TD2 (two lines of 36) or
    /// TD3 / passport (two lines of 44), and also a lone data line of any of those widths — verifies every
    /// check digit it carries, and repairs what the check digits allow: spaces read for the filler
    /// <c>&lt;</c>, letters read for digits inside the all-numeric date fields, and any single-character
    /// confusion inside a field whose printed check digit then agrees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A TD3 or TD2 lower line is fully self-checking — even its composite check digit covers only fields
    /// on that line — so a single such line can be validated on its own, which is what an OCR result
    /// usually offers. For TD1 a lone 30-column line is checked against the two date check digits it
    /// carries.
    /// </para>
    /// <para>
    /// The composite check digit is verified but never repaired: it spans the whole document number,
    /// dates and optional data, so a mismatch there means the damage is not localizable and the value is
    /// reported invalid and left untouched — including any field-level repairs already found. Correcting
    /// half an identity document is worse than correcting none of it.
    /// </para>
    /// </remarks>
    public static FieldNormalizer Mrz()
    {
        return value =>
        {
            if (string.IsNullOrWhiteSpace(value)) return FieldNormalizationResult.NotApplicable;
            var text = value.Trim();

            var lines = SplitMrzLines(text);
            if (lines is null) return FieldNormalizationResult.NotApplicable;

            var spec = SelectMrzSpec(lines);
            if (spec is null) return FieldNormalizationResult.NotApplicable;
            if (!HasPlausibleCheckPositions(lines, spec)) return FieldNormalizationResult.NotApplicable;

            bool changed = false;
            foreach (var field in spec.Fields)
            {
                if (!TryFixMrzField(lines, field, ref changed)) return FieldNormalizationResult.Invalid(text);
            }

            if (spec.CompositeCheckIndex >= 0 && !VerifyMrzComposite(lines, spec, ref changed))
            {
                return FieldNormalizationResult.Invalid(text);
            }

            var rebuilt = string.Join('\n', Array.ConvertAll(lines, static l => new string(l)));
            return changed || !string.Equals(rebuilt, text, StringComparison.Ordinal)
                ? FieldNormalizationResult.Fixed(rebuilt)
                : FieldNormalizationResult.Valid(rebuilt);
        };
    }

    /// <summary>
    /// Whether <paramref name="iban"/> passes the ISO 13616 mod-97 checksum. Spaces and casing are
    /// ignored; the value must be 15 to 34 alphanumeric characters starting with two letters and two
    /// digits.
    /// </summary>
    /// <param name="iban">The candidate IBAN.</param>
    /// <exception cref="ArgumentNullException"><paramref name="iban"/> is <see langword="null"/>.</exception>
    public static bool IsValidIban(string iban)
    {
        ArgumentNullException.ThrowIfNull(iban);
        var compact = CompactIban(iban);
        if (compact.Length < 15 || compact.Length > 34) return false;
        if (!HasIbanShape(compact)) return false;
        foreach (char c in compact)
        {
            if (!char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c)) return false;
        }
        return Mod97(compact) == 1;
    }

    /// <summary>
    /// Computes the ICAO 9303 check digit of an MRZ field: each character is weighted 7, 3, 1 in turn
    /// (digits by value, <c>A</c>–<c>Z</c> as 10–35, the filler <c>&lt;</c> as 0) and the weighted sum is
    /// taken modulo 10.
    /// </summary>
    /// <param name="field">The field the check digit is computed over.</param>
    /// <returns>The check digit 0–9, or -1 if the field contains a character outside the MRZ alphabet.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public static int MrzCheckDigit(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return ComputeMrzCheckDigit(field.AsSpan());
    }

    // ---- regexes ----
    // The character classes below are "digits, plus the letters recognizers habitually emit in their
    // place"; ToDigit is what turns them back into digits once a match is found.

    [GeneratedRegex(
        @"^(?<a>[0-9OoQIilLZzSsGgTtBb]{1,4})(?<sep>[./\-])(?<b>[0-9OoQIilLZzSsGgTtBb]{1,2})\k<sep>(?<c>[0-9OoQIilLZzSsGgTtBb]{1,4})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex LooseDateRegex();

    [GeneratedRegex(
        @"^(?<neg>-)?(?<paren>\()?\s*(?<pre>[$€£¥₹₽¢]|[A-Z]{3})?\s*" +
        @"(?<num>[0-9OoQIilLZzSsGgTtBb][0-9OoQIilLZzSsGgTtBb.,'’   ]*)\s*" +
        @"(?<post>[$€£¥₹₽¢]|[A-Z]{3})?\s*\)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex LooseAmountRegex();

    // ---- shared character helpers ----

    /// <summary>Maps a letter recognizers commonly emit in place of a digit onto that digit.</summary>
    private static char ToDigit(char c) => c switch
    {
        'O' or 'o' or 'Q' or 'D' => '0',
        'I' or 'i' or 'l' or '|' => '1',
        'Z' or 'z' => '2',
        'S' or 's' => '5',
        'G' => '6',
        'T' or 't' => '7',
        'B' => '8',
        'g' or 'q' => '9',
        'b' => '6',
        _ => c,
    };

    /// <summary>
    /// The upper-case characters a given upper-case character is plausibly confused with, for the
    /// checksum-guided repair of IBANs and MRZs.
    /// </summary>
    private static string ConfusionAlternatives(char c) => c switch
    {
        '0' => "O",
        'O' => "0",
        '1' => "IL",
        'I' => "1",
        'L' => "1",
        '2' => "Z",
        'Z' => "2",
        '5' => "S",
        'S' => "5",
        '6' => "G",
        'G' => "6",
        '8' => "B",
        'B' => "8",
        '9' => "Q",
        'Q' => "9",
        'U' => "V",
        'V' => "U",
        _ => "",
    };

    /// <summary>Whether the character is an upper-case letter, or a digit routinely misread for one.</summary>
    private static bool IsLetterish(char c)
    {
        if (char.IsAsciiLetterUpper(c)) return true;
        foreach (char alternative in ConfusionAlternatives(c))
        {
            if (char.IsAsciiLetterUpper(alternative)) return true;
        }
        return false;
    }

    /// <summary>Whether the character is a digit, or a letter routinely misread for one.</summary>
    private static bool IsDigitish(char c) => char.IsAsciiDigit(ToDigit(c));

    private static int CountAsciiDigits(string text)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (char.IsAsciiDigit(c)) count++;
        }
        return count;
    }

    /// <summary>Coerces digit-like letters and parses the result as a non-negative integer.</summary>
    private static bool TryParseDigits(string text, out int value)
    {
        value = 0;
        if (text.Length == 0 || text.Length > 9) return false;
        foreach (char c in text)
        {
            char digit = ToDigit(c);
            if (!char.IsAsciiDigit(digit)) return false;
            value = (value * 10) + (digit - '0');
        }
        return true;
    }

    // ---- currency ----

    private static bool TryParseAmount(string number, out decimal value)
    {
        value = 0m;
        var sb = new StringBuilder(number.Length);
        foreach (char c in number)
        {
            if (c is ' ' or ' ' or ' ' or '\'' or '’') continue;
            sb.Append(c is '.' or ',' ? c : ToDigit(c));
        }

        var cleaned = sb.ToString();
        int lastDot = cleaned.LastIndexOf('.');
        int lastComma = cleaned.LastIndexOf(',');

        char decimalSeparator;
        if (lastDot >= 0 && lastComma >= 0)
        {
            decimalSeparator = lastDot > lastComma ? '.' : ',';
        }
        else if (lastDot >= 0 || lastComma >= 0)
        {
            char only = lastDot >= 0 ? '.' : ',';
            int position = lastDot >= 0 ? lastDot : lastComma;
            int occurrences = 0;
            foreach (char c in cleaned)
            {
                if (c == only) occurrences++;
            }
            int trailing = cleaned.Length - position - 1;
            decimalSeparator = occurrences == 1 && trailing != 3 ? only : '\0';
        }
        else
        {
            decimalSeparator = '\0';
        }

        var digits = new StringBuilder(cleaned.Length);
        foreach (char c in cleaned)
        {
            if (c == decimalSeparator) digits.Append('.');
            else if (c is '.' or ',') continue;
            else if (char.IsAsciiDigit(c)) digits.Append(c);
            else return false;
        }

        return decimal.TryParse(digits.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    // ---- IBAN ----

    private static string CompactIban(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c)) continue;
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    private static bool HasIbanShape(string compact)
        => compact.Length >= 15
           && char.IsAsciiLetterUpper(compact[0])
           && char.IsAsciiLetterUpper(compact[1])
           && char.IsAsciiDigit(compact[2])
           && char.IsAsciiDigit(compact[3]);

    /// <summary>ISO 7064 mod-97-10 over the IBAN rotated four characters to the left.</summary>
    private static int Mod97(string compact)
    {
        int remainder = 0;
        for (int k = 0; k < compact.Length; k++)
        {
            char c = compact[(k + 4) % compact.Length];
            if (char.IsAsciiDigit(c))
            {
                remainder = ((remainder * 10) + (c - '0')) % 97;
            }
            else if (char.IsAsciiLetterUpper(c))
            {
                remainder = ((remainder * 100) + (c - 'A' + 10)) % 97;
            }
            else
            {
                return -1;
            }
        }
        return remainder;
    }

    /// <summary>
    /// Walks every position trying each OCR-confusable alternative, returning the first candidate that has
    /// IBAN shape and satisfies mod-97. Single-character errors are exactly what mod-97 is guaranteed to
    /// detect, so a candidate that validates is the repair, not a repair.
    /// </summary>
    private static string? TryRepairIban(string compact)
    {
        var buffer = compact.ToCharArray();
        for (int i = 0; i < buffer.Length; i++)
        {
            char original = buffer[i];
            foreach (char alternative in ConfusionAlternatives(original))
            {
                buffer[i] = alternative;
                var candidate = new string(buffer);
                if (HasIbanShape(candidate) && Mod97(candidate) == 1) return candidate;
            }
            buffer[i] = original;
        }
        return null;
    }

    private static string GroupInFours(string compact)
    {
        var sb = new StringBuilder(compact.Length + (compact.Length / 4));
        for (int i = 0; i < compact.Length; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append(' ');
            sb.Append(compact[i]);
        }
        return sb.ToString();
    }

    // ---- MRZ ----

    /// <summary>One check-digit-protected field: where the data is and where its check digit is printed.</summary>
    private readonly record struct MrzField(int Line, int Start, int Length, int CheckLine, int CheckIndex, bool NumericOnly);

    /// <summary>One stretch of characters feeding a composite check digit.</summary>
    private readonly record struct MrzSegment(int Line, int Start, int Length);

    private sealed record MrzSpec(
        MrzField[] Fields,
        MrzSegment[] Composite,
        int CompositeCheckLine,
        int CompositeCheckIndex);

    // TD3 (passport): the lower line carries every check digit, composite included.
    private static MrzSpec BuildTd3Spec(int line) => new(
        [
            new MrzField(line, 0, 9, line, 9, false),
            new MrzField(line, 13, 6, line, 19, true),
            new MrzField(line, 21, 6, line, 27, true),
            new MrzField(line, 28, 14, line, 42, false),
        ],
        [new MrzSegment(line, 0, 10), new MrzSegment(line, 13, 7), new MrzSegment(line, 21, 22)],
        line, 43);

    // TD2: the same layout on a 36-column line, without the personal-number check digit.
    private static MrzSpec BuildTd2Spec(int line) => new(
        [
            new MrzField(line, 0, 9, line, 9, false),
            new MrzField(line, 13, 6, line, 19, true),
            new MrzField(line, 21, 6, line, 27, true),
        ],
        [new MrzSegment(line, 0, 10), new MrzSegment(line, 13, 7), new MrzSegment(line, 21, 14)],
        line, 35);

    private static readonly MrzSpec Td3TwoLine = BuildTd3Spec(1);
    private static readonly MrzSpec Td3SingleLine = BuildTd3Spec(0);
    private static readonly MrzSpec Td2TwoLine = BuildTd2Spec(1);
    private static readonly MrzSpec Td2SingleLine = BuildTd2Spec(0);

    // TD1: document number on the upper line, dates on the middle line, composite across both.
    private static readonly MrzSpec Td1Full = new(
        [
            new MrzField(0, 5, 9, 0, 14, false),
            new MrzField(1, 0, 6, 1, 6, true),
            new MrzField(1, 8, 6, 1, 14, true),
        ],
        [new MrzSegment(0, 5, 25), new MrzSegment(1, 0, 7), new MrzSegment(1, 8, 7), new MrzSegment(1, 18, 11)],
        1, 29);

    // A lone 30-column line can only be checked against the two date check digits it carries.
    private static readonly MrzSpec Td1MiddleLine = new(
        [
            new MrzField(0, 0, 6, 0, 6, true),
            new MrzField(0, 8, 6, 0, 14, true),
        ],
        [], -1, -1);

    /// <summary>
    /// Splits into MRZ lines: upper-cased, spaces folded onto the filler character, blank lines dropped.
    /// Returns <see langword="null"/> when a character outside the MRZ alphabet appears.
    /// </summary>
    private static char[][]? SplitMrzLines(string text)
    {
        var lines = new List<char[]>(3);
        foreach (var raw in text.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            var buffer = new char[trimmed.Length];
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = char.ToUpperInvariant(trimmed[i]);
                if (c is ' ' or '«' or '‹') c = '<';
                if (c != '<' && !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c)) return null;
                buffer[i] = c;
            }
            lines.Add(buffer);
        }
        return lines.Count == 0 ? null : [.. lines];
    }

    private static MrzSpec? SelectMrzSpec(char[][] lines) => lines switch
    {
        [{ Length: 44 }, { Length: 44 }] => Td3TwoLine,
        [{ Length: 36 }, { Length: 36 }] => Td2TwoLine,
        [{ Length: 30 }, { Length: 30 }, { Length: 30 }] => Td1Full,
        [{ Length: 44 }] => Td3SingleLine,
        [{ Length: 36 }] => Td2SingleLine,
        [{ Length: 30 }] => Td1MiddleLine,
        _ => null,
    };

    /// <summary>
    /// Cheap plausibility gate: every position that should hold a check digit must already hold a digit,
    /// a letter routinely misread for one, or the filler. Without it, any 44-character line of prose
    /// would be claimed as an MRZ.
    /// </summary>
    private static bool HasPlausibleCheckPositions(char[][] lines, MrzSpec spec)
    {
        foreach (var field in spec.Fields)
        {
            if (!IsCheckDigitish(lines[field.CheckLine][field.CheckIndex])) return false;
        }
        return spec.CompositeCheckIndex < 0
               || IsCheckDigitish(lines[spec.CompositeCheckLine][spec.CompositeCheckIndex]);
    }

    private static bool IsCheckDigitish(char c) => c == '<' || IsDigitish(c);

    private static bool TryFixMrzField(char[][] lines, MrzField field, ref bool changed)
    {
        var line = lines[field.Line];
        var checkLine = lines[field.CheckLine];

        if (field.NumericOnly)
        {
            for (int i = field.Start; i < field.Start + field.Length; i++)
            {
                char digit = ToDigit(line[i]);
                if (digit != line[i] && char.IsAsciiDigit(digit))
                {
                    line[i] = digit;
                    changed = true;
                }
            }
        }

        char printed = checkLine[field.CheckIndex];
        if (printed != '<')
        {
            char digit = ToDigit(printed);
            if (digit != printed && char.IsAsciiDigit(digit))
            {
                checkLine[field.CheckIndex] = digit;
                printed = digit;
                changed = true;
            }
        }

        bool allFiller = true;
        for (int i = field.Start; i < field.Start + field.Length; i++)
        {
            if (line[i] != '<') { allFiller = false; break; }
        }

        // An unused optional field is filled with '<' and its check digit may be printed as '<' or '0'.
        if (allFiller) return printed is '<' or '0';
        if (!char.IsAsciiDigit(printed)) return false;

        int expected = printed - '0';
        if (ComputeMrzCheckDigit(line.AsSpan(field.Start, field.Length)) == expected) return true;

        for (int i = field.Start; i < field.Start + field.Length; i++)
        {
            char original = line[i];
            foreach (char alternative in ConfusionAlternatives(original))
            {
                if (field.NumericOnly && !char.IsAsciiDigit(alternative)) continue;
                line[i] = alternative;
                if (ComputeMrzCheckDigit(line.AsSpan(field.Start, field.Length)) == expected)
                {
                    changed = true;
                    return true;
                }
            }
            line[i] = original;
        }
        return false;
    }

    private static bool VerifyMrzComposite(char[][] lines, MrzSpec spec, ref bool changed)
    {
        var checkLine = lines[spec.CompositeCheckLine];
        char printed = checkLine[spec.CompositeCheckIndex];
        char digit = ToDigit(printed);
        if (digit != printed && char.IsAsciiDigit(digit))
        {
            checkLine[spec.CompositeCheckIndex] = digit;
            printed = digit;
            changed = true;
        }
        if (!char.IsAsciiDigit(printed)) return false;

        var sb = new StringBuilder();
        foreach (var segment in spec.Composite)
        {
            sb.Append(lines[segment.Line], segment.Start, segment.Length);
        }
        return ComputeMrzCheckDigit(sb.ToString().AsSpan()) == printed - '0';
    }

    private static int ComputeMrzCheckDigit(ReadOnlySpan<char> field)
    {
        ReadOnlySpan<int> weights = [7, 3, 1];
        int sum = 0;
        for (int i = 0; i < field.Length; i++)
        {
            char c = field[i];
            int value;
            if (char.IsAsciiDigit(c)) value = c - '0';
            else if (char.IsAsciiLetterUpper(c)) value = c - 'A' + 10;
            else if (c == '<') value = 0;
            else return -1;
            sum += value * weights[i % 3];
        }
        return sum % 10;
    }
}
