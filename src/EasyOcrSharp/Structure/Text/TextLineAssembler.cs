using System.Text;

namespace EasyOcrSharp.Structure.Text;

/// <summary>
/// One recognized fragment inside a layout block, in page coordinates: an OCR line box (or a recognized
/// formula region) with its text. <see cref="IsFormula"/> marks LaTeX content that is spliced into the
/// running text wrapped in <c>$...$</c> — the C# counterpart of PaddleX's <c>TextSpan</c> with
/// <c>label='formula'</c> (<c>layout_parsing/layout_objects.py:35-56</c>).
/// </summary>
internal readonly record struct BlockSpan(float X1, float Y1, float X2, float Y2, string Text, bool IsFormula);

/// <summary>
/// Assembles a layout block's recognized spans into the block's final text — the C# port of PaddleX
/// PP-StructureV3's line/paragraph text assembly: <c>LayoutBlock.group_boxes_into_lines</c> +
/// <c>LayoutBlock.update_text_content</c> and <c>TextLine.get_texts</c>/<c>format_line</c>
/// (<c>paddlex/inference/pipelines/layout_parsing/layout_objects.py:134-376, 532-731</c>), with the
/// per-label delimiters of <c>setting.py:36-42</c>.
/// <para>
/// The pipeline is: (1) group spans into visual lines by vertical projection overlap ≥ 0.6 against the
/// line's running band (<c>line_height_iou_threshold</c>, projection ratio in "small" mode —
/// <c>utils.py:174-218</c>), lines top-to-bottom, spans within a line left-to-right; (2) join spans in a
/// line with a single space when the boundary characters are English letters/digits/<c>'$'</c> (no space
/// for CJK-CJK adjacency), splicing formula spans wrapped in <c>$...$</c> with a space on each side;
/// (3) join lines: strip a soft hyphen (<c>letter + '-'</c> at line end followed by a lowercase letter)
/// and concatenate directly, emit <c>'\n'</c> when the line is a real paragraph break
/// (<see cref="NeedNewLine"/>), otherwise treat the wrap as a continuation and join with
/// <c>' '</c>/<c>''</c> by the same boundary-character rule; (4) per-label overrides:
/// <c>doc_title</c> joins everything with a single space, <c>content</c> (TOC) joins lines with
/// <c>'\n'</c> always (<c>setting.py:38-41</c>), and <c>reference</c> blocks measure the text band from
/// the OCR extents instead of the layout box (<c>layout_objects.py:648-654</c>).
/// </para>
/// <para>
/// Vertical (CJK top-to-bottom) text: when more than 70% of the spans are taller than wide AND
/// horizontal grouping would put every span on its own line, spans are regrouped into columns taken
/// right-to-left, each column read top-to-bottom. This is a pragmatic port with deliberate
/// simplifications versus Python's direction-parameterized <c>TextLine</c>: columns are joined with
/// <c>'\n'</c> unconditionally (no vertical <c>need_new_line</c> heuristics), the over-tall vertical
/// line filter (<c>layout_objects.py:588-601</c>) is not applied, and formula-projection span splitting
/// with re-recognition (<c>layout_objects.py:171-199</c>) is not ported — spans arrive already cut by
/// the engine.
/// </para>
/// </summary>
internal static class TextLineAssembler
{
    /// <summary>
    /// Minimum vertical projection overlap (relative to the smaller of the two heights) for a span to
    /// join the current visual line. PaddleX <c>LINE_SETTINGS['line_height_iou_threshold']</c>
    /// (<c>setting.py:37</c>) as passed to <c>group_boxes_into_lines</c>.
    /// </summary>
    private const float LineHeightIouThreshold = 0.6f;

    /// <summary>
    /// A line whose right edge falls short of the block's right edge by more than this fraction of the
    /// block width ends a paragraph (<c>block_text_width * 0.3</c> tests in
    /// <c>layout_objects.py:326-333</c> and <c>722-725</c>).
    /// </summary>
    private const float LineEndShortfallRatio = 0.3f;

    /// <summary>
    /// The block's FIRST line uses a laxer shortfall test — it must end more than half the block width
    /// short to force a break (the 0.5 branch, <c>layout_objects.py:369-374</c>).
    /// </summary>
    private const float FirstLineEndShortfallRatio = 0.5f;

    /// <summary>
    /// A vertical gap to the next line larger than this multiple of the line's height ends a paragraph
    /// (<c>line_gap_limit = self.height * 1.5</c>, <c>layout_objects.py:196</c>).
    /// </summary>
    private const float LineGapFactor = 1.5f;

