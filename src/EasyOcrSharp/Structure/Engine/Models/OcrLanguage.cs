namespace EasyOcrSharp.Structure.Engine.Models;

/// <summary>
/// The languages/scripts EasyOcrSharp.Structure.Engine can recognize, as a strongly-typed alternative to the raw string
/// codes (e.g. <see cref="English"/> instead of <c>"en"</c>). Convert to the underlying code with
/// <see cref="OcrLanguageExtensions.ToCode(OcrLanguage)"/>, or pass the enum directly to the
/// <c>ExtractTextFromImage</c> overloads in <see cref="OcrLanguageExtensions"/>.
/// <para>
/// Recognition is <b>per-script</b>: every Latin-script language shares one recognizer (likewise Cyrillic,
/// Arabic, Devanagari, …), so picking <see cref="French"/> or <see cref="German"/> loads the same model as
/// <see cref="Latin"/> — the named entries exist for readability. Use <see cref="Auto"/> to let EasyOcrSharp.Structure.Engine
/// detect the dominant script and load the matching pack on demand.
/// </para>
/// </summary>
internal enum OcrLanguage
{
    /// <summary>
    /// Auto-detect the dominant script and load the matching recognizer (maps to <c>"auto"</c>).
    /// </summary>
    Auto = 0,

    // ---- East Asian ----

    /// <summary>
    /// English (<c>en</c>) — served by the default PP-OCRv5 recognizer.
    /// </summary>
    English,

    /// <summary>
    /// Simplified Chinese (<c>ch</c>).
    /// </summary>
    ChineseSimplified,

    /// <summary>
    /// Traditional Chinese (<c>chinese_cht</c>).
    /// </summary>
    ChineseTraditional,

    /// <summary>
    /// Japanese (<c>japan</c>).
    /// </summary>
    Japanese,

    /// <summary>
    /// Korean (<c>korean</c>).
    /// </summary>
    Korean,

    // ---- Latin script (one shared recognizer) ----

    /// <summary>
    /// Any Latin-script language (<c>latin</c>) — use when the specific language isn't listed.
    /// </summary>
    Latin,

    /// <summary>
    /// French (<c>fr</c>).
    /// </summary>
    French,
    /// <summary>
    /// German (<c>de</c>).
    /// </summary>
    German,
    /// <summary>
    /// Spanish (<c>es</c>).
    /// </summary>
    Spanish,
    /// <summary>
    /// Italian (<c>it</c>).
    /// </summary>
    Italian,
    /// <summary>
    /// Portuguese (<c>pt</c>).
    /// </summary>
    Portuguese,
    /// <summary>
    /// Dutch (<c>nl</c>).
    /// </summary>
    Dutch,
    /// <summary>
    /// Polish (<c>pl</c>).
    /// </summary>
    Polish,
    /// <summary>
    /// Turkish (<c>tr</c>).
    /// </summary>
    Turkish,
    /// <summary>
    /// Vietnamese (<c>vi</c>).
    /// </summary>
    Vietnamese,
    /// <summary>
    /// Indonesian (<c>id</c>).
    /// </summary>
    Indonesian,
    /// <summary>
    /// Malay (<c>ms</c>).
    /// </summary>
    Malay,
    /// <summary>
    /// Swedish (<c>sv</c>).
    /// </summary>
    Swedish,
    /// <summary>
    /// Norwegian (<c>no</c>).
    /// </summary>
    Norwegian,
    /// <summary>
    /// Danish (<c>da</c>).
    /// </summary>
    Danish,
    /// <summary>
    /// Romanian (<c>ro</c>).
    /// </summary>
    Romanian,
    /// <summary>
    /// Czech (<c>cs</c>).
    /// </summary>
    Czech,
    /// <summary>
    /// Hungarian (<c>hu</c>).
    /// </summary>
    Hungarian,
    /// <summary>
    /// Croatian (<c>hr</c>).
    /// </summary>
    Croatian,
    /// <summary>
    /// Slovak (<c>sk</c>).
    /// </summary>
    Slovak,
    /// <summary>
    /// Slovenian (<c>sl</c>).
    /// </summary>
    Slovenian,
    /// <summary>
    /// Albanian (<c>sq</c>).
    /// </summary>
    Albanian,
    /// <summary>
    /// Swahili (<c>sw</c>).
    /// </summary>
    Swahili,
    /// <summary>
    /// Tagalog (<c>tl</c>).
    /// </summary>
    Tagalog,
    /// <summary>
    /// Latvian (<c>lv</c>).
    /// </summary>
    Latvian,
    /// <summary>
    /// Lithuanian (<c>lt</c>).
    /// </summary>
    Lithuanian,
    /// <summary>
    /// Estonian (<c>et</c>).
    /// </summary>
    Estonian,
    /// <summary>
    /// Icelandic (<c>is</c>).
    /// </summary>
    Icelandic,
    /// <summary>
    /// Irish (<c>ga</c>).
    /// </summary>
    Irish,
    /// <summary>
    /// Welsh (<c>cy</c>).
    /// </summary>
    Welsh,
    /// <summary>
    /// Azerbaijani (<c>az</c>).
    /// </summary>
    Azerbaijani,
    /// <summary>
    /// Uzbek (<c>uz</c>).
    /// </summary>
    Uzbek,

