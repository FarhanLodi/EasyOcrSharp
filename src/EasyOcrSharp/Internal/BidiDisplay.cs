using EasyOcrSharp.Models;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Dependency-free equivalent of python-bidi's <c>get_display</c>, which Python EasyOCR applies to every
/// reading of its <c>arabic</c> recognizer pack (<c>easyocr.py</c>, <c>Reader.recognize</c>:
/// <c>if self.model_lang == 'arabic': item[1] = get_display(item[1])</c>).
/// </summary>
/// <remarks>
/// <para>
/// Why it is needed: a CTC recognizer's alignment is monotonic in the image columns, so it can only emit
/// characters in the order they are <i>painted</i>, left to right. For Arabic that is the reverse of the
/// reading order — <c>سلام</c> comes out as <c>م ا ل س</c> — while digit runs and Latin words, which are painted
/// left to right, come out correctly. Running the bidirectional algorithm over that string reverses the
/// right-to-left runs again and leaves the left-to-right ones alone, which recovers the logical order.
/// </para>
/// <para>
/// This implements the subset of the Unicode Bidirectional Algorithm (UAX #9) that a single recognized
/// line can exercise: one paragraph whose base direction comes from its first strong character (P2/P3),
/// no explicit embeddings or isolates, weak-type resolution W1–W7, neutral resolution N1–N2, implicit
/// levels I1–I2, trailing-whitespace reset L1, run reversal L2 and bracket mirroring L4. Bidi classes are
/// derived from code-point ranges rather than the full Unicode database, which is exact for the Arabic,
/// Hebrew, Latin, digit and ASCII punctuation characters the recognizer vocabularies contain.
/// </para>
/// </remarks>
internal static class BidiDisplay
{
    private enum BidiClass : byte { L, R, AL, EN, ES, ET, AN, CS, NSM, WS, ON }

    /// <summary>Returns <paramref name="text"/> passed through the bidirectional algorithm (python-bidi's <c>get_display</c>).</summary>
    public static string GetDisplay(string text)
        => TryReorder(text, out var display, out _) ? display : text;

    /// <summary>Applies <see cref="Apply(OcrLine)"/> to every line.</summary>
    public static IReadOnlyList<OcrLine> Apply(IReadOnlyList<OcrLine> lines)
    {
        OcrLine[]? copy = null;
        for (int i = 0; i < lines.Count; i++)
        {
            var reordered = Apply(lines[i]);
            if (ReferenceEquals(reordered, lines[i])) continue;
            copy ??= lines.ToArray();
            copy[i] = reordered;
        }
        return copy ?? lines;
    }

    /// <summary>
    /// Reorders one recognized line. Its text is reordered; its per-character entries (when present) follow
    /// the same permutation, and its words are re-sequenced into the new order with their text reordered, so
    /// concatenating <see cref="OcrChar.Value"/> and the words still reproduce <see cref="OcrLine.Text"/>.
    /// Geometry is untouched — every glyph keeps the pixels it was read from. Lines with no right-to-left
    /// character are returned as the same instance.
    /// </summary>
    public static OcrLine Apply(OcrLine line)
    {
        if (!TryReorder(line.Text, out var display, out var order)) return line;

        return line with
        {
            Text = display,
            Characters = ReorderCharacters(line.Text, line.Characters, display, order),
            Words = ReorderWords(line.Text, line.Words, display, order),
        };
    }

    private static IReadOnlyList<OcrChar> ReorderCharacters(string logical, IReadOnlyList<OcrChar> characters, string display, int[] order)
    {
        if (characters.Count != logical.Length) return characters;
        for (int i = 0; i < logical.Length; i++)
        {
            if (characters[i].Value.Length != 1 || characters[i].Value[0] != logical[i]) return characters;
        }

        var result = new OcrChar[characters.Count];
        for (int k = 0; k < result.Length; k++)
        {
            var source = characters[order[k]];
            result[k] = source.Value[0] == display[k] ? source : source with { Value = display[k].ToString() };
        }
        return result;
    }

    private static IReadOnlyList<OcrWord> ReorderWords(string logical, IReadOnlyList<OcrWord> words, string display, int[] order)
    {
        if (words.Count == 0) return words;

        // Word i of the recognizer covers the i-th whitespace-delimited token of the logical string
        // (WordGeometry splits on exactly that rule).
        var wordOf = new int[logical.Length];
        var wordLength = new List<int>();
        int current = -1;
        for (int i = 0; i < logical.Length; i++)
        {
            if (char.IsWhiteSpace(logical[i])) { wordOf[i] = -1; current = -1; continue; }
            if (current < 0) { current = wordLength.Count; wordLength.Add(0); }
            wordOf[i] = current;
            wordLength[current]++;
        }

        if (wordLength.Count == words.Count)
        {
            var result = new List<OcrWord>(words.Count);
            bool clean = true;
            for (int a = 0; a < display.Length && clean;)
            {
                if (char.IsWhiteSpace(display[a])) { a++; continue; }
                int b = a;
                int word = wordOf[order[a]];
                while (b < display.Length && !char.IsWhiteSpace(display[b]))
                {
                    if (wordOf[order[b]] != word) { clean = false; break; }
                    b++;
                }
                if (!clean || word < 0 || b - a != wordLength[word]) { clean = false; break; }
                result.Add(words[word] with { Text = display[a..b] });
                a = b;
            }
            if (clean && result.Count == words.Count) return result;
        }

        // A token split across direction runs (rare): keep each word where it is, text reordered.
        return words.Select(w => w with { Text = GetDisplay(w.Text) }).ToArray();
    }

