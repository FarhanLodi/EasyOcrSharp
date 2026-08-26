namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// The Unicode scripts <see cref="ScriptDetection"/> can classify. <see cref="Unknown"/> covers code points
/// that fall outside every counted range (digits, ASCII punctuation, symbols, whitespace, emoji, …) and is
/// never treated as a dominant script on its own.
/// </summary>
internal enum DetectedScript
{
    /// <summary>
    /// No counted script characters were seen (only digits/punctuation/symbols/whitespace).
    /// </summary>
    Unknown,

    /// <summary>
    /// Latin alphabet (Basic Latin letters + Latin-1/Extended supplements). English, French, German, …
    /// </summary>
    Latin,

    /// <summary>
    /// Cyrillic alphabet. Russian, Ukrainian, Bulgarian, Serbian, …
    /// </summary>
    Cyrillic,

    /// <summary>
    /// Arabic script. Arabic, Persian, Urdu, Uyghur.
    /// </summary>
    Arabic,

    /// <summary>
    /// Devanagari script. Hindi, Marathi, Nepali, Sanskrit, …
    /// </summary>
    Devanagari,

    /// <summary>
    /// Korean Hangul (syllables + Jamo).
    /// </summary>
    Hangul,

    /// <summary>
    /// Japanese Hiragana / Katakana kana (the unambiguously-Japanese syllabaries).
    /// </summary>
    Kana,

    /// <summary>
    /// Han / CJK ideographs (shared by Chinese and Japanese kanji).
    /// </summary>
    Han,

    /// <summary>
    /// Thai script.
    /// </summary>
    Thai,

    /// <summary>
    /// Greek and Coptic alphabet.
    /// </summary>
    Greek,

    /// <summary>
    /// Telugu script.
    /// </summary>
    Telugu,

    /// <summary>
    /// Tamil script.
    /// </summary>
    Tamil,
}
