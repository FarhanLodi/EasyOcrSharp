namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// A static, dependency-free classifier that infers the dominant Unicode script of a string by counting the
/// code points that fall into each script's well-known block range(s), and maps that script to the
/// representative PaddleOCR language code its recognizer pack is registered under in
/// <see cref="StructureModelRegistry.FindByLanguage"/>.
/// <para>
/// This is a coarse heuristic used by the language auto-detection fast path: digits, punctuation, symbols
/// and whitespace are ignored (they belong to no script), so the dominant script reflects only the letters
/// actually present. It is intentionally lightweight — it does not distinguish Simplified from Traditional
/// Chinese, nor Chinese kanji from Japanese kanji (both map to <see cref="DetectedScript.Han"/>); the
/// multi-pack confidence vote in the engine resolves those finer cases.
/// </para>
/// </summary>
internal static class ScriptDetection
{
    /// <summary>
    /// Counts the code points of <paramref name="text"/> per script and returns the script with the highest
    /// count, or <see cref="DetectedScript.Unknown"/> when the string contains no counted script characters
    /// (e.g. only digits / punctuation). Code points outside every counted range do not vote.
    /// </summary>
    public static DetectedScript DominantScript(ReadOnlySpan<char> text)
    {
        // One bucket per non-Unknown DetectedScript value; indexed by (int)script.
        Span<int> counts = stackalloc int[12];

        for (int i = 0; i < text.Length; i++)
        {
            // Decode surrogate pairs so astral-plane scripts (none counted here, but kept correct) and BMP
            // code points are both classified by their true code point.
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }

            var script = Classify(cp);
            if (script != DetectedScript.Unknown) counts[(int)script]++;
        }