    /// <summary>
    /// Fraction of spans that must be taller than wide before the block is considered vertical
    /// (CJK top-to-bottom) text and regrouped into right-to-left columns.
    /// </summary>
    private const float VerticalSpanRatioThreshold = 0.7f;

    /// <summary>
    /// Assembles the block's text from its recognized spans.
    /// </summary>
    /// <param name="spans">Recognized fragments in page coordinates, any order.</param>
    /// <param name="label">Raw paddle layout label of the block (e.g. <c>"text"</c>, <c>"doc_title"</c>,
    /// <c>"content"</c>, <c>"reference"</c>); drives the per-label delimiter rules.</param>
    /// <param name="blockX1">Left edge of the layout block (page coordinates).</param>
    /// <param name="blockY1">Top edge of the layout block. Currently unused — reserved for the vertical
    /// flow the pragmatic vertical port replaces with unconditional column breaks.</param>
    /// <param name="blockX2">Right edge of the layout block; with <paramref name="blockX1"/> it defines
    /// the block width the <see cref="NeedNewLine"/> shortfall tests measure against.</param>
    /// <param name="blockY2">Bottom edge of the layout block. Currently unused (see <paramref name="blockY1"/>).</param>
    public static string Assemble(
        IReadOnlyList<BlockSpan> spans,
        string label,
        float blockX1,
        float blockY1,
        float blockX2,
        float blockY2)
    {
        if (spans is null || spans.Count == 0) return string.Empty;

        // Spans with no visible text contribute nothing (Python skips empty line_texts,
        // layout_objects.py:692-693); internal spacing of surviving span texts is preserved.
        var items = new List<BlockSpan>(spans.Count);
        foreach (var span in spans)
        {
            if (!string.IsNullOrWhiteSpace(span.Text)) items.Add(span);
        }
        if (items.Count == 0) return string.Empty;

        var lines = GroupIntoLines(items);
        bool vertical = false;
        if (lines.Count == items.Count && IsVerticalCandidate(items))
        {
            // Every "line" holds a single tall span — this is vertical CJK text; read columns
            // right-to-left instead.
            lines = GroupIntoColumns(items);
            vertical = true;
        }

        var lineTexts = new List<string>(lines.Count);
        foreach (var line in lines) lineTexts.Add(BuildLineText(line));

        // Per-label delimiters (setting.py:38-41): doc_title flows onto one line, content (TOC) keeps
        // every entry on its own line.
        if (string.Equals(label, "doc_title", StringComparison.Ordinal))
            return JoinNonEmpty(lineTexts, " ");
        if (string.Equals(label, "content", StringComparison.Ordinal))
            return JoinNonEmpty(lineTexts, "\n");

        // Pragmatic vertical port: each right-to-left column is one output line.
        if (vertical)
            return JoinNonEmpty(lineTexts, "\n");

        // Reference blocks measure the text band from the OCR extents rather than the layout box
        // (layout_objects.py:648-654); a degenerate block box falls back the same way.
        float start = blockX1, stop = blockX2;
        if (string.Equals(label, "reference", StringComparison.Ordinal) || stop <= start)
        {
            start = float.MaxValue;
            stop = float.MinValue;
            foreach (var span in items)
            {
                start = Math.Min(start, span.X1);
                stop = Math.Max(stop, span.X2);
            }
        }

        return JoinLines(lines, lineTexts, stop - start, stop);
    }

    /// <summary>
    /// A visual line (or, in vertical mode, a column): its ordered spans plus the axis-aligned union
    /// box, maintained incrementally as spans are added — Python's <c>TextLine.region_box</c>
    /// (<c>layout_objects.py:113-132</c>).
    /// </summary>
    internal sealed class SpanLine
    {
        public List<BlockSpan> Spans { get; } = new();
        public float MinX { get; private set; } = float.MaxValue;
        public float MinY { get; private set; } = float.MaxValue;
        public float MaxX { get; private set; } = float.MinValue;
        public float MaxY { get; private set; } = float.MinValue;

        public float Height => MaxY - MinY;

        public void Add(BlockSpan span)
        {
            Spans.Add(span);
            MinX = Math.Min(MinX, span.X1);
            MinY = Math.Min(MinY, span.Y1);
            MaxX = Math.Max(MaxX, span.X2);
            MaxY = Math.Max(MaxY, span.Y2);
        }
    }

