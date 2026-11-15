using System.Diagnostics.CodeAnalysis;
using System.Text;
using EasyOcrSharp.Correction;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Extraction;

/// <summary>
/// Parses the machine-readable zone (ICAO Doc 9303 TD1, TD2 and TD3) of passports, ID cards and visas from
/// OCR output into named fields, with the outcome of every check digit.
/// </summary>
/// <remarks>
/// <para>
/// Candidate lines are normalized (spaces removed, <c>«</c> and trailing <c>K</c> runs read as fillers),
/// located by length, corrected positionally — digit look-alikes in numeric fields, letter look-alikes in
/// alphabetic fields — and verified with the check digits, computed by the same routine as
/// <see cref="FieldNormalizers.MrzCheckDigit(string)"/>.
/// </para>
/// <para>
/// The alphanumeric document number is repaired only by the smallest unique set of letter→digit
/// changes that satisfies its check digit. Anything the check digits cannot vouch for is left as read and
/// reported in <see cref="MrzResult.Checks"/>: the 7-3-1 scheme sees character values modulo 10, so it
/// cannot tell <c>L</c> from <c>1</c> or <c>G</c> from <c>6</c>, and a TD3 composite uses the same weights
/// as the field checks, so it adds no evidence for a single-field repair.
/// </para>
/// <para>
/// For best results OCR the MRZ with <see cref="RecommendedRecognitionOptions"/>, optionally restricted to
/// the bottom of the page with <see cref="RecognitionOptions.Region"/> (for example
/// <c>OcrRegion.Fraction(0, 0.7, 1, 0.3)</c>), or read it as a zone with
/// <see cref="ZoneOcrExtensions"/>.
/// </para>
/// </remarks>
public static class MrzParser
{
    /// <summary>The MRZ character set: <c>A</c>–<c>Z</c>, <c>0</c>–<c>9</c> and the filler <c>&lt;</c>.</summary>
    public const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789<";

    /// <summary>
    /// Recognition options suited to MRZ reading: the recognizer's output is restricted to
    /// <see cref="Alphabet"/> through <see cref="RecognitionOptions.Allowlist"/>. Returns a new instance on
    /// each call; combine with <c>with { Region = … }</c> as needed.
    /// </summary>
    public static RecognitionOptions RecommendedRecognitionOptions => new()
    {
        Allowlist = Alphabet,
    };

    /// <summary>
    /// Locates and parses an MRZ in an OCR result. Lines are first joined into visual rows (so an MRZ line
    /// the detector split into several boxes still parses), then tried individually.
    /// </summary>
    /// <param name="result">The OCR result.</param>
    /// <param name="mrz">The parsed MRZ when found. Check <see cref="MrzResult.IsValid"/> before trusting it.</param>
    /// <returns><see langword="true"/> when an MRZ-shaped block was found and at least one check digit matched.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    public static bool TryParse(OcrResult result, [NotNullWhen(true)] out MrzResult? mrz)
    {
        ArgumentNullException.ThrowIfNull(result);

        MrzResult? best = null;
        var boxes = result.Lines.Select(TextGeometry.BoxOf).ToList();
        if (boxes.Any(b => !b.IsEmpty))
        {
            var rows = TextGeometry.GroupRows(boxes, 0.5)
                .Select(row => string.Concat(row.Select(i => result.Lines[i].Text)));
            best = FindBest(rows);
        }

        if (best is not { IsValid: true })
        {
            var fromLines = FindBest(result.Lines.Select(l => l.Text));
            if (fromLines is not null && (best is null || Score(fromLines) > Score(best)))
            {
                best = fromLines;
            }
        }

        mrz = best;
        return mrz is not null;
    }

    /// <summary>
    /// Locates and parses an MRZ in a sequence of text lines (for example OCR lines, or a pasted MRZ).
    /// </summary>
    /// <param name="lines">The candidate lines, in top-to-bottom order.</param>
    /// <param name="mrz">The parsed MRZ when found. Check <see cref="MrzResult.IsValid"/> before trusting it.</param>
    /// <returns><see langword="true"/> when an MRZ-shaped block was found and at least one check digit matched.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is <see langword="null"/>.</exception>
    public static bool TryParse(IEnumerable<string> lines, [NotNullWhen(true)] out MrzResult? mrz)
    {
        ArgumentNullException.ThrowIfNull(lines);
        mrz = FindBest(lines);
        return mrz is not null;
    }

