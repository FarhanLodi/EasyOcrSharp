using System.Globalization;
using System.Text;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Pdf.Internal;

/// <summary>One word rebuilt from a page's embedded characters, in rendered-page pixels.</summary>
/// <param name="Text">The word, never empty and never containing whitespace.</param>
/// <param name="Glyphs">The characters it was built from, in content (reading) order.</param>
/// <param name="Box">The union of the glyph boxes.</param>
internal sealed record EmbeddedWord(string Text, IReadOnlyList<PdfTextChar> Glyphs, OcrBoundingBox Box);

/// <summary>One line rebuilt from a page's embedded characters, in rendered-page pixels.</summary>
/// <param name="Words">The line's words in content (reading) order; never empty.</param>
/// <param name="Box">The union of the word boxes.</param>
internal sealed record EmbeddedLine(IReadOnlyList<EmbeddedWord> Words, OcrBoundingBox Box)
{
    /// <summary>The words joined by single spaces.</summary>
    public string Text => Words.Count == 1 ? Words[0].Text : string.Join(' ', Words.Select(w => w.Text));
}

/// <summary>
/// Turns a PDF page's embedded characters into lines and words, decides whether the text layer is trustworthy
/// enough to use instead of OCR, and shapes the accepted lines into the <see cref="OcrResult"/> the OCR path
/// would have produced for the same <see cref="RecognitionOptions"/>. Pure managed code — PDFium access lives in
/// <see cref="PdfRasterizer"/> — so every rule here is unit-testable with synthetic character boxes.
/// </summary>
internal static class EmbeddedTextLayer
{
    /// <summary>Minimum number of non-whitespace characters for the text layer to be used.</summary>
    internal const int MinCharacters = 20;

    /// <summary>Maximum share of replacement, control or private-use characters (exclusive).</summary>
    internal const double MaxInvalidRatio = 0.05;

    /// <summary>Minimum share of letters, combining marks or digits among non-whitespace characters.</summary>
    internal const double MinLetterOrDigitRatio = 0.60;

    /// <summary>In <see cref="PdfTextLayerMode.Auto"/>, the minimum share of the page area the text line boxes must cover.</summary>
    /// <remarks>
    /// A sparse born-digital invoice covers about 2% of its page with line boxes, a report page 3%, a dense article
    /// 20%. A scanned page carrying one digital 10 pt header line covers roughly 0.4–0.6%, so 1% separates the two.
    /// </remarks>
    internal const double MinAutoCoverage = 0.01;

    /// <summary>A horizontal gap wider than this many em heights starts a new word.</summary>
    internal const double WordGapFactor = 0.3;

    /// <summary>
    /// A gap must also exceed this multiple of the line's median letter gap to start a word. That keeps letter-spaced
    /// (tracked) text, whose letter gaps exceed 0.3 em, from being split into single letters.
    /// </summary>
    internal const double TrackingGapFactor = 1.5;

    /// <summary>Minimum number of letter gaps on a line before its median spacing is trusted.</summary>
    internal const int MinTrackingSamples = 3;

    /// <summary>A horizontal gap wider than this many em heights starts a new line, as OCR detection splits columns.</summary>
    internal const double ColumnGapFactor = 2.0;

    /// <summary>Minimum vertical overlap, relative to the shorter box, for a character to join the current line.</summary>
    internal const double MinVerticalOverlap = 0.5;

    /// <summary>Maximum baseline shift, in em heights, for a character to join the current line.</summary>
    internal const double MaxBaselineShift = 0.5;

    /// <summary>
    /// The separator between the lines of a merged paragraph in <see cref="OcrLine.Characters"/> — the same
    /// geometry-less entry the OCR path uses, so concatenating the characters reproduces the paragraph text.
    /// </summary>
    private static readonly OcrChar NewlineChar = new() { Value = "\n", Confidence = 0 };

    /// <summary>
    /// Returns the page's lines when its embedded text should be used for <paramref name="mode"/>; otherwise
    /// <see langword="null"/>, meaning OCR the page. Characters whose box centre lies off the rendered page
    /// (clipped by the crop box) are disregarded — OCR could not see them either.
    /// </summary>
    public static List<EmbeddedLine>? SelectLines(IReadOnlyList<PdfTextChar> chars, PdfTextLayerMode mode, int pageWidth, int pageHeight)
    {
        if (mode == PdfTextLayerMode.Ignore) return null;

        var visible = OnPage(chars, pageWidth, pageHeight);
        if (!PassesQualityGate(visible)) return null;

        var lines = BuildLines(visible);
        if (mode == PdfTextLayerMode.Auto && CoverageRatio(lines, pageWidth, pageHeight) < MinAutoCoverage) return null;
        return lines;
    }

