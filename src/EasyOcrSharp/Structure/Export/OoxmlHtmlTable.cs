using System.IO.Compression;
using System.Text;

namespace EasyOcrSharp.Structure.Export;

/// <summary>
/// A minimal forgiving parser for the recovered table HTML carried on a
/// <see cref="StructureBlock.TableHtml"/>. PaddleOCR's table recognizer emits well-formed (if attribute-light)
/// <c>&lt;table&gt;…&lt;/table&gt;</c> markup whose cells may carry <c>rowspan</c>/<c>colspan</c>; this parser
/// materializes that into a rectangular <see cref="OoxmlTableGrid"/> with merge information so both the Word
/// and Excel exporters can lay the cells out faithfully. It is intentionally tolerant: it scans for
/// <c>&lt;tr&gt;</c> / <c>&lt;td&gt;</c> / <c>&lt;th&gt;</c> tags, ignores anything it does not understand, and
/// strips inner tags from cell content — except <c>&lt;br&gt;</c>, which the table recognizer emits between a
/// cell's visual lines and which becomes a <c>\n</c> in <see cref="OoxmlGridCell.Text"/> for the exporters to
/// render as a real line break.
/// </summary>
internal static class OoxmlHtmlTable
{
    public static OoxmlTableGrid? Parse(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var rows = new List<List<OoxmlRawCell>>();
        int i = 0;
        int n = html.Length;

        while (i < n)
        {
            int lt = html.IndexOf('<', i);
            if (lt < 0) break;
            int gt = html.IndexOf('>', lt + 1);
            if (gt < 0) break;

            string tag = html.Substring(lt + 1, gt - lt - 1).Trim();
            string lower = tag.ToLowerInvariant();

            if (lower == "tr" || lower.StartsWith("tr ") || lower.StartsWith("tr\t") || lower.StartsWith("tr\n"))
            {
                rows.Add(new List<OoxmlRawCell>());
                i = gt + 1;
                continue;
            }

            if (IsCellOpen(lower))
            {
                int colSpan = ReadSpan(tag, "colspan");
                int rowSpan = ReadSpan(tag, "rowspan");

                // The cell content runs from just after this open tag to its matching close tag.
                int contentStart = gt + 1;
                int close = FindCellClose(html, contentStart);
                int contentEnd = close < 0 ? n : close;
                string raw = html.Substring(contentStart, contentEnd - contentStart);
                string text = CleanCellText(raw);

                if (rows.Count == 0) rows.Add(new List<OoxmlRawCell>());
                rows[^1].Add(new OoxmlRawCell(text, Math.Max(1, colSpan), Math.Max(1, rowSpan)));

                // Resume scanning after the cell content (the close tag, if any, is re-scanned harmlessly).
                i = close < 0 ? n : close;
                continue;
            }

            i = gt + 1;
        }

        // Drop trailing empty rows produced by stray <tr> with no cells.
        rows.RemoveAll(r => r.Count == 0);
        if (rows.Count == 0) return null;

        return OoxmlTableGrid.FromRows(rows);
    }

    private static bool IsCellOpen(string lowerTag)
        => lowerTag == "td" || lowerTag == "th"
           || lowerTag.StartsWith("td ") || lowerTag.StartsWith("th ")
           || lowerTag.StartsWith("td\t") || lowerTag.StartsWith("th\t")
           || lowerTag.StartsWith("td\n") || lowerTag.StartsWith("th\n");

    /// <summary>
    /// Finds the index of the next <c>&lt;/td&gt;</c> or <c>&lt;/th&gt;</c> from <paramref name="from"/>.
    /// </summary>
    private static int FindCellClose(string html, int from)
    {
        int i = from;
        while (i < html.Length)
        {
            int lt = html.IndexOf("</", i, StringComparison.Ordinal);
            if (lt < 0) return -1;
            int gt = html.IndexOf('>', lt + 2);
            if (gt < 0) return -1;
            string name = html.Substring(lt + 2, gt - lt - 2).Trim().ToLowerInvariant();
            if (name == "td" || name == "th") return lt;
            i = gt + 1;
        }
        return -1;
    }

    /// <summary>
    /// Upper bound on a single <c>colspan</c>/<c>rowspan</c>. The grid builder allocates
    /// <c>rowCount &#215; maxCols</c> arrays from the summed spans, so an unclamped attribute
    /// (<c>colspan="100000000"</c>) is an immediate <see cref="OutOfMemoryException"/> in
    /// <c>SaveAsDocx</c>/<c>SaveAsXlsx</c>. Mirrors <c>TableHtmlParser.MaxSpan</c>.
    /// </summary>
    private const int MaxSpan = 512;

    private static int ReadSpan(string tag, string attr)
    {
        int idx = tag.IndexOf(attr, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 1;
        int eq = tag.IndexOf('=', idx);
        if (eq < 0) return 1;

        int j = eq + 1;
        while (j < tag.Length && (tag[j] == ' ' || tag[j] == '"' || tag[j] == '\'')) j++;
        int start = j;
        while (j < tag.Length && char.IsDigit(tag[j])) j++;
        if (j == start) return 1;
        if (!int.TryParse(tag.AsSpan(start, j - start), out int v) || v <= 0) return 1;
        return Math.Min(v, MaxSpan);
    }

    /// <summary>
    /// Strips nested tags and decodes the handful of HTML entities PaddleOCR emits. <c>&lt;br&gt;</c> (in any
    /// of its spellings — <c>&lt;br&gt;</c>, <c>&lt;br/&gt;</c>, <c>&lt;br /&gt;</c>) is the one tag that
    /// carries meaning here: it becomes a newline, so a multi-line cell survives into Word and Excel instead
    /// of having its lines run together.
    /// </summary>
    private static string CleanCellText(string raw)
    {
        if (raw.Length == 0) return string.Empty;

        var sb = new StringBuilder(raw.Length);
        int i = 0;
        while (i < raw.Length)
        {
            char ch = raw[i];
            if (ch != '<')
            {
                sb.Append(ch);
                i++;
                continue;
            }

            // A tag: skip to its '>' (or to the end when the markup is truncated), emitting a newline for
            // <br> and nothing for everything else.
            int gt = raw.IndexOf('>', i + 1);
            int end = gt < 0 ? raw.Length : gt + 1;
            if (IsLineBreak(raw.AsSpan(i + 1, (gt < 0 ? raw.Length : gt) - i - 1)))
            {
                sb.Append('\n');
            }
            i = end;
        }

        return DecodeEntities(sb.ToString()).Trim();
    }

    /// <summary>
    /// True when the tag body (between <c>&lt;</c> and <c>&gt;</c>) is a <c>br</c>, self-closing or not.
    /// </summary>
    private static bool IsLineBreak(ReadOnlySpan<char> tagBody)
    {
        var body = tagBody.Trim().TrimEnd('/').Trim();
        return body.Equals("br", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeEntities(string s)
    {
        if (s.IndexOf('&') < 0) return s;
        return s
            .Replace("&nbsp;", " ")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&apos;", "'")
            .Replace("&amp;", "&");
    }
}
