using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace EasyOcrSharp.Redaction;

/// <summary>
/// Ready-made <see cref="RedactionRule"/>s for the identifiers people actually need to strike out of a
/// scanned document, plus the check-digit algorithms behind them.
/// </summary>
/// <remarks>
/// <para>
/// Every preset pairs a regular expression with the validation that regular expressions cannot do. A
/// pattern of sixteen digits matches a purchase-order number as readily as a credit card; only the Luhn
/// residue tells them apart, and only mod-97 separates an IBAN from a warehouse code. Presets that need
/// no arithmetic (email, SSN) carry no validator and are pure pattern matches.
/// </para>
/// <para>
/// Every pattern is source-generated (<see cref="GeneratedRegexAttribute"/>) so it is compiled at build
/// time and safe under trimming/AOT, and every one carries a one-second match timeout so a hostile line
/// of OCR text cannot stall a batch.
/// </para>
/// <para>
/// These operate on <i>recognized</i> text, so they inherit the recognizer's mistakes: an OCR pass that
/// reads <c>5</c> as <c>S</c> breaks the Luhn check and the number survives. Treat automated redaction
/// as a very good first pass, not as a guarantee, and keep a human in the loop when the stakes are high.
/// </para>
/// </remarks>
public static partial class RedactionPatterns
{
    // ---- patterns -------------------------------------------------------------------------------