    /// <summary>
    /// The quality gate: at least <see cref="MinCharacters"/> non-whitespace characters, fewer than
    /// <see cref="MaxInvalidRatio"/> of them invalid, and at least <see cref="MinLetterOrDigitRatio"/> letters,
    /// combining marks or digits. A broken <c>/ToUnicode</c> map typically yields U+FFFD, control or private-use
    /// code points, or a soup of symbols, and fails here.
    /// </summary>
    /// <remarks>
    /// Combining marks count as letters: Devanagari, Bengali, Thai and vowelled Arabic or Hebrew carry a large
    /// share of them (vowel signs, viramas, harakat), and counting them against the ratio would send perfectly
    /// good born-digital pages in those scripts to OCR.
    /// </remarks>
    public static bool PassesQualityGate(IReadOnlyList<PdfTextChar> chars)
    {
        int total = 0, invalid = 0, letterLike = 0;
        foreach (var c in chars)
        {
            if (char.IsWhiteSpace(c.Value)) continue;
            total++;
            if (IsInvalid(c.Value)) invalid++;
            else if (IsLetterLike(c.Value)) letterLike++;
        }

        return total >= MinCharacters
            && invalid < total * MaxInvalidRatio
            && letterLike >= total * MinLetterOrDigitRatio;
    }

    /// <summary>
    /// The share of the page area covered by text line boxes (overlaps are not subtracted). Line boxes are used
    /// rather than tight glyph boxes: glyph ink covers only about 1.3–1.6% of an ordinary born-digital invoice or
    /// report page, which would fall below the <see cref="PdfTextLayerMode.Auto"/> threshold.
    /// </summary>
    public static double CoverageRatio(IReadOnlyList<EmbeddedLine> lines, int pageWidth, int pageHeight)
    {
        if (pageWidth <= 0 || pageHeight <= 0) return 0;
        double area = 0;
        foreach (var line in lines)
            area += line.Box.Width * line.Box.Height;
        return area / ((double)pageWidth * pageHeight);
    }

    /// <summary>
    /// Groups characters (in content-stream order) into lines by vertical overlap and baseline, starting a new line
    /// when the text jumps away from the previous character or across a gap wider than
    /// <see cref="ColumnGapFactor"/> em — as OCR detection splits columns. Within a line, a word starts at
    /// whitespace; a line with no whitespace at all is split at gaps wider than both <see cref="WordGapFactor"/> em
    /// and <see cref="TrackingGapFactor"/> times its median letter gap.
    /// </summary>
    /// <remarks>
    /// Gaps are measured in the direction the text is moving, so a right-to-left run — Arabic or Hebrew, which
    /// PDFium reports in logical order, each character to the left of the one before — forms lines and words
    /// exactly as a left-to-right one does, and keeps its logical (searchable) character order.
    /// </remarks>
    public static List<EmbeddedLine> BuildLines(IReadOnlyList<PdfTextChar> chars)
    {
        var lines = new List<EmbeddedLine>();
        var glyphs = new List<(PdfTextChar Char, bool SpaceBefore)>();
        double lineTop = 0, lineBottom = 0;
        bool pendingSpace = false;

        foreach (var c in chars)
        {
            if (char.IsWhiteSpace(c.Value) || char.IsControl(c.Value))
            {
                // PDFium reports both real spaces and generated CR/LF here; geometry decides line breaks, so any
                // whitespace only marks a word boundary.
                if (glyphs.Count > 0) pendingSpace = true;
                continue;
            }

            if (glyphs.Count > 0 && !JoinsLine(c, glyphs[^1].Char, lineTop, lineBottom))
            {
                AddLine(lines, glyphs);
                glyphs.Clear();
                pendingSpace = false;
            }

            if (glyphs.Count == 0)
            {
                lineTop = c.Top;
                lineBottom = c.Bottom;
            }
            else
            {
                lineTop = Math.Min(lineTop, c.Top);
                lineBottom = Math.Max(lineBottom, c.Bottom);
            }

            glyphs.Add((c, pendingSpace));
            pendingSpace = false;
        }

        AddLine(lines, glyphs);
        return lines;
    }