    private static MrzResult? FindBest(IEnumerable<string?> lines)
    {
        var normalized = new List<string>();
        foreach (var line in lines)
        {
            if (Normalize(line) is { } n) normalized.Add(n);
        }

        MrzResult? best = null;
        int bestScore = 0;
        for (int i = 0; i < normalized.Count; i++)
        {
            Consider(TryTwoLine(normalized, i, 44, MrzFormat.Td3));
            Consider(TryTwoLine(normalized, i, 36, MrzFormat.Td2));
            Consider(TryTd1(normalized, i));
        }
        return best;

        void Consider(MrzResult? candidate)
        {
            if (candidate is null) return;
            int score = Score(candidate);
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }
    }

    // A structurally plausible block with no matching check digit scores 0 and is never reported.
    private static int Score(MrzResult result) => result.IsValid ? 100 : result.Checks.PassedCount;

    /// <summary>
    /// Upper-cases, strips whitespace and maps filler look-alikes. <c>«</c> is kept as a marker so
    /// <see cref="Fit"/> can read it as one or two fillers. Returns <see langword="null"/> for non-MRZ text.
    /// </summary>
    internal static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var sb = new StringBuilder(raw.Length);
        foreach (char ch in raw)
        {
            if (char.IsWhiteSpace(ch)) continue;
            char c = ch switch
            {
                '‹' or '＜' or '≺' => '<',   // ‹ ＜ ≺
                '«' => '«',                       // « (resolved by Fit)
                _ => char.ToUpperInvariant(ch),
            };
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '<' or '«') sb.Append(c);
            else return null;
        }

        // Runs of fillers at the end of a line are commonly misread as K.
        int k = sb.Length;
        while (k > 0 && sb[k - 1] == 'K') k--;
        if (sb.Length - k >= 2)
        {
            for (int j = k; j < sb.Length; j++) sb[j] = '<';
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// Fits a normalized line to the format width: resolves <c>«</c> as two or one fillers, pads a line that
    /// lost up to four trailing fillers, and trims up to four surplus trailing fillers.
    /// </summary>
    private static string? Fit(string line, int target)
    {
        string[] variants = line.Contains('«')
            ? [line.Replace("«", "<<"), line.Replace("«", "<")]
            : [line];

        foreach (var v in variants)
        {
            if (v.Length == target) return v;
        }

        foreach (var v in variants)
        {
            int diff = target - v.Length;
            if (diff is > 0 and <= 4 && v[^1] == '<') return v + new string('<', diff);
            if (diff is < 0 and >= -4 && v.AsSpan(target).IndexOfAnyExcept('<') < 0) return v[..target];
        }
        return null;
    }

    private static MrzResult? TryTwoLine(List<string> lines, int i, int length, MrzFormat format)
    {
        if (i + 1 >= lines.Count) return null;
        var l1 = Fit(lines[i], length);
        var l2 = l1 is null ? null : Fit(lines[i + 1], length);
        if (l1 is null || l2 is null) return null;

        return ParseTwoLine(l1, l2, format);
    }

    private static MrzResult? TryTd1(List<string> lines, int i)
    {
        if (i + 2 >= lines.Count) return null;
        var l1 = Fit(lines[i], 30);
        var l2 = l1 is null ? null : Fit(lines[i + 1], 30);
        var l3 = l2 is null ? null : Fit(lines[i + 2], 30);
        if (l1 is null || l2 is null || l3 is null) return null;

        return ParseTd1(l1, l2, l3);
    }

    // TD3 (44) and TD2 (36) share line 2's layout up to position 28.
    private static MrzResult? ParseTwoLine(string line1, string line2, MrzFormat format)
    {
        int len = line1.Length;
        char[] a = line1.ToCharArray();
        char[] b = line2.ToCharArray();

        ToLetters(a, 0, len);
        if (!char.IsAsciiLetterUpper(a[0])) return null;
        bool visa = a[0] == 'V';

        ToDigits(b, 9, 1);
        ToLetters(b, 10, 3);
        ToDigits(b, 13, 7);
        ToLetters(b, 20, 1);
        ToDigits(b, 21, 7);

        bool documentOk = RepairDocumentNumber(b, 0, 9, b[9]);
        bool birthOk = CheckOk(b.AsSpan(13, 6), b[19]);
        bool expiryOk = CheckOk(b.AsSpan(21, 6), b[27]);

        bool? optionalOk = null, compositeOk = null;
        string optional;
        if (visa)
        {
            optional = new string(b, 28, len - 28);
        }
        else if (format == MrzFormat.Td3)
        {
            ToDigits(b, 42, 2);
            optional = new string(b, 28, 14);
            optionalOk = CheckOk(b.AsSpan(28, 14), b[42], allowFiller: true);
            compositeOk = CheckOk(Concat(b, (0, 10), (13, 7), (21, 22)), b[43]);
        }
        else
        {
            ToDigits(b, 35, 1);
            optional = new string(b, 28, 7);
            compositeOk = CheckOk(Concat(b, (0, 10), (13, 7), (21, 14)), b[35]);
        }

        var (surname, given) = ParseNames(new string(a, 5, len - 5));
        return new MrzResult
        {
            Format = format,
            DocumentType = Clean(new string(a, 0, 2)),
            IssuingCountry = Clean(new string(a, 2, 3)),
            Surname = surname,
            GivenNames = given,
            DocumentNumber = Clean(new string(b, 0, 9)),
            Nationality = Clean(new string(b, 10, 3)),
            BirthDate = ParseDate(b, 13, expiry: false),
            Sex = b[20].ToString(),
            ExpiryDate = ParseDate(b, 21, expiry: true),
            OptionalData = Clean(optional),
            Checks = new MrzCheckResults(documentOk, birthOk, expiryOk, optionalOk, compositeOk),
            RawLines = [line1, line2],
            CorrectedLines = [new string(a), new string(b)],
        };
    }

    private static MrzResult? ParseTd1(string line1, string line2, string line3)
    {
        char[] a = line1.ToCharArray();
        char[] b = line2.ToCharArray();
        char[] c = line3.ToCharArray();

        ToLetters(a, 0, 5);
        if (!char.IsAsciiLetterUpper(a[0])) return null;
        ToDigits(b, 0, 7);
        ToLetters(b, 7, 1);
        ToDigits(b, 8, 7);
        ToLetters(b, 15, 3);
        ToDigits(b, 29, 1);
        ToLetters(c, 0, 30);

        string documentNumber;
        string optional1;
        bool documentOk;
        if (a[14] == '<' && a[15] != '<')
        {
            // Long document number (ICAO 9303-5 note j): it continues in the optional data, whose last
            // character before the next filler is the check digit over the whole number.
            int overflowEnd = Array.IndexOf(a, '<', 15);
            if (overflowEnd < 0) overflowEnd = 30;
            ToDigits(a, overflowEnd - 1, 1);
            string number = new string(a, 5, 9).TrimEnd('<') + new string(a, 15, Math.Max(0, overflowEnd - 16));
            documentOk = overflowEnd - 15 >= 2 && CheckOk(number, a[overflowEnd - 1]);
            documentNumber = number;
            optional1 = overflowEnd + 1 < 30 ? new string(a, overflowEnd + 1, 29 - overflowEnd) : string.Empty;
        }
        else
        {
            ToDigits(a, 14, 1);
            documentOk = RepairDocumentNumber(a, 5, 9, a[14]);
            documentNumber = Clean(new string(a, 5, 9));
            optional1 = new string(a, 15, 15);
        }

        bool birthOk = CheckOk(b.AsSpan(0, 6), b[6]);
        bool expiryOk = CheckOk(b.AsSpan(8, 6), b[14]);
        string composite = new string(a, 5, 25) + new string(b, 0, 7) + new string(b, 8, 7) + new string(b, 18, 11);
        bool compositeOk = CheckOk(composite, b[29]);

        var (surname, given) = ParseNames(new string(c));
        return new MrzResult
        {
            Format = MrzFormat.Td1,
            DocumentType = Clean(new string(a, 0, 2)),
            IssuingCountry = Clean(new string(a, 2, 3)),
            Surname = surname,
            GivenNames = given,
            DocumentNumber = documentNumber,
            Nationality = Clean(new string(b, 15, 3)),
            BirthDate = ParseDate(b, 0, expiry: false),
            Sex = b[7].ToString(),
            ExpiryDate = ParseDate(b, 8, expiry: true),
            OptionalData = Clean(optional1),
            OptionalData2 = Clean(new string(b, 18, 11)),
            Checks = new MrzCheckResults(documentOk, birthOk, expiryOk, null, compositeOk),
            RawLines = [line1, line2, line3],
            CorrectedLines = [new string(a), new string(b), new string(c)],
        };
    }

    private static bool CheckOk(ReadOnlySpan<char> data, char check, bool allowFiller = false)
    {
        int computed = FieldNormalizers.ComputeMrzCheckDigit(data);
        if (computed < 0) return false;
        if (check == '<') return allowFiller && computed == 0;
        return char.IsAsciiDigit(check) && check - '0' == computed;
    }

    /// <summary>
    /// Checks an alphanumeric document number; when it fails, looks for the <b>smallest</b> set of letter
    /// look-alikes which, read as digits, makes the check digit match, and applies it only when that
    /// smallest repair is unique.
    /// </summary>
    /// <remarks>
    /// Converting every look-alike at once (the obvious approach) is unsafe: the 7-3-1 check digit only
    /// sees character values modulo 10, and <c>L</c> (21) ≡ <c>1</c>, <c>G</c> (16) ≡ <c>6</c>, so a
    /// legitimate <c>L</c> or <c>G</c> in the number would be flipped "for free" alongside the real fix
    /// (<c>L8989O2C3</c> would become <c>1898902C3</c>, which passes). A minimal repair never includes such
    /// a check-neutral change, and an ambiguous one is left for a human rather than guessed.
    /// </remarks>
    private static bool RepairDocumentNumber(char[] line, int start, int length, char check)
    {
        if (CheckOk(line.AsSpan(start, length), check)) return true;

        var positions = new List<int>(length);
        for (int i = start; i < start + length; i++)
        {
            if (DigitFor(line[i]) != line[i]) positions.Add(i);
        }
        if (positions.Count == 0) return false;

        int subsets = 1 << positions.Count;
        var buffer = new char[length];
        for (int size = 1; size <= positions.Count; size++)
        {
            char[]? found = null;
            for (int mask = 1; mask < subsets; mask++)
            {
                if (System.Numerics.BitOperations.PopCount((uint)mask) != size) continue;

                Array.Copy(line, start, buffer, 0, length);
                for (int bit = 0; bit < positions.Count; bit++)
                {
                    if ((mask & (1 << bit)) != 0)
                    {
                        int p = positions[bit] - start;
                        buffer[p] = DigitFor(buffer[p]);
                    }
                }

                if (!CheckOk(buffer, check)) continue;
                if (found is not null) return false; // two different minimal repairs: ambiguous
                found = (char[])buffer.Clone();
            }

            if (found is not null)
            {
                Array.Copy(found, 0, line, start, length);
                return true;
            }
        }
        return false;
    }

    private static void ToDigits(char[] line, int start, int length)
    {
        for (int i = start; i < start + length; i++)
        {
            line[i] = DigitFor(line[i]);
        }
    }

    private static char DigitFor(char c) => c switch
    {
        'O' or 'Q' or 'D' => '0',
        'I' or 'L' => '1',
        'Z' => '2',
        'S' => '5',
        'G' => '6',
        'B' => '8',
        _ => c,
    };

    private static void ToLetters(char[] line, int start, int length)
    {
        for (int i = start; i < start + length; i++)
        {
            line[i] = line[i] switch
            {
                '0' => 'O',
                '1' => 'I',
                '2' => 'Z',
                '5' => 'S',
                '6' => 'G',
                '8' => 'B',
                var other => other,
            };
        }
    }

    private static string Concat(char[] line, params (int Start, int Length)[] parts)
    {
        var sb = new StringBuilder();
        foreach (var (start, length) in parts) sb.Append(line, start, length);
        return sb.ToString();
    }

    private static (string Surname, string GivenNames) ParseNames(string field)
    {
        string trimmed = field.TrimEnd('<');
        int separator = trimmed.IndexOf("<<", StringComparison.Ordinal);
        string surname = separator < 0 ? trimmed : trimmed[..separator];
        string given = separator < 0 ? string.Empty : trimmed[(separator + 2)..];
        return (Words(surname), Words(given));
    }

    private static string Words(string value) => string.Join(' ', value.Split('<', StringSplitOptions.RemoveEmptyEntries));

    private static string Clean(string value) => value.TrimEnd('<').Replace('<', ' ').Trim();

    private static DateOnly? ParseDate(char[] line, int start, bool expiry)
    {
        for (int i = start; i < start + 6; i++)
        {
            if (!char.IsAsciiDigit(line[i])) return null;
        }

        int yy = (line[start] - '0') * 10 + (line[start + 1] - '0');
        int month = (line[start + 2] - '0') * 10 + (line[start + 3] - '0');
        int day = (line[start + 4] - '0') * 10 + (line[start + 5] - '0');

        int currentYear = DateTime.UtcNow.Year;
        int year = 2000 + yy;
        if (expiry ? year > currentYear + 50 : year > currentYear) year -= 100;

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        return new DateOnly(year, month, day);
    }
}
