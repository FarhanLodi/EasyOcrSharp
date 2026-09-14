using System.Text;

namespace EasyOcrSharp.Structure;

/// <summary>
/// Joins per-page document-structure results into one continuous Markdown string — the C# parity for
/// Python PP-StructureV3's <c>concatenate_markdown_pages()</c>. Pages are normally joined with a blank
/// line; when the previous page ends mid-paragraph and the next begins mid-paragraph (derived from each
/// page's geometric continuation flags), they are joined with a single space instead — or with nothing at
/// all when either boundary character is CJK, so Chinese paragraphs flow across page breaks without a
/// spurious space. The page order supplied by the caller is preserved and empty / null pages are skipped.
/// </summary>
internal static class StructureMarkdownExtensions
{
    /// <summary>
    /// The pre-2.1 opt-in page joiner: a blank line, a Markdown horizontal rule (<c>---</c>), then a blank
    /// line. The default join is now Python-parity paragraph-aware concatenation (no visible rule); pass
    /// <c>new MarkdownRenderOptions { PageSeparator = StructureMarkdownExtensions.PageSeparator }</c> to
    /// keep page boundaries visible. The leading/trailing blank lines keep the rule from being parsed as a
    /// Setext heading underline for the preceding paragraph.
    /// </summary>
    public const string PageSeparator = "\n\n---\n\n";

    /// <summary>
    /// Renders each page's structure to Markdown (via <see cref="StructureResult.ToMarkdown"/>) and
    /// concatenates the pages in the supplied order. With the default options
    /// (<see cref="MarkdownRenderOptions.PageSeparator"/> = <c>null</c>) the join is Python-parity: a blank
    /// line between pages, except that a page ending mid-paragraph joins the next page's mid-paragraph
    /// start with a single space (or directly, when either boundary character is CJK). Set
    /// <see cref="MarkdownRenderOptions.PageSeparator"/> to force an explicit separator (e.g.
    /// <see cref="PageSeparator"/> for the old horizontal rule). Null pages and pages whose Markdown is
    /// empty / whitespace are skipped.
    /// </summary>
    /// <param name="pages">The per-page results, in reading order. Must not be <c>null</c>; individual entries may be <c>null</c>.</param>
    /// <param name="options">Rendering options applied to every page; <c>null</c> uses <see cref="MarkdownRenderOptions.Default"/>.</param>
    /// <returns>The concatenated multi-page Markdown document (empty when there is no renderable page).</returns>
    public static string ConcatenateMarkdownPages(
        this IEnumerable<StructureResult> pages, MarkdownRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        options ??= MarkdownRenderOptions.Default;

        if (options.PageSeparator is { } separator)
            return Concatenate(pages.Select(p => p?.ToMarkdown(options)), separator);

        var sb = new StringBuilder();
        // Python: previous_page_last_element_paragraph_end_flag starts True.
        bool prevPageEndsParagraph = true;

        foreach (var page in pages)
        {
            if (page is null) continue;
            var rendered = page.RenderMarkdownPage(options);
            if (string.IsNullOrWhiteSpace(rendered.Markdown))
            {
                // An empty page still forwards its end flag (an all-furniture page can end "complete").
                prevPageEndsParagraph = rendered.LastBlockSegEnd;
                continue;
            }

            if (sb.Length == 0)
            {
                sb.Append(rendered.Markdown);
            }
            else if (!rendered.FirstBlockSegStart && !prevPageEndsParagraph)
            {
                // Both sides are mid-paragraph: splice the pages together. Latin text gets a single
                // space; CJK on either side of the boundary joins directly (no space inside a word).
                char last = sb[^1];
                char first = rendered.Markdown[0];
                if (!IsCjk(last) && !IsCjk(first)) sb.Append(' ');
                sb.Append(rendered.Markdown);
            }
            else
            {
                sb.Append("\n\n").Append(rendered.Markdown);
            }

            prevPageEndsParagraph = rendered.LastBlockSegEnd;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Concatenates already-rendered per-page Markdown fragments in the supplied order. Pre-rendered
    /// strings carry no continuation flags, so pages are joined with <paramref name="pageSeparator"/> —
    /// a blank line by default (Python's non-continuation join) — with null / empty / whitespace pages
    /// skipped and each retained page's outer whitespace trimmed. Prefer the
    /// <see cref="ConcatenateMarkdownPages(IEnumerable{StructureResult}, MarkdownRenderOptions)"/>
    /// overload when the <see cref="StructureResult"/> pages are available: it can join continued
    /// paragraphs across the page break.
    /// </summary>
    /// <param name="pages">The per-page Markdown strings, in reading order. Must not be <c>null</c>; individual entries may be <c>null</c>.</param>
    /// <param name="pageSeparator">The joiner between pages; <c>null</c> uses a blank line (<c>"\n\n"</c>). Pass <see cref="PageSeparator"/> for the old horizontal rule.</param>
    /// <returns>The concatenated multi-page Markdown document (empty when there is no non-empty page).</returns>
    public static string ConcatenateMarkdownPages(this IEnumerable<string?> pages, string? pageSeparator = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        return Concatenate(pages, pageSeparator ?? "\n\n");
    }

    /// <summary>
    /// Joins the non-empty, trimmed pages with <paramref name="separator"/> in order.
    /// </summary>
    private static string Concatenate(IEnumerable<string?> pages, string separator)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var page in pages)
        {
            if (string.IsNullOrWhiteSpace(page)) continue;
            if (!first) sb.Append(separator);
            sb.Append(page.Trim());
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Whether <paramref name="c"/> is a CJK unified ideograph — Python's <c>[一-鿿]</c>
    /// (U+4E00–U+9FFF) test used by the cross-page paragraph splice.
    /// </summary>
    private static bool IsCjk(char c) => c >= '一' && c <= '鿿';
}