    /// <summary>
    /// Builds the <see cref="OcrResult"/> for a page read from its embedded text layer, shaped by the same
    /// <see cref="RecognitionOptions"/> the OCR path would have used.
    /// </summary>
    public static OcrResult CreateResult(
        IReadOnlyList<EmbeddedLine> lines,
        RecognitionOptions? options,
        IReadOnlyList<string> languages,
        int pixelWidth,
        int pixelHeight,
        TimeSpan duration)
    {
        var ocrLines = ToOcrLines(lines, options ?? RecognitionOptions.Default, languages, pixelWidth, pixelHeight);
        return new OcrResult
        {
            FullText = BuildFullText(ocrLines),
            Lines = ocrLines,
            Languages = languages,
            Duration = duration,
            SourceWidth = pixelWidth,
            SourceHeight = pixelHeight,
        };
    }

    /// <summary>
    /// Converts the page's lines to <see cref="OcrLine"/>s, honouring <see cref="RecognitionOptions.Region"/>
    /// (only characters centred inside it are kept), <see cref="RecognitionOptions.Grouping"/> (one result per
    /// word, per line, or per paragraph — the latter through the same <c>ParagraphGrouper</c> the OCR path uses)
    /// and <see cref="RecognitionOptions.WordLevelDetail"/>. Lines and paragraphs keep the document's content
    /// order, which for a born-digital PDF is the author's reading order.
    /// </summary>
    internal static IReadOnlyList<OcrLine> ToOcrLines(
        IReadOnlyList<EmbeddedLine> lines,
        RecognitionOptions options,
        IReadOnlyList<string> languages,
        int pixelWidth,
        int pixelHeight)
    {
        if (options.Region is { } region)
            lines = Clip(lines, region.Resolve(pixelWidth, pixelHeight));

        var detail = options.WordLevelDetail;
        switch (options.Grouping)
        {
            case TextGrouping.Word:
            {
                var words = new List<OcrLine>();
                foreach (var line in lines)
                {
                    foreach (var word in line.Words)
                        words.Add(ToOcrLine(word, detail));
                }
                return words;
            }

            case TextGrouping.Paragraph:
            {
                var source = lines.Select(l => ToOcrLine(l, detail)).ToList();
                bool rightToLeft = ScriptDirection.IsRightToLeft(options.ReadingDirection, languages);
                var paragraphs = ParagraphGrouper.Merge(source, options.GroupingOptions, rightToLeft);
                return OrderAndReattach(paragraphs, source, detail);
            }

            default:
                return lines.Select(l => ToOcrLine(l, detail)).ToList();
        }
    }

    /// <summary>
    /// True for a replacement character, a Unicode noncharacter, a (non-whitespace) control or a private-use
    /// character — what a font without a usable <c>/ToUnicode</c> map extracts as.
    /// </summary>
    internal static bool IsInvalid(char ch)
        => ch is '�' or '￾' or '￿'
        || (char.IsControl(ch) && !char.IsWhiteSpace(ch))
        || char.GetUnicodeCategory(ch) == UnicodeCategory.PrivateUse;

    /// <summary>
    /// Normalizes a character as PDFium reports it. PDFium marks a hyphen that ends a line inside a hyphenated
    /// word with U+0002 rather than the hyphen it draws; it is restored here so the line reads as it looks, and
    /// so the marker is neither dropped as a control character nor counted against the quality gate.
    /// </summary>
    internal static char NormalizeCharacter(char ch) => ch == '\u0002' ? '-' : ch;

    private static bool IsLetterLike(char ch)
    {
        if (char.IsLetterOrDigit(ch)) return true;
        var category = char.GetUnicodeCategory(ch);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }

    /// <summary>The characters a renderer would actually show: centred on the page, with a box of some size.</summary>
    private static IReadOnlyList<PdfTextChar> OnPage(IReadOnlyList<PdfTextChar> chars, int pageWidth, int pageHeight)
    {
        bool Visible(in PdfTextChar c)
        {
            if (char.IsWhiteSpace(c.Value)) return true; // positions of spaces do not matter; keep word boundaries
            if (c.Right <= c.Left && c.Bottom <= c.Top) return false;
            double x = c.CenterX, y = c.CenterY;
            return x >= 0 && y >= 0 && x <= pageWidth && y <= pageHeight;
        }

        bool all = true;
        foreach (var c in chars)
        {
            if (!Visible(c))
            {
                all = false;
                break;
            }
        }
        if (all) return chars;

        var kept = new List<PdfTextChar>(chars.Count);
        foreach (var c in chars)
        {
            if (Visible(c)) kept.Add(c);
        }
        return kept;
    }

