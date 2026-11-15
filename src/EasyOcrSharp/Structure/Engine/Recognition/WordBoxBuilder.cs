using EasyOcrSharp.Structure.Engine.Geometry;
using EasyOcrSharp.Structure.Engine.Models;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;
using OcrWord = EasyOcrSharp.Models.OcrWord;

namespace EasyOcrSharp.Structure.Engine.Recognition;

/// <summary>
/// Builds word-level boxes (<see cref="RecognitionOptions.ReturnWordBoxes"/>, PaddleOCR 3.x's
/// <c>return_word_box</c>) from the CTC timesteps of a line's emitted characters.
/// <para>
/// Words are split at whitespace tokens. Han, Kana and CJK punctuation characters are each their own word
/// (Hangul is space-delimited, so Korean splits at spaces like Latin text). Following PaddleOCR's
/// <c>cal_ocr_word_box</c>, a non-CJK word spans from its first character's first timestep to its last
/// character's last timestep, while a CJK character is centred on its timestep with the line's average CJK
/// character width. Crop-space intervals are then mapped back through the 180° flip, the crop padding, the
/// 90° vertical-text rotation and the rectification quad onto the page.
/// </para>
/// </summary>
internal static class WordBoxBuilder
{
    /// <summary>
    /// Builds the page-space words of one line.
    /// </summary>
    /// <param name="reading">The line's reading, with <see cref="RecognizedText.Characters"/> populated.</param>
    /// <param name="geometry">How the rectified crop maps back onto the page.</param>
    /// <param name="cropWidth">Width of the crop that was recognized (padding included).</param>
    /// <param name="cropHeight">Height of the crop that was recognized (padding included).</param>
    /// <param name="cropPadding">The white border added around the rectified crop (<see cref="RecognitionOptions.CropPadding"/>).</param>
    /// <param name="flipped">True when the recognized crop was the 180°-rotated one.</param>
    /// <returns>The words in recognition order; empty when the reading carries no characters.</returns>
    public static IReadOnlyList<OcrWord> Build(
        RecognizedText reading, CropGeometry geometry, int cropWidth, int cropHeight, int cropPadding, bool flipped)
    {
        if (reading.Characters is not { Count: > 0 } characters) return Array.Empty<OcrWord>();

        var spans = SplitWords(characters, reading.StepWidth, cropWidth);
        if (spans.Count == 0) return Array.Empty<OcrWord>();

        int padding = Math.Max(0, cropPadding);
        double innerWidth = Math.Max(0, cropWidth - 2 * padding);
        double innerHeight = Math.Max(0, cropHeight - 2 * padding);
        double top = Math.Min(padding, cropHeight / 2.0);
        double bottom = cropHeight - top;

        var words = new OcrWord[spans.Count];
        for (int k = 0; k < spans.Count; k++)
        {
            var (text, confidence, start, end) = spans[k];
            var polygon = new[]
            {
                Map(start, top), Map(end, top), Map(end, bottom), Map(start, bottom),
            };
            words[k] = new OcrWord
            {
                Text = reading.RightToLeft ? RtlTextReorder.ApplyDisplayOrder(text) : text,
                Confidence = confidence,
                BoundingPolygon = polygon,
                BoundingBox = OcrBoundingBox.FromPoints(polygon),
            };
        }
        return words;

        OcrPoint Map(double x, double y)
        {
            // Undo the 180° flip, then the padding, then the rectification (incl. the vertical rotation).
            if (flipped)
            {
                x = cropWidth - x;
                y = cropHeight - y;
            }
            x = Math.Clamp(x - padding, 0, innerWidth);
            y = Math.Clamp(y - padding, 0, innerHeight);
            return geometry.MapCrop(x, y);
        }
    }

    /// <summary>
    /// Splits emitted characters into words and measures each word's horizontal interval on the crop.
    /// </summary>
    /// <param name="characters">The emitted characters, in emission order.</param>
    /// <param name="stepWidth">Crop pixels covered by one CTC timestep.</param>
    /// <param name="contentWidth">The crop width; intervals are clamped to <c>[0, contentWidth]</c>.</param>
    /// <returns>Each word's text, mean character probability and crop-space <c>[Start, End]</c> interval.</returns>
    public static List<(string Text, double Confidence, double Start, double End)> SplitWords(
        IReadOnlyList<CtcCharacter> characters, double stepWidth, double contentWidth)
    {
        var words = new List<(string Text, double Confidence, double Start, double End)>();
        if (characters.Count == 0 || stepWidth <= 0 || contentWidth <= 0) return words;

        double cjkWidth = EstimateCjkCharWidth(characters, stepWidth, contentWidth);
        int wordStart = -1;
        for (int i = 0; i <= characters.Count; i++)
        {
            bool atEnd = i == characters.Count;
            bool space = !atEnd && string.IsNullOrWhiteSpace(characters[i].Token);
            bool cjk = !atEnd && !space && IsCjk(characters[i].Token);

            if ((atEnd || space || cjk) && wordStart >= 0)
            {
                words.Add(MeasureRun(characters, wordStart, i - 1, stepWidth, contentWidth));
                wordStart = -1;
            }
            if (atEnd || space) continue;

            if (cjk)
            {
                var ch = characters[i];
                double center = ch.PixelGeometry?.Center ?? (ch.FirstStep + 0.5) * stepWidth;
                words.Add((ch.Token, ch.Probability,
                    Math.Clamp(center - cjkWidth / 2, 0, contentWidth),
                    Math.Clamp(center + cjkWidth / 2, 0, contentWidth)));
                continue;
            }

            if (wordStart < 0) wordStart = i;
        }
        return words;
    }