    /// <summary>
    /// Groups spans into visual lines: spans are walked top-to-bottom and a span joins the current line
    /// while its vertical projection overlaps the line's running band by at least
    /// <see cref="LineHeightIouThreshold"/> of the smaller height (<c>group_boxes_into_lines</c>,
    /// <c>layout_objects.py:560-586</c>, projection mode "small"). Spans within a line are ordered
    /// left-to-right, lines top-to-bottom.
    /// </summary>
    internal static List<SpanLine> GroupIntoLines(IReadOnlyList<BlockSpan> spans)
    {
        var sorted = new List<BlockSpan>(spans);
        sorted.Sort((a, b) => a.Y1 != b.Y1 ? a.Y1.CompareTo(b.Y1) : a.X1.CompareTo(b.X1));

        var lines = new List<SpanLine>();
        var current = new SpanLine();
        current.Add(sorted[0]);
        for (int i = 1; i < sorted.Count; i++)
        {
            var span = sorted[i];
            float overlap = Math.Min(current.MaxY, span.Y2) - Math.Max(current.MinY, span.Y1);
            float smaller = Math.Min(current.Height, span.Y2 - span.Y1);
            float ratio = overlap > 0 && smaller > 0 ? overlap / smaller : 0f;
            if (ratio >= LineHeightIouThreshold)
            {
                current.Add(span);
            }
            else
            {
                lines.Add(current);
                current = new SpanLine();
                current.Add(span);
            }
        }
        lines.Add(current);

        foreach (var line in lines)
            line.Spans.Sort((a, b) => a.X1 != b.X1 ? a.X1.CompareTo(b.X1) : a.Y1.CompareTo(b.Y1));
        lines.Sort((a, b) => a.MinY != b.MinY ? a.MinY.CompareTo(b.MinY) : a.MinX.CompareTo(b.MinX));
        return lines;
    }

    /// <summary>
    /// Vertical-mode counterpart of <see cref="GroupIntoLines"/>: spans are walked right-to-left and a
    /// span joins the current column while its horizontal projection overlaps the column's running band
    /// by at least <see cref="LineHeightIouThreshold"/> of the smaller width. Spans within a column are
    /// ordered top-to-bottom, columns right-to-left (Python's <c>direction='vertical'</c> branch,
    /// <c>layout_objects.py:561-565</c>).
    /// </summary>
    internal static List<SpanLine> GroupIntoColumns(IReadOnlyList<BlockSpan> spans)
    {
        var sorted = new List<BlockSpan>(spans);
        sorted.Sort((a, b) => a.X1 != b.X1 ? b.X1.CompareTo(a.X1) : a.Y1.CompareTo(b.Y1));

        var columns = new List<SpanLine>();
        var current = new SpanLine();
        current.Add(sorted[0]);
        for (int i = 1; i < sorted.Count; i++)
        {
            var span = sorted[i];
            float overlap = Math.Min(current.MaxX, span.X2) - Math.Max(current.MinX, span.X1);
            float smaller = Math.Min(current.MaxX - current.MinX, span.X2 - span.X1);
            float ratio = overlap > 0 && smaller > 0 ? overlap / smaller : 0f;
            if (ratio >= LineHeightIouThreshold)
            {
                current.Add(span);
            }
            else
            {
                columns.Add(current);
                current = new SpanLine();
                current.Add(span);
            }
        }
        columns.Add(current);

        foreach (var column in columns)
            column.Spans.Sort((a, b) => a.Y1 != b.Y1 ? a.Y1.CompareTo(b.Y1) : b.X1.CompareTo(a.X1));
        columns.Sort((a, b) => b.MaxX.CompareTo(a.MaxX));
        return columns;
    }

    /// <summary>
    /// True when more than <see cref="VerticalSpanRatioThreshold"/> of the spans are taller than wide —
    /// the trigger for vertical (CJK top-to-bottom) column grouping.
    /// </summary>
    internal static bool IsVerticalCandidate(IReadOnlyList<BlockSpan> spans)
    {
        int tall = 0;
        foreach (var span in spans)
        {
            if (span.Y2 - span.Y1 > span.X2 - span.X1) tall++;
        }
        return tall > spans.Count * VerticalSpanRatioThreshold;
    }