    /// <summary>
    /// Closes a line, splitting it into words. A line that carries any whitespace — spaces in the content stream,
    /// or the ones PDFium generates itself wherever positioning leaves a word-sized gap — is split at that
    /// whitespace only. Glyph boxes are tight ink boxes, so a glyph with wide side bearings (<c>1</c> before
    /// <c>.</c> is about 0.28 em of empty ink in Helvetica) would otherwise sit right at the gap threshold and
    /// break <c>1.2</c> or <c>14</c> apart. Only a line without any whitespace at all falls back to gaps, measured
    /// against the line's largest em so one small glyph cannot shrink the threshold, and lifted above the line's
    /// median letter gap so letter-spaced text is not split into letters.
    /// </summary>
    private static void AddLine(List<EmbeddedLine> lines, List<(PdfTextChar Char, bool SpaceBefore)> glyphs)
    {
        if (glyphs.Count == 0) return;

        bool explicitSpaces = false;
        double lineEm = 0;
        foreach (var (c, spaceBefore) in glyphs)
        {
            explicitSpaces |= spaceBefore;
            lineEm = Math.Max(lineEm, c.EmHeight);
        }

        double threshold = double.PositiveInfinity;
        if (!explicitSpaces)
        {
            var letterGaps = new List<double>(glyphs.Count);
            for (int i = 1; i < glyphs.Count; i++)
                letterGaps.Add(Gap(glyphs[i - 1].Char, glyphs[i].Char));
            double typicalGap = letterGaps.Count >= MinTrackingSamples ? Median(letterGaps) : 0;
            threshold = Math.Max(WordGapFactor * lineEm, TrackingGapFactor * typicalGap);
        }

        var words = new List<EmbeddedWord>();
        var current = new List<PdfTextChar> { glyphs[0].Char };
        for (int i = 1; i < glyphs.Count; i++)
        {
            var (c, spaceBefore) = glyphs[i];
            if (spaceBefore || Gap(glyphs[i - 1].Char, c) > threshold)
            {
                words.Add(MakeWord(current));
                current = new List<PdfTextChar>();
            }
            current.Add(c);
        }
        words.Add(MakeWord(current));

        lines.Add(MakeLine(words));
    }

    private static EmbeddedWord MakeWord(List<PdfTextChar> glyphs)
    {
        var text = new StringBuilder(glyphs.Count);
        double left = double.PositiveInfinity, top = double.PositiveInfinity;
        double right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        foreach (var g in glyphs)
        {
            text.Append(g.Value);
            left = Math.Min(left, g.Left);
            top = Math.Min(top, g.Top);
            right = Math.Max(right, g.Right);
            bottom = Math.Max(bottom, g.Bottom);
        }
        return new EmbeddedWord(text.ToString(), glyphs, new OcrBoundingBox(left, top, right, bottom));
    }

    private static EmbeddedLine MakeLine(List<EmbeddedWord> words)
    {
        double left = double.PositiveInfinity, top = double.PositiveInfinity;
        double right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        foreach (var w in words)
        {
            left = Math.Min(left, w.Box.MinX);
            top = Math.Min(top, w.Box.MinY);
            right = Math.Max(right, w.Box.MaxX);
            bottom = Math.Max(bottom, w.Box.MaxY);
        }
        return new EmbeddedLine(words, new OcrBoundingBox(left, top, right, bottom));
    }

    /// <summary>
    /// The gap between two consecutive characters, measured in the direction the text is moving: left to right
    /// when <paramref name="c"/> lies right of <paramref name="prev"/>, right to left otherwise. Negative when
    /// the boxes overlap (kerning, combining marks).
    /// </summary>
    private static double Gap(in PdfTextChar prev, in PdfTextChar c)
        => c.CenterX >= prev.CenterX ? c.Left - prev.Right : prev.Left - c.Right;

    private static double Median(List<double> values)
    {
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }

    private static bool JoinsLine(in PdfTextChar c, in PdfTextChar prev, double lineTop, double lineBottom)
    {
        double em = Math.Max(c.EmHeight, prev.EmHeight);
        double gap = Gap(prev, c);
        if (gap < -em || gap > ColumnGapFactor * em) return false;

        // A glyph nested in the line band (or containing it) always joins: punctuation, quotes, a capital after a quote.
        bool nested = (c.Top >= lineTop && c.Bottom <= lineBottom) || (c.Top <= lineTop && c.Bottom >= lineBottom);
        if (nested) return true;

        double lineHeight = Math.Max(1.0, lineBottom - lineTop);
        double charHeight = Math.Max(1.0, c.Bottom - c.Top);
        double overlap = Math.Min(lineBottom, c.Bottom) - Math.Max(lineTop, c.Top);
        bool overlaps = overlap >= MinVerticalOverlap * Math.Min(lineHeight, charHeight);
        bool sameBaseline = Math.Abs(c.Bottom - prev.Bottom) <= MaxBaselineShift * em
            || Math.Abs(c.Bottom - lineBottom) <= MaxBaselineShift * em;
        return overlaps && sameBaseline;
    }

    /// <summary>Keeps only the characters centred inside <paramref name="region"/>, rebuilding the boxes that shrink.</summary>
    private static IReadOnlyList<EmbeddedLine> Clip(IReadOnlyList<EmbeddedLine> lines, (int X, int Y, int Width, int Height) region)
    {
        var clipped = new List<EmbeddedLine>(lines.Count);
        if (region.Width <= 0 || region.Height <= 0) return clipped;

        bool Inside(in PdfTextChar c)
            => c.CenterX >= region.X && c.CenterX <= region.X + region.Width
            && c.CenterY >= region.Y && c.CenterY <= region.Y + region.Height;

        foreach (var line in lines)
        {
            var words = new List<EmbeddedWord>(line.Words.Count);
            bool changed = false;
            foreach (var word in line.Words)
            {
                var glyphs = new List<PdfTextChar>(word.Glyphs.Count);
                foreach (var g in word.Glyphs)
                {
                    if (Inside(g)) glyphs.Add(g);
                }

                if (glyphs.Count == word.Glyphs.Count)
                {
                    words.Add(word);
                }
                else
                {
                    changed = true;
                    if (glyphs.Count > 0) words.Add(MakeWord(glyphs));
                }
            }

            if (!changed) clipped.Add(line);
            else if (words.Count > 0) clipped.Add(MakeLine(words));
        }
        return clipped;
    }

    private static OcrLine ToOcrLine(EmbeddedLine line, WordLevelDetail detail) => new()
    {
        Text = line.Text,
        Confidence = 1.0,
        BoundingPolygon = Rectangle(line.Box),
        BoundingBox = line.Box,
        Words = detail == WordLevelDetail.None ? Array.Empty<OcrWord>() : line.Words.Select(ToOcrWord).ToArray(),
        Characters = detail == WordLevelDetail.Characters ? LineCharacters(line) : Array.Empty<OcrChar>(),
    };

    /// <summary>One word as a result of its own — <see cref="TextGrouping.Word"/>, EasyOCR's raw per-box output.</summary>
    private static OcrLine ToOcrLine(EmbeddedWord word, WordLevelDetail detail) => new()
    {
        Text = word.Text,
        Confidence = 1.0,
        BoundingPolygon = Rectangle(word.Box),
        BoundingBox = word.Box,
        Words = detail == WordLevelDetail.None ? Array.Empty<OcrWord>() : new[] { ToOcrWord(word) },
        Characters = detail == WordLevelDetail.Characters ? word.Glyphs.Select(ToOcrChar).ToArray() : Array.Empty<OcrChar>(),
    };

    private static OcrWord ToOcrWord(EmbeddedWord word) => new()
    {
        Text = word.Text,
        Confidence = 1.0,
        BoundingPolygon = Rectangle(word.Box),
        BoundingBox = word.Box,
    };

    private static OcrChar ToOcrChar(PdfTextChar glyph)
    {
        var box = new OcrBoundingBox(glyph.Left, glyph.Top, glyph.Right, glyph.Bottom);
        return new OcrChar
        {
            Value = glyph.Value.ToString(),
            Confidence = 1.0,
            BoundingPolygon = Rectangle(box),
            BoundingBox = box,
        };
    }