    // ---- Cyrillic script ----

    /// <summary>
    /// Any Cyrillic-script language (<c>cyrillic</c>).
    /// </summary>
    Cyrillic,
    /// <summary>
    /// Russian (<c>ru</c>).
    /// </summary>
    Russian,
    /// <summary>
    /// Ukrainian (<c>uk</c>).
    /// </summary>
    Ukrainian,
    /// <summary>
    /// Bulgarian (<c>bg</c>).
    /// </summary>
    Bulgarian,
    /// <summary>
    /// Belarusian (<c>be</c>).
    /// </summary>
    Belarusian,
    /// <summary>
    /// Serbian, Cyrillic script (<c>rs_cyrillic</c>).
    /// </summary>
    SerbianCyrillic,
    /// <summary>
    /// Mongolian (<c>mn</c>).
    /// </summary>
    Mongolian,

    // ---- Arabic script ----

    /// <summary>
    /// Arabic (<c>ar</c>).
    /// </summary>
    Arabic,
    /// <summary>
    /// Persian / Farsi (<c>fa</c>).
    /// </summary>
    Persian,
    /// <summary>
    /// Urdu (<c>ur</c>).
    /// </summary>
    Urdu,
    /// <summary>
    /// Uyghur (<c>ug</c>).
    /// </summary>
    Uyghur,

    // ---- Devanagari script ----

    /// <summary>
    /// Any Devanagari-script language (<c>devanagari</c>).
    /// </summary>
    Devanagari,
    /// <summary>
    /// Hindi (<c>hi</c>).
    /// </summary>
    Hindi,
    /// <summary>
    /// Marathi (<c>mr</c>).
    /// </summary>
    Marathi,
    /// <summary>
    /// Nepali (<c>ne</c>).
    /// </summary>
    Nepali,
    /// <summary>
    /// Sanskrit (<c>sa</c>).
    /// </summary>
    Sanskrit,

    // ---- Other scripts ----

    /// <summary>
    /// Thai (<c>thai</c>).
    /// </summary>
    Thai,
    /// <summary>
    /// Greek (<c>greek</c>).
    /// </summary>
    Greek,
    /// <summary>
    /// Tamil (<c>tamil</c>).
    /// </summary>
    Tamil,
    /// <summary>
    /// Telugu (<c>telugu</c>).
    /// </summary>
    Telugu,

    /// <summary>
    /// East-Slavic recognizer pack (<c>eslav</c>) — Russian/Ukrainian/Belarusian tuned variant.
    /// </summary>
    EastSlavic,
}
