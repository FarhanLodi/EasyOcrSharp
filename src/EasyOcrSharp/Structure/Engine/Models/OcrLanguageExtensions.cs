namespace EasyOcrSharp.Structure.Engine.Models;

/// <summary>
/// Converts between the strongly-typed <see cref="OcrLanguage"/> enum and the underlying recognizer
/// language codes. The OCR methods accept <see cref="OcrLanguage"/> directly, so most callers never need
/// these — they bridge raw string codes that arrive from outside the type system (CLI arguments,
/// configuration files, or an <see cref="OcrResult.Languages"/> round-trip) back to the enum.
/// </summary>
internal static class OcrLanguageExtensions
{
    /// <summary>
    /// Returns the underlying recognizer language code for <paramref name="language"/> (e.g.
    /// <see cref="OcrLanguage.French"/> → <c>"fr"</c>, <see cref="OcrLanguage.Auto"/> → <c>"auto"</c>).
    /// </summary>
    /// <param name="language">The language to convert.</param>
    /// <returns>The code understood by the recognizer pipeline.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="language"/> is not a defined value.</exception>
    public static string ToCode(this OcrLanguage language) => language switch
    {
        OcrLanguage.Auto => "auto",

        OcrLanguage.English => "en",
        OcrLanguage.ChineseSimplified => "ch",
        OcrLanguage.ChineseTraditional => "chinese_cht",
        OcrLanguage.Japanese => "japan",
        OcrLanguage.Korean => "korean",

        OcrLanguage.Latin => "latin",
        OcrLanguage.French => "fr",
        OcrLanguage.German => "de",
        OcrLanguage.Spanish => "es",
        OcrLanguage.Italian => "it",
        OcrLanguage.Portuguese => "pt",
        OcrLanguage.Dutch => "nl",
        OcrLanguage.Polish => "pl",
        OcrLanguage.Turkish => "tr",
        OcrLanguage.Vietnamese => "vi",
        OcrLanguage.Indonesian => "id",
        OcrLanguage.Malay => "ms",
        OcrLanguage.Swedish => "sv",
        OcrLanguage.Norwegian => "no",
        OcrLanguage.Danish => "da",
        OcrLanguage.Romanian => "ro",
        OcrLanguage.Czech => "cs",
        OcrLanguage.Hungarian => "hu",
        OcrLanguage.Croatian => "hr",
        OcrLanguage.Slovak => "sk",
        OcrLanguage.Slovenian => "sl",
        OcrLanguage.Albanian => "sq",
        OcrLanguage.Swahili => "sw",
        OcrLanguage.Tagalog => "tl",
        OcrLanguage.Latvian => "lv",
        OcrLanguage.Lithuanian => "lt",
        OcrLanguage.Estonian => "et",
        OcrLanguage.Icelandic => "is",
        OcrLanguage.Irish => "ga",
        OcrLanguage.Welsh => "cy",
        OcrLanguage.Azerbaijani => "az",
        OcrLanguage.Uzbek => "uz",

        OcrLanguage.Cyrillic => "cyrillic",
        OcrLanguage.Russian => "ru",
        OcrLanguage.Ukrainian => "uk",
        OcrLanguage.Bulgarian => "bg",
        OcrLanguage.Belarusian => "be",
        OcrLanguage.SerbianCyrillic => "rs_cyrillic",
        OcrLanguage.Mongolian => "mn",

        OcrLanguage.Arabic => "ar",
        OcrLanguage.Persian => "fa",
        OcrLanguage.Urdu => "ur",
        OcrLanguage.Uyghur => "ug",

        OcrLanguage.Devanagari => "devanagari",
        OcrLanguage.Hindi => "hi",
        OcrLanguage.Marathi => "mr",
        OcrLanguage.Nepali => "ne",
        OcrLanguage.Sanskrit => "sa",

        OcrLanguage.Thai => "thai",
        OcrLanguage.Greek => "greek",
        OcrLanguage.Tamil => "tamil",
        OcrLanguage.Telugu => "telugu",

        OcrLanguage.EastSlavic => "eslav",

        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Unknown OCR language."),
    };

    /// <summary>
    /// Converts a sequence of <see cref="OcrLanguage"/> values to their recognizer codes, in order.
    /// </summary>
    /// <param name="languages">The languages to convert.</param>
    /// <returns>The codes, in order.</returns>
    public static string[] ToCodes(this IEnumerable<OcrLanguage> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        return languages.Select(ToCode).ToArray();
    }

    // Built once from ToCode so the reverse lookup auto-stays-in-sync with the forward mapping — adding a
    // new OcrLanguage entry (with a ToCode arm) makes it parseable here without any change to this map.
    private static readonly Dictionary<string, OcrLanguage> CodeToLanguage =
        Enum.GetValues<OcrLanguage>().ToDictionary(l => l.ToCode(), l => l, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Attempts to parse a recognizer language code (e.g. <c>"fr"</c>, <c>"auto"</c>) into the matching
    /// <see cref="OcrLanguage"/>. Case-insensitive and trims surrounding whitespace.
    /// </summary>
    /// <param name="code">The code to parse.</param>
    /// <param name="language">The parsed language when this returns <see langword="true"/>; otherwise <see cref="OcrLanguage.Auto"/>.</param>
    /// <returns><see langword="true"/> if <paramref name="code"/> mapped to a known language; otherwise <see langword="false"/> (including for null/blank input).</returns>
    public static bool TryFromCode(string code, out OcrLanguage language)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            language = OcrLanguage.Auto;
            return false;
        }
        return CodeToLanguage.TryGetValue(code.Trim(), out language);
    }

    /// <summary>
    /// Parses a recognizer language code (e.g. <c>"fr"</c>, <c>"auto"</c>) into the matching
    /// <see cref="OcrLanguage"/>. Case-insensitive and trims surrounding whitespace.
    /// </summary>
    /// <param name="code">The code to parse.</param>
    /// <returns>The matching language.</returns>
    /// <exception cref="ArgumentException"><paramref name="code"/> is null, blank, or not a known code.</exception>
    public static OcrLanguage FromCode(string code)
    {
        if (!TryFromCode(code, out var language))
            throw new ArgumentException($"Unknown OCR language code '{code}'.", nameof(code));
        return language;
    }

    /// <summary>
    /// Parses a sequence of recognizer language codes into <see cref="OcrLanguage"/> values, in order.
    /// </summary>
    /// <param name="codes">The codes to parse.</param>
    /// <returns>The parsed languages, in order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="codes"/> is null.</exception>
    /// <exception cref="ArgumentException">Any code is null, blank, or not a known code.</exception>
    public static IReadOnlyList<OcrLanguage> FromCodes(IEnumerable<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        return codes.Select(FromCode).ToArray();
    }
}
