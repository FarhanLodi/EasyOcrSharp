using EasyOcrSharp.Models;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Resolves <see cref="TextReadingDirection"/> to a concrete left-to-right / right-to-left decision for the
/// reading-order sorters.
/// </summary>
internal static class ScriptDirection
{
    /// <summary>
    /// Language codes whose script is written right-to-left. Kept to codes this library can actually be asked
    /// for — the Arabic recognizer pack (<c>ar</c>, <c>fa</c>, <c>ug</c>, <c>ur</c>) plus the other
    /// right-to-left codes a caller may pass through the structure engine or a custom recognizer.
    /// <para>
    /// Deliberately excludes <c>ku</c>: EasyOCR routes Kurdish to the <b>Latin</b> pack, so it is Kurmanji in
    /// Latin script and reads left-to-right.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> RightToLeftCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar",   // Arabic
        "fa",   // Persian / Farsi
        "ur",   // Urdu
        "ug",   // Uyghur
        "he",   // Hebrew
        "iw",   // Hebrew (legacy ISO code)
        "yi",   // Yiddish
        "ps",   // Pashto
        "sd",   // Sindhi
        "dv",   // Divehi / Maldivian
        "ckb",  // Central Kurdish (Sorani, Arabic script)
        "arc",  // Aramaic
        "syr",  // Syriac
    };

    /// <summary>
    /// Whether text in <paramref name="languages"/> should be assembled right-to-left under
    /// <paramref name="direction"/>.
    /// </summary>
    /// <param name="direction">The caller's requested direction; <see cref="TextReadingDirection.Auto"/> derives it.</param>
    /// <param name="languages">The requested language codes, or <c>null</c> when none are known.</param>
    /// <returns><c>true</c> to assemble right-to-left.</returns>
    public static bool IsRightToLeft(TextReadingDirection direction, IEnumerable<string>? languages)
    {
        switch (direction)
        {
            case TextReadingDirection.LeftToRight:
                return false;
            case TextReadingDirection.RightToLeft:
                return true;
        }

        if (languages is null) return false;

        // Every requested language must be right-to-left. A mixed request resolves left-to-right: see the
        // reasoning on TextReadingDirection.Auto.
        bool any = false;
        foreach (var language in languages)
        {
            if (string.IsNullOrWhiteSpace(language)) continue;
            any = true;
            if (!IsRightToLeftLanguage(language)) return false;
        }

        return any;
    }

    /// <summary>
    /// Whether <paramref name="languages"/> should be assembled right-to-left, deriving the direction from
    /// the languages alone. For call sites that expose no per-call direction option — the structure engine's
    /// text pipeline, which takes its own <c>RecognitionOptions</c> type.
    /// </summary>
    public static bool IsRightToLeft(IEnumerable<string>? languages)
        => IsRightToLeft(TextReadingDirection.Auto, languages);

    /// <summary>
    /// Whether a single language code names a right-to-left script. Accepts a bare code (<c>ar</c>) or a
    /// tagged one (<c>ar-SA</c>, <c>fa_IR</c>), matching on the primary subtag.
    /// </summary>
    public static bool IsRightToLeftLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language)) return false;

        var code = language.Trim();
        int separator = code.IndexOfAny(['-', '_']);
        if (separator > 0) code = code[..separator];

        return RightToLeftCodes.Contains(code);
    }
}
