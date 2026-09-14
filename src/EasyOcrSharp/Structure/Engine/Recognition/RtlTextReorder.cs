using System.Text;

namespace EasyOcrSharp.Structure.Engine.Recognition;

/// <summary>
/// Converts right-to-left OCR output from logical order (the order the recognizer emits characters,
/// which for RTL scripts is visually reversed) to display order. Python PaddleX applies
/// <c>bidi.algorithm.get_display</c> to the arabic recognizer pack's outputs
/// (<c>text_recognition/predictor.py:189-196</c>); this is a pragmatic, dependency-free equivalent
/// tuned for single-line OCR strings rather than a full Unicode Bidirectional Algorithm implementation.
/// <para>
/// Algorithm: each character is classified as strong-RTL (Arabic/Hebrew script ranges), strong-LTR
/// (letters and digits — digits are kept LTR so numbers stay readable, mirroring the UBA's
/// left-to-right rendering of digit sequences), or neutral (spaces, punctuation, brackets). Neutrals
/// take the direction of their neighbors when both sides agree, otherwise the paragraph direction.
/// The string then splits into maximal same-direction runs; when the paragraph is predominantly RTL
/// the run order is reversed, characters inside every RTL run are reversed (with paired brackets
/// mirrored so they still face their content), and LTR runs (Latin words, digit sequences) are kept
/// intact.
/// </para>
/// <para>
/// Known simplification shared with codepoint-level bidi: reversing an RTL run moves combining marks
/// (e.g. Arabic harakat) in front of their base character. OCR dictionaries emit presentation-shaped
/// or bare letters, so this rarely matters in practice.
/// </para>
/// </summary>
internal static class RtlTextReorder
{
    private enum CharClass
    {
        Ltr,
        Rtl,
        Neutral,
    }

    /// <summary>
    /// Reorders <paramref name="logical"/> (recognizer output order) into visual display order.
    /// Strings containing no RTL characters are returned unchanged. Deterministic: the result depends
    /// only on the input string.
    /// </summary>
    /// <param name="logical">The recognized text in logical (emission) order.</param>
    /// <returns>The text in display order.</returns>
    public static string ApplyDisplayOrder(string logical)
    {
        if (string.IsNullOrEmpty(logical))
            return logical;

        int length = logical.Length;
        var classes = new CharClass[length];
        int rtlCount = 0, ltrCount = 0;
        for (int i = 0; i < length; i++)
        {
            CharClass cls = Classify(logical[i]);
            classes[i] = cls;
            if (cls == CharClass.Rtl) rtlCount++;
            else if (cls == CharClass.Ltr) ltrCount++;
        }

        // Nothing right-to-left — the logical order is already the display order.
        if (rtlCount == 0)
            return logical;

        // Paragraph direction: strong-character majority; a tie falls back to the first strong
        // character (the UBA's default base-direction rule).
        bool paragraphRtl = rtlCount != ltrCount
            ? rtlCount > ltrCount
            : FirstStrongIsRtl(classes);

        ResolveNeutrals(classes, paragraphRtl ? CharClass.Rtl : CharClass.Ltr);

        // Emit runs of consecutive same-direction characters. For an RTL paragraph the last logical
        // run is leftmost on screen, so runs are visited in reverse; RTL runs are always
        // character-reversed regardless of paragraph direction.
        var sb = new StringBuilder(length);
        if (paragraphRtl)
        {
            int end = length;
            while (end > 0)
            {
                int start = end - 1;
                while (start > 0 && classes[start - 1] == classes[end - 1])
                    start--;
                AppendRun(sb, logical, start, end - start, classes[start]);
                end = start;
            }
        }
        else
        {
            int start = 0;
            while (start < length)
            {
                int end = start + 1;
                while (end < length && classes[end] == classes[start])
                    end++;
                AppendRun(sb, logical, start, end - start, classes[start]);
                start = end;
            }
        }

        return sb.ToString();
    }

    private static CharClass Classify(char c)
    {
        // Digits first: Arabic-Indic digits (U+0660-0669, U+06F0-06F9) sit inside the Arabic block
        // but must stay in logical order — the UBA renders digit sequences left-to-right even inside
        // RTL text.
        if (char.IsDigit(c))
            return CharClass.Ltr;
        if (IsRtl(c))
            return CharClass.Rtl;
        if (char.IsLetter(c))
            return CharClass.Ltr;
        return CharClass.Neutral;
    }

    private static bool IsRtl(char c) =>
        (c >= '\u0590' && c <= '\u05FF')    // Hebrew
        || (c >= '\u0600' && c <= '\u06FF') // Arabic
        || (c >= '\u0750' && c <= '\u077F') // Arabic Supplement
        || (c >= '\u08A0' && c <= '\u08FF') // Arabic Extended-A
        || (c >= '\uFB50' && c <= '\uFDFF') // Arabic Presentation Forms-A
        || (c >= '\uFE70' && c <= '\uFEFF'); // Arabic Presentation Forms-B

    private static bool FirstStrongIsRtl(CharClass[] classes)
    {
        foreach (CharClass cls in classes)
        {
            if (cls != CharClass.Neutral)
                return cls == CharClass.Rtl;
        }
        return false;
    }

    /// <summary>
    /// Rewrites every <see cref="CharClass.Neutral"/> entry to a strong direction: neutrals bounded by
    /// the same direction on both sides take it; mixed or missing boundaries (string start/end) take
    /// the paragraph direction, matching the UBA's sos/eos convention.
    /// </summary>
    private static void ResolveNeutrals(CharClass[] classes, CharClass paragraph)
    {
        int length = classes.Length;
        for (int i = 0; i < length; i++)
        {
            if (classes[i] != CharClass.Neutral)
                continue;

            int segmentEnd = i;
            while (segmentEnd < length && classes[segmentEnd] == CharClass.Neutral)
                segmentEnd++;

            CharClass before = i > 0 ? classes[i - 1] : paragraph;
            CharClass after = segmentEnd < length ? classes[segmentEnd] : paragraph;
            CharClass resolved = before == after ? before : paragraph;
            for (int j = i; j < segmentEnd; j++)
                classes[j] = resolved;
            i = segmentEnd - 1;
        }
    }

    private static void AppendRun(StringBuilder sb, string text, int start, int length, CharClass direction)
    {
        if (direction == CharClass.Rtl)
            AppendReversed(sb, text, start, length);
        else
            sb.Append(text, start, length);
    }

    /// <summary>
    /// Appends <paramref name="length"/> characters starting at <paramref name="start"/> in reverse,
    /// keeping surrogate pairs intact and mirroring paired brackets so they still face their content
    /// after reversal.
    /// </summary>
    private static void AppendReversed(StringBuilder sb, string text, int start, int length)
    {
        for (int i = start + length - 1; i >= start; i--)
        {
            char c = text[i];
            if (char.IsLowSurrogate(c) && i > start && char.IsHighSurrogate(text[i - 1]))
            {
                sb.Append(text[i - 1]).Append(c);
                i--;
            }
            else
            {
                sb.Append(Mirror(c));
            }
        }
    }

    private static char Mirror(char c) => c switch
    {
        '(' => ')',
        ')' => '(',
        '[' => ']',
        ']' => '[',
        '{' => '}',
        '}' => '{',
        '<' => '>',
        '>' => '<',
        _ => c,
    };
}