    /// <summary>
    /// Joins one line's spans into its text (<c>format_line</c>'s span loop,
    /// <c>layout_objects.py:296-311</c>): a single space separates spans when the boundary characters
    /// are English letters/digits/<c>'$'</c> (never for CJK-CJK adjacency); a formula span is wrapped in
    /// <c>$...$</c> (unless it already carries the dollars) and gets a space on each side, doubles
    /// collapsed. Trailing whitespace is stripped; internal span spacing is preserved.
    /// </summary>
    internal static string BuildLineText(SpanLine line)
    {
        var sb = new StringBuilder();
        bool prevFormula = false;
        foreach (var span in line.Spans)
        {
            string text = span.Text;
            if (span.IsFormula && !text.StartsWith('$') && !text.EndsWith('$'))
                text = "$" + text + "$";

            if (sb.Length > 0)
            {
                char left = sb[^1];
                char right = text[0];
                bool wantSpace = prevFormula || span.IsFormula || IsSpacingChar(left) || IsSpacingChar(right);
                if (wantSpace && !char.IsWhiteSpace(left) && !char.IsWhiteSpace(right))
                    sb.Append(' ');
            }
            sb.Append(text);
            prevFormula = span.IsFormula;
        }

        int end = sb.Length;
        while (end > 0 && char.IsWhiteSpace(sb[end - 1])) end--;
        return sb.ToString(0, end);
    }

    /// <summary>
    /// Joins the built line texts into the block content (the default, delimiter-less branch of
    /// <c>update_text_content</c>, <c>layout_objects.py:687-726</c>, folded together with
    /// <c>format_line</c>'s line-end decisions): a soft hyphen (English letter + <c>'-'</c> at line end,
    /// next line starting lowercase) is stripped and the halves concatenated directly
    /// (<c>layout_objects.py:348-350</c>); a real paragraph break (<see cref="NeedNewLine"/>) emits
    /// <c>'\n'</c>; any other wrap is a continuation joined by the boundary-character space rule.
    /// </summary>
    private static string JoinLines(List<SpanLine> lines, List<string> lineTexts, float blockWidth, float blockStop)
    {
        var indices = new List<int>(lineTexts.Count);
        for (int i = 0; i < lineTexts.Count; i++)
        {
            if (lineTexts[i].Length > 0) indices.Add(i);
        }
        if (indices.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        for (int k = 0; k < indices.Count; k++)
        {
            int i = indices[k];
            string text = lineTexts[i];
            if (k == indices.Count - 1)
            {
                sb.Append(text);
                break;
            }

            int j = indices[k + 1];
            string nextText = lineTexts[j];

            // Soft hyphen: "exam-" + "ple" -> "example" (strip the '-', no separator).
            if (text.Length >= 2 && text[^1] == '-' && IsEnglishLetter(text[^2]) && nextText[0] is >= 'a' and <= 'z')
            {
                sb.Append(text, 0, text.Length - 1);
                continue;
            }

            sb.Append(text);
            if (NeedNewLine(lines[i], lines[j], blockWidth, blockStop, isFirstLine: k == 0))
                sb.Append('\n');
            else if (IsSpacingChar(text[^1]) || IsSpacingChar(nextText[0]))
                sb.Append(' ');
        }
        return sb.ToString();
    }

    /// <summary>
    /// True when the line ends a paragraph: its right edge falls more than
    /// <see cref="LineEndShortfallRatio"/> (first line: <see cref="FirstLineEndShortfallRatio"/>) of the
    /// block width short of the block's right edge, or the vertical gap to the next line exceeds
    /// <see cref="LineGapFactor"/> × the line height (<c>need_new_line</c>,
    /// <c>layout_objects.py:326-374</c>).
    /// </summary>
    private static bool NeedNewLine(SpanLine line, SpanLine next, float blockWidth, float blockStop, bool isFirstLine)
    {
        float shortfallRatio = isFirstLine ? FirstLineEndShortfallRatio : LineEndShortfallRatio;
        if (blockWidth > 0 && blockStop - line.MaxX > blockWidth * shortfallRatio) return true;
        return next.MinY - line.MaxY > line.Height * LineGapFactor;
    }

    private static string JoinNonEmpty(List<string> lineTexts, string delimiter)
    {
        var sb = new StringBuilder();
        foreach (var text in lineTexts)
        {
            if (text.Length == 0) continue;
            if (sb.Length > 0) sb.Append(delimiter);
            sb.Append(text);
        }
        return sb.ToString();
    }

    /// <summary>ASCII English letter — PaddleX <c>is_english_letter</c> (<c>utils.py:296-298</c>).</summary>
    private static bool IsEnglishLetter(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    /// <summary>
    /// A boundary character that wants a space next to it: English letter, ASCII digit, or <c>'$'</c>
    /// (formula delimiter) — the character class of <c>format_line</c>'s spacing tests
    /// (<c>layout_objects.py:306-311, 352-355</c>).
    /// </summary>
    private static bool IsSpacingChar(char c) => IsEnglishLetter(c) || c is (>= '0' and <= '9') or '$';
}