    /// <summary>
    /// Returns a copy of <paramref name="words"/> with every polygon point passed through
    /// <paramref name="map"/> (and the boxes recomputed) — used when lines are translated or rotated into
    /// another frame. Returns the input unchanged when it is empty.
    /// </summary>
    /// <param name="words">The words to transform.</param>
    /// <param name="map">The point transform.</param>
    /// <returns>The transformed words.</returns>
    public static IReadOnlyList<OcrWord> Transform(IReadOnlyList<OcrWord> words, Func<OcrPoint, OcrPoint> map)
    {
        if (words.Count == 0) return words;
        var result = new OcrWord[words.Count];
        for (int i = 0; i < words.Count; i++)
        {
            var source = words[i].BoundingPolygon;
            var polygon = new OcrPoint[source.Count];
            for (int p = 0; p < polygon.Length; p++) polygon[p] = map(source[p]);
            result[i] = words[i] with { BoundingPolygon = polygon, BoundingBox = OcrBoundingBox.FromPoints(polygon) };
        }
        return result;
    }

    /// <summary>
    /// True for tokens that form a word on their own: Han ideographs (incl. extensions and compatibility
    /// ideographs), Hiragana/Katakana, Bopomofo, CJK symbols and punctuation, and fullwidth forms.
    /// </summary>
    internal static bool IsCjk(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        int cp = char.IsHighSurrogate(token[0]) && token.Length > 1 && char.IsLowSurrogate(token[1])
            ? char.ConvertToUtf32(token[0], token[1])
            : token[0];
        return cp is (>= 0x2E80 and <= 0x2FDF)     // CJK radicals, Kangxi radicals
            or (>= 0x3000 and <= 0x312F)           // CJK symbols/punctuation, Hiragana, Katakana, Bopomofo
            or (>= 0x31A0 and <= 0x31FF)           // Bopomofo ext, CJK strokes, Katakana phonetic ext
            or (>= 0x3400 and <= 0x4DBF)           // CJK Extension A
            or (>= 0x4E00 and <= 0x9FFF)           // CJK Unified Ideographs
            or (>= 0xF900 and <= 0xFAFF)           // CJK Compatibility Ideographs
            or (>= 0xFF01 and <= 0xFF65)           // Fullwidth ASCII variants and halfwidth CJK punctuation
            or (>= 0x20000 and <= 0x3134F);        // CJK Extensions B–G
    }

    /// <summary>
    /// PaddleOCR's average CJK character width: for every run of at least two consecutive CJK characters,
    /// <c>(lastStep − firstStep + 1) · stepWidth / (count − 1)</c>, averaged; falling back to the crop width
    /// divided by the character count when no such run exists.
    /// </summary>
    private static double EstimateCjkCharWidth(IReadOnlyList<CtcCharacter> characters, double stepWidth, double contentWidth)
    {
        double sum = 0;
        int runs = 0;
        int runStart = -1;
        for (int i = 0; i <= characters.Count; i++)
        {
            bool cjk = i < characters.Count && IsCjk(characters[i].Token);
            if (cjk)
            {
                if (runStart < 0) runStart = i;
                continue;
            }
            if (runStart >= 0 && i - runStart > 1)
            {
                var first = characters[runStart];
                var last = characters[i - 1];
                double span = first.PixelGeometry is null && last.PixelGeometry is null
                    ? (last.FirstStep - first.FirstStep + 1) * stepWidth
                    : (last.PixelGeometry?.Start ?? last.FirstStep * stepWidth)
                        - (first.PixelGeometry?.Start ?? first.FirstStep * stepWidth)
                        + (last.PixelGeometry?.StepWidth ?? stepWidth);
                sum += span / (i - runStart - 1);
                runs++;
            }
            runStart = -1;
        }
        return runs > 0 ? sum / runs : contentWidth / characters.Count;
    }

    /// <summary>
    /// Measures a run of non-CJK characters <c>[first, last]</c> as one word.
    /// </summary>
    private static (string Text, double Confidence, double Start, double End) MeasureRun(
        IReadOnlyList<CtcCharacter> characters, int first, int last, double stepWidth, double contentWidth)
    {
        var text = new System.Text.StringBuilder();
        double probabilitySum = 0;
        for (int i = first; i <= last; i++)
        {
            text.Append(characters[i].Token);
            probabilitySum += characters[i].Probability;
        }
        double start = Math.Clamp(characters[first].PixelGeometry?.Start ?? characters[first].FirstStep * stepWidth, 0, contentWidth);
        double end = Math.Clamp(characters[last].PixelGeometry?.End ?? (characters[last].LastStep + 1) * stepWidth, start, contentWidth);
        return (text.ToString(), probabilitySum / (last - first + 1), start, end);
    }
}