        var best = DetectedScript.Unknown;
        int bestCount = 0;
        // Skip index 0 (Unknown); pick the highest-count script. Ties resolve to the lower enum value, which
        // is stable and deterministic across runs.
        for (int s = 1; s < counts.Length; s++)
        {
            if (counts[s] > bestCount)
            {
                bestCount = counts[s];
                best = (DetectedScript)s;
            }
        }
        return best;
    }

    /// <summary>
    /// True when the dominant script of <paramref name="text"/> is one the default PP-OCRv5 recognizer
    /// (Chinese / English / Japanese on <c>ppocrv5_dict.txt</c>) natively covers — i.e. Latin letters or any
    /// CJK script (Han ideographs and Japanese kana). Used by the fast path to decide whether a high-confidence
    /// default-pack reading can be accepted without trying the other candidate packs.
    /// </summary>
    public static bool IsLatinOrCjk(ReadOnlySpan<char> text)
    {
        var script = DominantScript(text);
        return script is DetectedScript.Latin or DetectedScript.Han or DetectedScript.Kana or DetectedScript.Hangul;
    }

    /// <summary>
    /// Maps a detected script to the representative PaddleOCR language code its recognizer pack is registered
    /// under (the code accepted by <see cref="StructureModelRegistry.FindByLanguage"/>). Returns <c>null</c> for
    /// <see cref="DetectedScript.Unknown"/> or any script without a dedicated pack.
    /// </summary>
    public static string? ToLanguageCode(DetectedScript script) => script switch
    {
        DetectedScript.Latin => "latin",
        DetectedScript.Cyrillic => "cyrillic",
        DetectedScript.Arabic => "arabic",
        DetectedScript.Devanagari => "devanagari",
        DetectedScript.Hangul => "korean",
        DetectedScript.Kana => "japan",
        DetectedScript.Han => "ch", // Han ideographs → the default PP-OCRv5 recognizer (covers Chinese)
        DetectedScript.Thai => "thai",
        DetectedScript.Greek => "greek",
        DetectedScript.Telugu => "telugu",
        DetectedScript.Tamil => "tamil",
        _ => null,
    };

    /// <summary>
    /// Classifies a single Unicode code point into its <see cref="DetectedScript"/> by block range, returning
    /// <see cref="DetectedScript.Unknown"/> for anything outside the counted scripts (digits, ASCII
    /// punctuation, symbols, whitespace, …). Ranges follow the canonical Unicode block assignments.
    /// </summary>
    private static DetectedScript Classify(int cp)
    {
        // Latin: only count actual letters from the Basic Latin / Latin-1 / Latin Extended blocks. ASCII
        // digits and punctuation are deliberately excluded so they never vote for a script.
        if ((cp >= 'A' && cp <= 'Z') || (cp >= 'a' && cp <= 'z')) return DetectedScript.Latin;
        if (cp >= 0x00C0 && cp <= 0x024F)                          // Latin-1 Supplement letters + Latin Extended-A/B
        {
            // 0x00D7 (×) and 0x00F7 (÷) are math symbols inside the Latin-1 letter range — don't count them.
            if (cp == 0x00D7 || cp == 0x00F7) return DetectedScript.Unknown;
            return DetectedScript.Latin;
        }
        if (cp >= 0x1E00 && cp <= 0x1EFF) return DetectedScript.Latin; // Latin Extended Additional

        // Greek and Coptic (+ Greek Extended). Excludes the Coptic-specific supplement, which is rare here.
        if ((cp >= 0x0370 && cp <= 0x03FF) || (cp >= 0x1F00 && cp <= 0x1FFF)) return DetectedScript.Greek;

        // Cyrillic (base + Supplement + Extended-A/B).
        if ((cp >= 0x0400 && cp <= 0x04FF) || (cp >= 0x0500 && cp <= 0x052F) ||
            (cp >= 0x2DE0 && cp <= 0x2DFF) || (cp >= 0xA640 && cp <= 0xA69F)) return DetectedScript.Cyrillic;

        // Arabic (base + Supplement + Extended-A + Presentation Forms A/B).
        if ((cp >= 0x0600 && cp <= 0x06FF) || (cp >= 0x0750 && cp <= 0x077F) ||
            (cp >= 0x08A0 && cp <= 0x08FF) || (cp >= 0xFB50 && cp <= 0xFDFF) ||
            (cp >= 0xFE70 && cp <= 0xFEFF)) return DetectedScript.Arabic;

        // Devanagari (base + Extended).
        if ((cp >= 0x0900 && cp <= 0x097F) || (cp >= 0xA8E0 && cp <= 0xA8FF)) return DetectedScript.Devanagari;

        // Tamil and Telugu (distinct Indic blocks).
        if (cp >= 0x0B80 && cp <= 0x0BFF) return DetectedScript.Tamil;
        if (cp >= 0x0C00 && cp <= 0x0C7F) return DetectedScript.Telugu;

        // Thai.
        if (cp >= 0x0E00 && cp <= 0x0E7F) return DetectedScript.Thai;

        // Korean Hangul: syllables + Jamo + compatibility/extended Jamo.
        if ((cp >= 0xAC00 && cp <= 0xD7AF) || (cp >= 0x1100 && cp <= 0x11FF) ||
            (cp >= 0x3130 && cp <= 0x318F) || (cp >= 0xA960 && cp <= 0xA97F)) return DetectedScript.Hangul;

        // Japanese kana: Hiragana + Katakana (the unambiguously-Japanese syllabaries). Classified before Han
        // so kana never falls through to the ideograph range.
        if ((cp >= 0x3040 && cp <= 0x309F) || (cp >= 0x30A0 && cp <= 0x30FF) ||
            (cp >= 0x31F0 && cp <= 0x31FF)) return DetectedScript.Kana;

        // Han / CJK unified ideographs (shared by Chinese hanzi and Japanese kanji) + common extensions and
        // the CJK compatibility ideographs block.
        if ((cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0x3400 && cp <= 0x4DBF) ||
            (cp >= 0xF900 && cp <= 0xFAFF) || (cp >= 0x20000 && cp <= 0x2A6DF)) return DetectedScript.Han;

        return DetectedScript.Unknown;
    }
}