    /// <summary>
    /// The line's characters, with one space entry between consecutive words spanning the gap between them, so
    /// concatenating <see cref="OcrChar.Value"/> reproduces the line text exactly as the OCR path's does.
    /// </summary>
    private static OcrChar[] LineCharacters(EmbeddedLine line)
    {
        var chars = new List<OcrChar>();
        for (int i = 0; i < line.Words.Count; i++)
        {
            var word = line.Words[i];
            if (i > 0)
            {
                var prev = line.Words[i - 1].Box;
                var next = word.Box;
                bool forward = next.CenterX >= prev.CenterX;
                double a = forward ? prev.MaxX : next.MaxX;
                double b = forward ? next.MinX : prev.MinX;
                var gap = new OcrBoundingBox(Math.Min(a, b), line.Box.MinY, Math.Max(a, b), line.Box.MaxY);
                chars.Add(new OcrChar { Value = " ", Confidence = 1.0, BoundingPolygon = Rectangle(gap), BoundingBox = gap });
            }
            foreach (var g in word.Glyphs)
                chars.Add(ToOcrChar(g));
        }
        return chars.ToArray();
    }

    /// <summary>
    /// Restores content order to merged paragraphs — <c>ParagraphGrouper</c> emits them top to bottom, which on a
    /// multi-column page interleaves the columns — and, when word geometry was requested, stitches each merged
    /// paragraph's words and characters back on from the lines it was built from (the grouper drops them). A
    /// paragraph's segments are matched to source lines by text and containment, each line claimed once.
    /// </summary>
    private static IReadOnlyList<OcrLine> OrderAndReattach(List<OcrLine> paragraphs, List<OcrLine> source, WordLevelDetail detail)
    {
        var claimed = new bool[source.Count];
        var keyed = new List<(int Order, int Position, OcrLine Paragraph)>(paragraphs.Count);
        for (int p = 0; p < paragraphs.Count; p++)
        {
            var paragraph = paragraphs[p];
            int single = IndexOfReference(source, paragraph);
            if (single >= 0)
            {
                claimed[single] = true;
                keyed.Add((single, p, paragraph));
                continue;
            }

            var parts = new List<int>();
            foreach (var segment in paragraph.Text.Split('\n'))
            {
                int match = -1;
                for (int i = 0; i < source.Count; i++)
                {
                    if (claimed[i] || !string.Equals(source[i].Text, segment, StringComparison.Ordinal)) continue;
                    if (!Contains(paragraph.BoundingBox, source[i].BoundingBox)) continue;
                    match = i;
                    break;
                }
                if (match < 0)
                {
                    parts.Clear();
                    break;
                }
                claimed[match] = true;
                parts.Add(match);
            }

            if (parts.Count == 0)
            {
                // Unmatched (never expected): keep the grouper's position after everything that did match.
                keyed.Add((int.MaxValue, p, paragraph));
                continue;
            }

            if (detail != WordLevelDetail.None)
            {
                var words = new List<OcrWord>();
                var characters = new List<OcrChar>();
                for (int i = 0; i < parts.Count; i++)
                {
                    if (i > 0 && detail == WordLevelDetail.Characters) characters.Add(NewlineChar);
                    words.AddRange(source[parts[i]].Words);
                    characters.AddRange(source[parts[i]].Characters);
                }
                paragraph = paragraph with
                {
                    Words = words,
                    Characters = detail == WordLevelDetail.Characters ? characters : Array.Empty<OcrChar>(),
                };
            }
            keyed.Add((parts.Min(), p, paragraph));
        }

        keyed.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Position.CompareTo(b.Position));
        return keyed.Select(k => k.Paragraph).ToList();
    }

    private static int IndexOfReference(List<OcrLine> lines, OcrLine line)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (ReferenceEquals(lines[i], line)) return i;
        }
        return -1;
    }

    private static bool Contains(OcrBoundingBox outer, OcrBoundingBox inner)
        => inner.MinX >= outer.MinX - 0.5 && inner.MinY >= outer.MinY - 0.5
        && inner.MaxX <= outer.MaxX + 0.5 && inner.MaxY <= outer.MaxY + 0.5;

    private static OcrPoint[] Rectangle(OcrBoundingBox box) => new[]
    {
        new OcrPoint(box.MinX, box.MinY), new OcrPoint(box.MaxX, box.MinY),
        new OcrPoint(box.MaxX, box.MaxY), new OcrPoint(box.MinX, box.MaxY),
    };

    /// <summary>The same LF-joined full text the OCR path builds.</summary>
    private static string BuildFullText(IReadOnlyList<OcrLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line.Text)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line.Text);
        }
        return sb.ToString();
    }
}
