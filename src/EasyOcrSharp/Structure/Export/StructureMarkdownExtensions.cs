using System.Text;

namespace EasyOcrSharp.Structure;

/// <summary>
/// Joins per-page document-structure results into one continuous Markdown string — the C# parity for
/// Python PP-StructureV3's <c>concatenate_markdown_pages()</c>. The page order supplied by the caller is
/// preserved, each page is separated by a clean page break, and empty / null pages are skipped so the
/// output never contains stray separators.
/// </summary>
internal static class StructureMarkdownExtensions
{
    /// <summary>
    /// The joiner placed between two consecutive (non-empty) pages: a blank line, a Markdown horizontal
    /// rule (<c>---</c>), then a blank line. Python's reference simply joins pages with newlines; we use an
    /// explicit <c>---</c> rule so page boundaries stay visible after concatenation while remaining valid
    /// CommonMark (a thematic break). The leading/trailing blank lines keep the rule from being parsed as a
    /// Setext heading underline for the preceding paragraph.
    /// </summary>
    public const string PageSeparator = "\n\n---\n\n";

    /// <summary>
    /// Renders each page's structure to Markdown (via <see cref="StructureResult.ToMarkdown"/>) and
    /// concatenates the pages in the supplied order, joined by <see cref="PageSeparator"/>. Null pages and
    /// pages whose Markdown is empty / whitespace are skipped.
    /// </summary>
    /// <param name="pages">The per-page results, in reading order. Must not be <c>null</c>; individual entries may be <c>null</c>.</param>
    /// <returns>The concatenated multi-page Markdown document (empty when there is no renderable page).</returns>
    public static string ConcatenateMarkdownPages(this IEnumerable<StructureResult> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        return Concatenate(pages.Select(p => p?.ToMarkdown()));
    }

    /// <summary>
    /// Concatenates already-rendered per-page Markdown fragments in the supplied order, joined by
    /// <see cref="PageSeparator"/>. Null / empty / whitespace pages are skipped, and each retained page's
    /// outer whitespace is trimmed so the separators line up cleanly.
    /// </summary>
    /// <param name="pages">The per-page Markdown strings, in reading order. Must not be <c>null</c>; individual entries may be <c>null</c>.</param>
    /// <returns>The concatenated multi-page Markdown document (empty when there is no non-empty page).</returns>
    public static string ConcatenateMarkdownPages(this IEnumerable<string?> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        return Concatenate(pages);
    }

    /// <summary>
    /// Joins the non-empty, trimmed pages with <see cref="PageSeparator"/> in order.
    /// </summary>
    private static string Concatenate(IEnumerable<string?> pages)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var page in pages)
        {
            if (string.IsNullOrWhiteSpace(page)) continue;
            if (!first) sb.Append(PageSeparator);
            sb.Append(page.Trim());
            first = false;
        }
        return sb.ToString();
    }
}
