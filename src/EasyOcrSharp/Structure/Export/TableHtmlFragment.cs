namespace EasyOcrSharp.Structure.Export;

/// <summary>
/// Extracts the bare <c>&lt;table&gt;…&lt;/table&gt;</c> fragment from recovered table markup.
/// <para>
/// The table recognizer returns what PaddleOCR's <c>get_pred_html</c> returns — a whole little document,
/// <c>&lt;html&gt;&lt;body&gt;&lt;table&gt;…&lt;/table&gt;&lt;/body&gt;&lt;/html&gt;</c> — and
/// <see cref="StructureBlock.TableHtml"/> carries it verbatim so the value stays at parity for callers that
/// compare against Python. Every exporter, though, embeds the table INSIDE a document of its own: a nested
/// <c>&lt;html&gt;</c>/<c>&lt;body&gt;</c> is invalid in the HTML export and simply noise in the Markdown
/// one. They all want the fragment, which is what this returns.
/// </para>
/// </summary>
internal static class TableHtmlFragment
{
    /// <summary>
    /// Returns the <c>&lt;table&gt;…&lt;/table&gt;</c> span of <paramref name="html"/> (outermost table,
    /// first opener to last closer), or the trimmed input when it carries no such span — leaving the
    /// caller's own fallback to deal with markup that is not a table at all.
    /// </summary>
    public static string Extract(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var trimmed = html.Trim();

        int start = trimmed.IndexOf("<table", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return trimmed;

        int end = trimmed.LastIndexOf("</table>", StringComparison.OrdinalIgnoreCase);
        if (end < start) return trimmed;

        return trimmed.Substring(start, end + "</table>".Length - start);
    }
}
