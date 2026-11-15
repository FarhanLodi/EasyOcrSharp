using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EasyOcrSharp.Correction;
using EasyOcrSharp.Redaction;

namespace EasyOcrSharp.Extraction;

/// <summary>
/// Ready-made <see cref="OcrPattern"/>s for values commonly pulled out of documents: e-mail, URL, phone,
/// date, amount, IBAN, payment card and percentage.
/// </summary>
/// <remarks>
/// <para>
/// Every pattern is matched per OCR line (values split across lines are not joined) and is deliberately
/// conservative: structured values are validated — IBAN mod-97 (<see cref="FieldNormalizers.IsValidIban"/>),
/// card Luhn (<see cref="RedactionPatterns.IsValidLuhn"/>), real calendar dates, consistent digit grouping —
/// so noise is dropped rather than reported.
/// </para>
/// <para>
/// OCR look-alikes (<c>O</c>→<c>0</c>, <c>I</c>/<c>l</c>→<c>1</c>) are tolerated only inside the checksum
/// validators, only in positions that must be digits, and never change <see cref="OcrMatch.Value"/>; the
/// repaired form is reported in <see cref="OcrMatch.Normalized"/>. A checksum makes such a repair
/// provable, which is why the unchecked patterns (dates, amounts, phones) do not attempt one.
/// </para>
/// <para>
/// Every expression is source-generated (<see cref="GeneratedRegexAttribute"/>), so it is trim/AOT-safe,
/// and carries a one-second match timeout so a hostile line of OCR text cannot stall a batch.
/// </para>
/// </remarks>
public static partial class OcrPatterns
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;
    private const int TimeoutMs = 1000;

    private const string EmailPattern =
        @"(?<![\w.%+-])[A-Za-z0-9_%+-](?:[A-Za-z0-9._%+-]*[A-Za-z0-9_%+-])?@(?:[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?\.)+[A-Za-z]{2,24}(?![\w-])";

    private const string UrlPattern =
        @"(?<![\w@/.])(?:https?://|www\.)[^\s<>""'`]*[^\s<>""'`.,;:!?)\]}]";

    private const string PhonePattern =
        @"(?<![\w+])(?:\+\d{1,3}[ .-]?(?:\(\d{1,4}\)[ .-]?)?\d{1,14}(?:[ .-]\d{1,5}){0,5}" +
        @"|\(\d{1,4}\)[ .-]?\d{1,10}(?:[ .-]\d{1,5}){0,5}" +
        @"|\d{1,5}(?:[ .-]\d{1,5}){1,5})(?!\w|[.-]\d)";

    private const string MonthNames =
        "(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)";

    private const string DatePattern =
        @"(?<![\w./-])(?:" +
        @"(?<iy>\d{4})(?<isep>[-/.])(?<im>\d{1,2})\k<isep>(?<id>\d{1,2})" +
        @"|(?<na>\d{1,2})(?<nsep>[-/.])(?<nb>\d{1,2})\k<nsep>(?<ny>\d{4}|\d{2})" +
        @"|(?<td>\d{1,2})(?:st|nd|rd|th)?[ -]+(?<tm>" + MonthNames + @")\.?,?[ -]+(?<ty>\d{4})" +
        @"|(?<mm>" + MonthNames + @")\.?[ ]+(?<md>\d{1,2})(?:st|nd|rd|th)?,?[ ]+(?<my>\d{4})" +
        @")(?![\w/-]|\.\d)";

    private const string CurrencyCodes =
        "USD|EUR|GBP|JPY|CNY|RMB|INR|PKR|LKR|BDT|CHF|CAD|AUD|NZD|SEK|NOK|DKK|ISK|PLN|CZK|HUF|RON|BGN|RUB|UAH|TRY|" +
        "BRL|MXN|ARS|CLP|COP|PEN|ZAR|NGN|KES|EGP|MAD|SGD|HKD|TWD|KRW|THB|VND|IDR|MYR|PHP|AED|SAR|QAR|KWD|BHD|OMR|ILS";

    private const string Currency =
        @"(?:US\$|R\$|[$€£¥₹₽₩₺₪฿₫₴₦]|(?<![A-Za-z])(?:" + CurrencyCodes + @")(?![A-Za-z]))";

    // Thousands separators: comma, dot, apostrophe, no-break space, narrow no-break space. A plain space is
    // deliberately not a grouping character, so "Qty 2 100.00 EUR" is not read as two thousand one hundred.
    private const string Number =
        @"(?:\d{1,3}(?:[,.'  ]\d{3})+(?:[.,]\d{1,2})?|\d+(?:[.,]\d{1,2})?)";

    private const string AmountPattern =
        @"(?<sign>[-−])?(?<cur>" + Currency + @")[  ]?(?<sign2>[-−])?(?<num>" + Number + @")(?!\d|[.,]\d)" +
        @"|(?<sign>[-−])?(?<![\d.,])(?<num>" + Number + @")[  ]?(?<cur>" + Currency + @")";

    private const string IbanPattern =
        @"(?<![A-Za-z0-9])[A-Z]{2}[0-9OIl]{2}(?:[ ]?[A-Z0-9]{4}){2,7}(?:[ ]?[A-Z0-9]{1,3})?(?![A-Za-z0-9])";

    private const string CardPattern =
        @"(?<![\w-])[0-9OIl]{4}(?:[ -]?[0-9OIl]{2,6}){2,4}(?!\w)";

    private const string PercentPattern =
        @"(?<![\w.,])[-+−]?\d+(?:[.,]\d+)?[  ]?%";

    [GeneratedRegex(EmailPattern, Options, TimeoutMs)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(UrlPattern, Options | RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(PhonePattern, Options, TimeoutMs)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(DatePattern, Options | RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex DateRegex();

    [GeneratedRegex(AmountPattern, Options, TimeoutMs)]
    private static partial Regex AmountRegex();

    [GeneratedRegex(IbanPattern, Options, TimeoutMs)]
    private static partial Regex IbanRegex();

    [GeneratedRegex(CardPattern, Options, TimeoutMs)]
    private static partial Regex CardRegex();

    [GeneratedRegex(PercentPattern, Options, TimeoutMs)]
    private static partial Regex PercentRegex();

    /// <summary>
    /// E-mail addresses. <see cref="OcrMatch.Normalized"/> lower-cases the domain part.
    /// </summary>
    public static OcrPattern Email { get; } = new(EmailRegex(), OcrMatchKind.Email, ResolveEmail);

    /// <summary>
    /// Web URLs starting with <c>http://</c>, <c>https://</c> or <c>www.</c>; trailing sentence punctuation
    /// is excluded. No normalized form.
    /// </summary>
    public static OcrPattern Url { get; } = new(UrlRegex(), OcrMatchKind.Url, static m => new MatchResolution(m.Length, null));

    /// <summary>
    /// Telephone numbers, conservatively: either an international number starting with <c>+</c> (7–15
    /// digits) or a national number with separators or a parenthesized area code (9–15 digits). Plain
    /// digit-grouped numbers such as <c>1 234 567</c> or <c>12.345.678</c> are rejected.
    /// <see cref="OcrMatch.Normalized"/> is the digits with a leading <c>+</c> when present (a <c>(0)</c>
    /// trunk prefix is dropped).
    /// </summary>
    public static OcrPattern Phone { get; } = new(PhoneRegex(), OcrMatchKind.Phone, ResolvePhone);

    /// <summary>
    /// Calendar dates: ISO (<c>2026-03-12</c>, <c>2026/03/12</c>), numeric day/month/year or month/day/year
    /// (<c>12/03/2026</c>, <c>12.03.26</c>) and English month names (<c>12 Mar 2026</c>,
    /// <c>March 12, 2026</c>). Impossible dates are rejected. <see cref="OcrMatch.Normalized"/> is
    /// <c>yyyy-MM-dd</c> only when unambiguous: numeric dates whose day and month could be swapped, and
    /// two-digit years, have no normalized form. Use <see cref="CreateDate(DateFieldOrder)"/> to resolve them
    /// with a known component order instead.
    /// </summary>
    public static OcrPattern Date { get; } = new(DateRegex(), OcrMatchKind.Date, m => ResolveDate(m, numericNormalizer: null));

    /// <summary>
    /// Monetary amounts with a currency symbol (<c>$ € £ ¥ ₹ ₽ ₩ ₺ ₪ ฿ ₫ ₴ ₦</c>, <c>US$</c>, <c>R$</c>) or a
    /// common ISO 4217 code, before or after the number. Thousands separators (<c>,</c> <c>.</c> <c>'</c>,
    /// no-break space) and both <c>.</c> and <c>,</c> decimals (1–2 digits) are understood; a number whose
    /// separators are inconsistent is rejected. <see cref="OcrMatch.Normalized"/> is <c>"EUR 1234.56"</c>:
    /// the ISO code (unambiguous symbols are mapped, <c>$</c> and <c>¥</c> are kept verbatim) and an
    /// invariant number.
    /// </summary>
    public static OcrPattern Amount { get; } = new(AmountRegex(), OcrMatchKind.Amount, ResolveAmount);

    /// <summary>
    /// International Bank Account Numbers, compact or in groups of four, validated with the ISO 13616 mod-97
    /// check (<see cref="FieldNormalizers.IsValidIban"/>). Look-alike letters in the check digits or in
    /// otherwise all-digit groups are repaired for validation only. A trailing group that breaks the
    /// checksum is dropped (it usually belongs to the next word). <see cref="OcrMatch.Normalized"/> is the
    /// compact upper-case IBAN.
    /// </summary>
    public static OcrPattern Iban { get; } = new(IbanRegex(), OcrMatchKind.Iban, ResolveIban);

    /// <summary>
    /// Payment card numbers (13–19 digits, compact or grouped with spaces/dashes, major-industry digit 2–6),
    /// validated with the Luhn check (<see cref="RedactionPatterns.IsValidLuhn"/>). At most two look-alike
    /// letters are repaired for validation only. <see cref="OcrMatch.Normalized"/> is the digits.
    /// </summary>
    public static OcrPattern PaymentCard { get; } = new(CardRegex(), OcrMatchKind.PaymentCard, ResolveCard);

    /// <summary>
    /// Percentages such as <c>15%</c>, <c>-2,5 %</c>. <see cref="OcrMatch.Normalized"/> is an invariant
    /// number followed by <c>%</c> (e.g. <c>-2.5%</c>).
    /// </summary>
    public static OcrPattern Percentage { get; } = new(PercentRegex(), OcrMatchKind.Percentage, ResolvePercentage);

    /// <summary>
    /// Every built-in pattern, for use with
    /// <see cref="OcrMatchExtensions.FindMatches(EasyOcrSharp.Models.OcrResult, IEnumerable{OcrPattern})"/>.
    /// </summary>
    public static IReadOnlyList<OcrPattern> All { get; } = Array.AsReadOnly(new[]
    {
        Email, Url, Phone, Date, Amount, Iban, PaymentCard, Percentage,
    });

    /// <summary>
    /// A <see cref="Date"/> pattern that also normalizes ambiguous numeric dates by reading them in a known
    /// component <paramref name="order"/> — the same reading <see cref="FieldNormalizers.Date"/> applies
    /// (including its two-digit-year pivot at 68). Which strings are matched is unchanged; only
    /// <see cref="OcrMatch.Normalized"/> differs. ISO and month-name dates are unaffected, and a numeric
    /// date that is not a real day in the requested order keeps a <see langword="null"/> normalized form.
    /// </summary>
    /// <param name="order">
    /// How to read <c>03/04/2026</c>-style dates. <see cref="DateFieldOrder.Auto"/> infers per value and
    /// falls back to day-first, exactly as <see cref="FieldNormalizers.Date"/> does.
    /// </param>
    /// <returns>A new pattern reporting <see cref="OcrMatchKind.Date"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="order"/> is not a defined value.</exception>
    public static OcrPattern CreateDate(DateFieldOrder order)
    {
        if (!Enum.IsDefined(order))
            throw new ArgumentOutOfRangeException(nameof(order), order, "Unknown date field order.");

        var normalizer = FieldNormalizers.Date(order);
        return new OcrPattern(DateRegex(), OcrMatchKind.Date, m => ResolveDate(m, normalizer));
    }

    // ---- resolvers ----

    private static MatchResolution? ResolveEmail(Match m)
    {
        string v = m.Value;
        int at = v.LastIndexOf('@');
        return new MatchResolution(m.Length, string.Concat(v.AsSpan(0, at + 1), v[(at + 1)..].ToLowerInvariant()));
    }

    private static MatchResolution? ResolvePhone(Match m)
    {
        string v = m.Value;
        bool plus = v[0] == '+';
        int digits = 0;
        foreach (char c in v)
        {
            if (char.IsDigit(c)) digits++;
        }

        if (digits > 15 || digits < (plus ? 7 : 9))
        {
            return null;
        }

        if (!plus && !v.Contains('('))
        {
            // A single space or dot separator with 3-digit groups is a grouped number, not a phone.
            char? separator = null;
            bool mixed = false;
            foreach (char c in v)
            {
                if (c is not (' ' or '.' or '-')) continue;
                if (separator is null) separator = c;
                else if (separator != c) mixed = true;
            }

            var groups = v.Split(' ', '.', '-');
            if (!mixed && separator is ' ' or '.' && groups[0].Length <= 3 && groups.Skip(1).All(g => g.Length == 3))
            {
                return null;
            }
        }

        var sb = new StringBuilder(16);
        if (plus) sb.Append('+');
        string body = plus ? v.Replace("(0)", string.Empty, StringComparison.Ordinal) : v;
        foreach (char c in body)
        {
            int d = CharUnicodeInfo.GetDecimalDigitValue(c);
            if (d >= 0) sb.Append((char)('0' + d));
        }
        return new MatchResolution(m.Length, sb.ToString());
    }

    private static MatchResolution? ResolveDate(Match m, FieldNormalizer? numericNormalizer)
    {
        var g = m.Groups;
        if (g["iy"].Success)
        {
            return TryIso(Digits(g["iy"].Value), Digits(g["im"].Value), Digits(g["id"].Value), out var iso)
                ? new MatchResolution(m.Length, iso)
                : null;
        }

        if (g["td"].Success)
        {
            return TryIso(Digits(g["ty"].Value), MonthNumber(g["tm"].Value), Digits(g["td"].Value), out var iso)
                ? new MatchResolution(m.Length, iso)
                : null;
        }

        if (g["mm"].Success)
        {
            return TryIso(Digits(g["my"].Value), MonthNumber(g["mm"].Value), Digits(g["md"].Value), out var iso)
                ? new MatchResolution(m.Length, iso)
                : null;
        }

        int a = Digits(g["na"].Value);
        int b = Digits(g["nb"].Value);
        bool twoDigitYear = g["ny"].Value.Length == 2;
        if (twoDigitYear)
        {
            // "1.5.26" reads like a version number; require two-digit parts with dot separators.
            if (g["nsep"].Value == "." && (g["na"].Length < 2 || g["nb"].Length < 2)) return null;
            if (!IsValidDate(2000, b, a) && !IsValidDate(2000, a, b)) return null;
            return new MatchResolution(m.Length, NormalizeWithOrder(numericNormalizer, m.Value));
        }

        int year = Digits(g["ny"].Value);
        bool dayFirst = IsValidDate(year, b, a);
        bool monthFirst = IsValidDate(year, a, b);
        if (!dayFirst && !monthFirst) return null;

        if (numericNormalizer is not null)
        {
            return new MatchResolution(m.Length, NormalizeWithOrder(numericNormalizer, m.Value));
        }

        string? normalized = dayFirst && monthFirst
            ? (a == b ? Iso(year, a, b) : null)
            : dayFirst ? Iso(year, b, a) : Iso(year, a, b);
        return new MatchResolution(m.Length, normalized);
    }

    /// <summary>Runs the explicit-order date normalizer, if any, keeping only a validated value.</summary>
    private static string? NormalizeWithOrder(FieldNormalizer? normalizer, string value)
    {
        if (normalizer is null) return null;
        var result = normalizer(value);
        return result.IsValid ? result.Value : null;
    }

    private static MatchResolution? ResolveAmount(Match m)
    {
        string? number = NormalizeNumber(m.Groups["num"].Value);
        if (number is null) return null;

        bool negative = m.Groups["sign"].Success || m.Groups["sign2"].Success;
        string currency = m.Groups["cur"].Value switch
        {
            "€" => "EUR",
            "£" => "GBP",
            "₹" => "INR",
            "₽" => "RUB",
            "₩" => "KRW",
            "₺" => "TRY",
            "₪" => "ILS",
            "฿" => "THB",
            "₫" => "VND",
            "₴" => "UAH",
            "₦" => "NGN",
            "US$" => "USD",
            "R$" => "BRL",
            "RMB" => "CNY",
            var other => other,
        };
        return new MatchResolution(m.Length, currency + " " + (negative ? "-" : string.Empty) + number);
    }

    private static MatchResolution? ResolvePercentage(Match m)
    {
        var sb = new StringBuilder(m.Length);
        foreach (char c in m.Value)
        {
            int d = CharUnicodeInfo.GetDecimalDigitValue(c);
            if (d >= 0) sb.Append((char)('0' + d));
            else if (c is '-' or '−') sb.Append('-');
            else if (c is '.' or ',') sb.Append('.');
        }
        sb.Append('%');
        return new MatchResolution(m.Length, sb.ToString());
    }

    private static MatchResolution? ResolveIban(Match m)
    {
        string candidate = m.Value;
        while (true)
        {
            if (TryValidateIban(candidate, out var normalized))
            {
                return new MatchResolution(candidate.Length, normalized);
            }

            // A trailing group may belong to the next word; retry without it.
            int cut = candidate.LastIndexOf(' ');
            if (cut <= 4) return null;
            candidate = candidate[..cut];
        }
    }

    private static MatchResolution? ResolveCard(Match m)
    {
        string candidate = m.Value;
        while (true)
        {
            if (TryValidateCard(candidate, out var digits))
            {
                return new MatchResolution(candidate.Length, digits);
            }

            int cut = candidate.LastIndexOfAny([' ', '-']);
            if (cut < 0) return null;
            candidate = candidate[..cut];
        }
    }

    // ---- validators (internal for tests) ----

    /// <summary>
    /// Validates an IBAN (spaces allowed) with mod-97 via <see cref="FieldNormalizers.IsValidIban"/>,
    /// retrying once with look-alike letters repaired in the check digits and in space-separated groups
    /// that are otherwise all digits. The broader single-character repair of
    /// <see cref="FieldNormalizers.Iban"/> is deliberately not used here: a pattern scan sees every
    /// IBAN-shaped token on the page, and trying every confusion at every position would let roughly one
    /// random token in four "validate".
    /// </summary>
    internal static bool TryValidateIban(string candidate, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        var compact = new StringBuilder(candidate.Length);
        foreach (char c in candidate)
        {
            if (c != ' ') compact.Append(char.ToUpperInvariant(c));
        }

        if (compact.Length is < 15 or > 34) return false;

        string raw = compact.ToString();
        if (FieldNormalizers.IsValidIban(raw))
        {
            normalized = raw;
            return true;
        }

        char[] chars = raw.ToCharArray();
        chars[2] = ToDigitLookalike(chars[2]);
        chars[3] = ToDigitLookalike(chars[3]);
        int offset = 0;
        foreach (var token in candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (offset >= 4 && IsDigitGroupWithLookalikes(token))
            {
                for (int k = 0; k < token.Length; k++)
                {
                    chars[offset + k] = ToDigitLookalike(chars[offset + k]);
                }
            }
            offset += token.Length;
        }

        string repaired = new(chars);
        if (!string.Equals(repaired, raw, StringComparison.Ordinal) && FieldNormalizers.IsValidIban(repaired))
        {
            normalized = repaired;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Validates a payment card number (spaces/dashes allowed): 13–19 digits, major-industry digit 2–6, not
    /// a single repeated digit, Luhn-valid (<see cref="RedactionPatterns.IsValidLuhn"/>). Up to two
    /// look-alike letters are repaired.
    /// </summary>
    internal static bool TryValidateCard(string candidate, [NotNullWhen(true)] out string? digits)
    {
        digits = null;
        var sb = new StringBuilder(candidate.Length);
        int lookalikes = 0;
        foreach (char c in candidate)
        {
            if (c is ' ' or '-') continue;
            char d = ToDigitLookalike(c);
            if (d != c) lookalikes++;
            if (!char.IsAsciiDigit(d)) return false;
            sb.Append(d);
        }

        if (sb.Length is < 13 or > 19 || lookalikes > 2) return false;

        string s = sb.ToString();
        if (s[0] is < '2' or > '6') return false;
        if (s.AsSpan().IndexOfAnyExcept(s[0]) < 0) return false;
        if (!RedactionPatterns.IsValidLuhn(s)) return false;

        digits = s;
        return true;
    }

    /// <summary>
    /// Converts a matched amount number to invariant form (<c>1.234,56</c> → <c>1234.56</c>). A final
    /// separator followed by 1–2 digits is the decimal mark; any other separators must be one repeated
    /// grouping character that differs from it. Returns <see langword="null"/> when the separators are
    /// inconsistent.
    /// </summary>
    internal static string? NormalizeNumber(string s)
    {
        int decimalIndex = -1;
        if (s.Length >= 3 && s[^3] is '.' or ',') decimalIndex = s.Length - 3;
        else if (s.Length >= 2 && s[^2] is '.' or ',') decimalIndex = s.Length - 2;

        int end = decimalIndex < 0 ? s.Length : decimalIndex;
        char? group = null;
        var integer = new StringBuilder(s.Length);
        for (int i = 0; i < end; i++)
        {
            int d = CharUnicodeInfo.GetDecimalDigitValue(s[i]);
            if (d >= 0)
            {
                integer.Append((char)('0' + d));
                continue;
            }

            if (group is null) group = s[i];
            else if (group != s[i]) return null;
        }

        if (decimalIndex >= 0 && group == s[decimalIndex]) return null;

        string whole = integer.ToString().TrimStart('0');
        if (whole.Length == 0) whole = "0";
        if (decimalIndex < 0) return whole;

        var fraction = new StringBuilder(2);
        for (int i = decimalIndex + 1; i < s.Length; i++)
        {
            fraction.Append((char)('0' + CharUnicodeInfo.GetDecimalDigitValue(s[i])));
        }
        return whole + "." + fraction;
    }

    // ---- helpers ----

    private static char ToDigitLookalike(char c) => c switch
    {
        'O' or 'o' => '0',
        'I' or 'l' or 'L' or '|' => '1',
        _ => c,
    };

    private static bool IsDigitGroupWithLookalikes(string token)
    {
        int digits = 0;
        foreach (char c in token)
        {
            if (char.IsAsciiDigit(c)) digits++;
            else if (c is not ('O' or 'I')) return false;
        }
        return digits >= 2 && digits < token.Length;
    }

    private static int Digits(string text)
    {
        int value = 0;
        foreach (char c in text)
        {
            value = value * 10 + CharUnicodeInfo.GetDecimalDigitValue(c);
        }
        return value;
    }

    private static int MonthNumber(string name) => name.Length < 3 ? 0 : name[..3].ToLowerInvariant() switch
    {
        "jan" => 1,
        "feb" => 2,
        "mar" => 3,
        "apr" => 4,
        "may" => 5,
        "jun" => 6,
        "jul" => 7,
        "aug" => 8,
        "sep" => 9,
        "oct" => 10,
        "nov" => 11,
        "dec" => 12,
        _ => 0,
    };

    private static bool IsValidDate(int year, int month, int day)
        => year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month);

    private static bool TryIso(int year, int month, int day, [NotNullWhen(true)] out string? iso)
    {
        iso = IsValidDate(year, month, day) ? Iso(year, month, day) : null;
        return iso is not null;
    }

    private static string Iso(int year, int month, int day)
        => new DateOnly(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