    /// <summary>
    /// Runs the algorithm. Returns false (and leaves the outputs meaningless) when the text holds no
    /// right-to-left character, in which case the display order is the input order.
    /// </summary>
    /// <param name="text">The text in recognizer output order.</param>
    /// <param name="display">The reordered text.</param>
    /// <param name="order">For each display position, the index of the input character placed there.</param>
    private static bool TryReorder(string text, out string display, out int[] order)
    {
        display = text;
        order = Array.Empty<int>();
        if (string.IsNullOrEmpty(text)) return false;

        int n = text.Length;
        var original = new BidiClass[n];
        bool anyRtl = false;
        for (int i = 0; i < n; i++)
        {
            original[i] = Classify(text[i]);
            anyRtl |= original[i] is BidiClass.R or BidiClass.AL;
        }
        if (!anyRtl) return false;

        // P2/P3: the first strong character sets the paragraph level.
        int baseLevel = 0;
        foreach (var c in original)
        {
            if (c == BidiClass.L) break;
            if (c is BidiClass.R or BidiClass.AL) { baseLevel = 1; break; }
        }
        var sos = baseLevel == 1 ? BidiClass.R : BidiClass.L;
        var cls = (BidiClass[])original.Clone();

        // W1: a non-spacing mark takes the class of the character before it.
        for (int i = 0; i < n; i++)
            if (cls[i] == BidiClass.NSM) cls[i] = i == 0 ? sos : cls[i - 1];

        // W2: a European number after Arabic letters is an Arabic number.
        var lastStrong = sos;
        for (int i = 0; i < n; i++)
        {
            if (cls[i] is BidiClass.L or BidiClass.R or BidiClass.AL) lastStrong = cls[i];
            else if (cls[i] == BidiClass.EN && lastStrong == BidiClass.AL) cls[i] = BidiClass.AN;
        }

        // W3
        for (int i = 0; i < n; i++)
            if (cls[i] == BidiClass.AL) cls[i] = BidiClass.R;

        // W4: a single separator between two numbers of the same kind joins them.
        for (int i = 1; i < n - 1; i++)
        {
            if (cls[i] == BidiClass.ES && cls[i - 1] == BidiClass.EN && cls[i + 1] == BidiClass.EN) cls[i] = BidiClass.EN;
            else if (cls[i] == BidiClass.CS && cls[i - 1] == cls[i + 1] && cls[i - 1] is BidiClass.EN or BidiClass.AN) cls[i] = cls[i - 1];
        }

        // W5: terminators (currency, percent) adjacent to a European number become part of it.
        for (int i = 0; i < n;)
        {
            if (cls[i] != BidiClass.ET) { i++; continue; }
            int j = i;
            while (j < n && cls[j] == BidiClass.ET) j++;
            if ((i > 0 && cls[i - 1] == BidiClass.EN) || (j < n && cls[j] == BidiClass.EN))
                for (int k = i; k < j; k++) cls[k] = BidiClass.EN;
            i = j;
        }

        // W6
        for (int i = 0; i < n; i++)
            if (cls[i] is BidiClass.ES or BidiClass.ET or BidiClass.CS) cls[i] = BidiClass.ON;

        // W7: a European number in a left-to-right context is left-to-right.
        lastStrong = sos;
        for (int i = 0; i < n; i++)
        {
            if (cls[i] is BidiClass.L or BidiClass.R) lastStrong = cls[i];
            else if (cls[i] == BidiClass.EN && lastStrong == BidiClass.L) cls[i] = BidiClass.L;
        }

        // N1/N2: neutrals between two runs of the same direction take it (numbers count as R); otherwise
        // they take the paragraph direction.
        for (int i = 0; i < n;)
        {
            if (cls[i] is not (BidiClass.WS or BidiClass.ON)) { i++; continue; }
            int j = i;
            while (j < n && cls[j] is BidiClass.WS or BidiClass.ON) j++;
            var before = i == 0 ? sos : StrongDirection(cls[i - 1]);
            var after = j == n ? sos : StrongDirection(cls[j]);
            var resolved = before == after ? before : sos;
            for (int k = i; k < j; k++) cls[k] = resolved;
            i = j;
        }

        // I1/I2
        var levels = new int[n];
        for (int i = 0; i < n; i++)
        {
            levels[i] = baseLevel == 0
                ? cls[i] switch { BidiClass.R => 1, BidiClass.AN or BidiClass.EN => 2, _ => 0 }
                : cls[i] switch { BidiClass.L or BidiClass.AN or BidiClass.EN => 2, _ => 1 };
        }

        // L1: trailing whitespace returns to the paragraph level.
        for (int i = n - 1; i >= 0 && original[i] == BidiClass.WS; i--) levels[i] = baseLevel;

        // L2: from the highest level down to the lowest odd one, reverse every run at that level or above.
        order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        var visualLevels = (int[])levels.Clone();
        int maxLevel = levels.Max();
        int minOdd = levels.Where(l => (l & 1) == 1).DefaultIfEmpty(maxLevel + 1).Min();
        for (int level = maxLevel; level >= minOdd; level--)
        {
            for (int i = 0; i < n;)
            {
                if (visualLevels[i] < level) { i++; continue; }
                int j = i;
                while (j < n && visualLevels[j] >= level) j++;
                Array.Reverse(order, i, j - i);
                Array.Reverse(visualLevels, i, j - i);
                i = j;
            }
        }

        // L4: mirror paired glyphs that ended up in a right-to-left run.
        var chars = new char[n];
        for (int k = 0; k < n; k++)
        {
            int source = order[k];
            chars[k] = (levels[source] & 1) == 1 ? Mirror(text[source]) : text[source];
        }
        display = new string(chars);
        return true;
    }