    [GeneratedRegex(
        @"(?<![A-Za-z0-9._%+\-])[A-Za-z0-9._%+\-]+@[A-Za-z0-9][A-Za-z0-9.\-]*\.[A-Za-z]{2,24}(?![A-Za-z])",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex EmailPattern();

    // A digit run of 7-20 characters that may carry +, spaces, dots, dashes and parentheses. Deliberately
    // permissive — the digit-count validator, not the pattern, decides what is plausibly a phone number.
    [GeneratedRegex(
        @"(?<![+\d])\+?\d[\d .\-()]{5,20}\d(?!\d)",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PhonePattern();

    // 13-19 digits with optional single space/dash separators; Luhn decides whether it is a card.
    [GeneratedRegex(
        @"(?<![\d\-])(?:\d[ \-]?){12,18}\d(?!\d)",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CreditCardPattern();

    // ISO 13616: two country letters, two check digits, then 11-30 alphanumerics, optionally spaced in
    // groups. mod-97 decides whether it is an IBAN.
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])[A-Z]{2}[0-9]{2}(?:[ ]?[A-Za-z0-9]){11,30}(?![A-Za-z0-9])",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IbanPattern();

    // SSA issuance rules: area 001-899 excluding 666, group 01-99, serial 0001-9999.
    [GeneratedRegex(
        @"(?<!\d)(?!000|666|9\d\d)\d{3}[ \-]?(?!00)\d{2}[ \-]?(?!0000)\d{4}(?!\d)",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SsnPattern();

    [GeneratedRegex(@"(?<!\d)\d{6,}(?!\d)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LongDigitRunPattern();

    // ---- rules ----------------------------------------------------------------------------------

    /// <summary>
    /// Email addresses (<c>name@example.co.uk</c>). Pattern-only: there is no cheap check that
    /// distinguishes a real mailbox from a well-formed one, and for redaction that is the right trade.
    /// </summary>
    public static RedactionRule Email { get; } = new()
    {
        Name = nameof(Email),
        Pattern = EmailPattern(),
    };

    /// <summary>
    /// Loose international phone numbers — an optional <c>+</c>, then digits interleaved with spaces,
    /// dots, dashes or parentheses — accepted only when the digits number 7 to 15 (E.164's maximum).
    /// Deliberately loose, and therefore the preset most prone to false positives: long dates and
    /// reference numbers can look like phone numbers to any pattern matcher. Pair it with
    /// <see cref="RedactionScope.MatchedWords"/> if collateral redaction is a concern.
    /// </summary>
    public static RedactionRule Phone { get; } = new()
    {
        Name = nameof(Phone),
        Pattern = PhonePattern(),
        Validator = static candidate =>
        {
            int digits = CountDigits(candidate);
            return digits is >= 7 and <= 15;
        },
    };

    /// <summary>
    /// Payment card numbers: 13-19 digits, optionally grouped with spaces or dashes, that satisfy the
    /// Luhn check digit. The Luhn test rejects roughly nine of every ten random digit strings, which is
    /// what makes this preset usable on invoices full of order and reference numbers.
    /// </summary>
    public static RedactionRule CreditCard { get; } = new()
    {
        Name = nameof(CreditCard),
        Pattern = CreditCardPattern(),
        Validator = static candidate =>
        {
            int digits = CountDigits(candidate);
            return digits is >= 13 and <= 19 && IsValidLuhn(candidate);
        },
    };

    /// <summary>
    /// IBANs (ISO 13616): country code, two check digits, then the national account part, validated by
    /// the mod-97 residue. Spaces inside the number are tolerated, as banks print them in groups of four.
    /// </summary>
    public static RedactionRule Iban { get; } = new()
    {
        Name = nameof(Iban),
        Pattern = IbanPattern(),
        Validator = IsValidIban,
    };

    /// <summary>
    /// US Social Security numbers in <c>123-45-6789</c>, <c>123 45 6789</c> or <c>123456789</c> form.
    /// The pattern already encodes the SSA's issuance rules — area 000, 666 and 900-999 are never
    /// issued, nor is a zero group or serial — so no separate validator is needed.
    /// </summary>
    public static RedactionRule UsSocialSecurityNumber { get; } = new()
    {
        Name = nameof(UsSocialSecurityNumber),
        Pattern = SsnPattern(),
    };

    /// <summary>
    /// Any unbroken run of six or more digits — the blunt instrument for account, policy, patient and
    /// case numbers that follow no standard. Expect it to catch years-in-a-row, totals and invoice
    /// numbers too; reach for it when over-redacting is cheaper than leaking.
    /// </summary>
    public static RedactionRule LongDigitRun { get; } = new()
    {
        Name = nameof(LongDigitRun),
        Pattern = LongDigitRunPattern(),
    };

    /// <summary>
    /// Every preset except <see cref="LongDigitRun"/>, which is excluded because its false-positive rate
    /// makes it a deliberate choice rather than a default. Suitable as a general "strip the obvious PII"
    /// starting point.
    /// </summary>
    public static IReadOnlyList<RedactionRule> Common { get; } =
        new ReadOnlyCollection<RedactionRule>(new[] { Email, Phone, CreditCard, Iban, UsSocialSecurityNumber });

    /// <summary>Every preset defined here, including <see cref="LongDigitRun"/>.</summary>
    public static IReadOnlyList<RedactionRule> All { get; } =
        new ReadOnlyCollection<RedactionRule>(new[] { Email, Phone, CreditCard, Iban, UsSocialSecurityNumber, LongDigitRun });

    // ---- check digits ---------------------------------------------------------------------------

    /// <summary>
    /// Applies the Luhn (mod-10) check digit algorithm, ignoring spaces and dashes. Returns false for
    /// anything containing a non-digit character, or fewer than two digits.
    /// </summary>
    /// <param name="value">The candidate number, e.g. <c>"4111 1111 1111 1111"</c>.</param>
    public static bool IsValidLuhn(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        int sum = 0;
        int count = 0;
        // Walk right to left: every second digit is doubled, and a double above 9 has 9 subtracted.
        for (int i = value.Length - 1; i >= 0; i--)
        {
            char c = value[i];
            if (c is ' ' or '-') continue;
            if (!char.IsAsciiDigit(c)) return false;

            int digit = c - '0';
            if ((count & 1) == 1)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
            count++;
        }

        return count >= 2 && sum % 10 == 0;
    }

    /// <summary>
    /// Validates an IBAN per ISO 13616: 15-34 characters after spaces are removed, a two-letter country
    /// code, two check digits, and a mod-97 residue of 1 once the first four characters are rotated to
    /// the end and letters are expanded to numbers (A=10 … Z=35).
    /// </summary>
    /// <param name="value">The candidate IBAN, with or without the conventional spacing.</param>
    public static bool IsValidIban(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        Span<char> compact = value.Length <= 64 ? stackalloc char[value.Length] : new char[value.Length];
        int n = 0;
        foreach (char c in value)
        {
            if (c is ' ' or '-') continue;
            if (!char.IsAsciiLetterOrDigit(c)) return false;
            compact[n++] = char.ToUpperInvariant(c);
        }

        if (n is < 15 or > 34) return false;
        if (!char.IsAsciiLetter(compact[0]) || !char.IsAsciiLetter(compact[1])) return false;
        if (!char.IsAsciiDigit(compact[2]) || !char.IsAsciiDigit(compact[3])) return false;

        // Rotate the country code + check digits to the end, expand letters, and take mod 97 in a
        // running fashion so no big-integer arithmetic is needed.
        int remainder = 0;
        for (int i = 0; i < n; i++)
        {
            char c = compact[(i + 4) % n];
            if (char.IsAsciiDigit(c))
            {
                remainder = (remainder * 10 + (c - '0')) % 97;
            }
            else
            {
                int expanded = c - 'A' + 10;          // two decimal digits
                remainder = (remainder * 100 + expanded) % 97;
            }
        }

        return remainder == 1;
    }

    private static int CountDigits(string value)
    {
        int digits = 0;
        foreach (char c in value)
        {
            if (char.IsAsciiDigit(c)) digits++;
        }
        return digits;
    }
}