    private static BidiClass StrongDirection(BidiClass c) => c == BidiClass.L ? BidiClass.L : BidiClass.R;

    private static BidiClass Classify(char c)
    {
        if (c is >= '0' and <= '9') return BidiClass.EN;
        switch (c)
        {
            case '+' or '-' or '\u207A' or '\u207B' or '\u208A' or '\u208B' or '\u2212' or '\uFB29' or '\uFE62' or '\uFE63' or '\uFF0B' or '\uFF0D':
                return BidiClass.ES;
            case '#' or '$' or '%' or (>= '\u00A2' and <= '\u00A5') or '\u00B0' or '\u00B1' or '\u0609' or '\u060A' or '\u066A'
                or (>= '\u2030' and <= '\u2034') or (>= '\u20A0' and <= '\u20CF') or '\u212E' or '\u2213' or '\uFE5F'
                or '\uFE69' or '\uFE6A' or (>= '\uFF03' and <= '\uFF05') or '\uFFE0' or '\uFFE1' or '\uFFE5' or '\uFFE6':
                return BidiClass.ET;
            case ',' or '.' or '/' or ':' or '\u00A0' or '\u060C' or '\u202F' or '\u2044' or '\uFE50' or '\uFE52' or '\uFE55'
                or '\uFF0C' or '\uFF0E' or '\uFF0F' or '\uFF1A':
                return BidiClass.CS;
            case ' ' or '\t' or '\f' or '\u1680' or (>= '\u2000' and <= '\u200A') or '\u2028' or '\u205F' or '\u3000':
                return BidiClass.WS;
            case '\u00B2' or '\u00B3' or '\u00B9' or '\u2070' or (>= '\u2074' and <= '\u2079') or (>= '\u2080' and <= '\u2089')
                or (>= '\u06F0' and <= '\u06F9') or (>= '\uFF10' and <= '\uFF19'):
                return BidiClass.EN;
            case (>= '\u0660' and <= '\u0669') or '\u066B' or '\u066C' or (>= '\u0600' and <= '\u0605') or '\u06DD' or '\u08E2':
                return BidiClass.AN;
        }

        var category = char.GetUnicodeCategory(c);
        if (category is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark)
            return BidiClass.NSM;

        if (c is (>= '\u0590' and <= '\u05FF') or (>= '\u07C0' and <= '\u085F') or (>= '\uFB1D' and <= '\uFB4F'))
            return BidiClass.R;
        if (c is (>= '\u0600' and <= '\u07BF') or (>= '\u0860' and <= '\u08FF') or (>= '\uFB50' and <= '\uFDFF') or (>= '\uFE70' and <= '\uFEFE'))
            return BidiClass.AL;

        if (char.IsLetterOrDigit(c) || char.IsSurrogate(c) || category == System.Globalization.UnicodeCategory.SpacingCombiningMark)
            return BidiClass.L;
        return BidiClass.ON;
    }

    private static char Mirror(char c) => c switch
    {
        '(' => ')', ')' => '(',
        '[' => ']', ']' => '[',
        '{' => '}', '}' => '{',
        '<' => '>', '>' => '<',
        '\u00AB' => '\u00BB', '\u00BB' => '\u00AB',
        '\u2039' => '\u203A', '\u203A' => '\u2039',
        _ => c,
    };
}
