using System;
using System.Collections.Generic;
using System.Xml.Linq;
using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure;
using Xunit;

using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for the structure export helpers
/// <see cref="StructureHtmlExporter.ToHtml"/> and
/// <see cref="StructureMarkdownExtensions.ConcatenateMarkdownPages(IEnumerable{StructureResult}, MarkdownRenderOptions)"/>.
/// The HTML exporter walks <see cref="StructureResult.Blocks"/> in reading order and renders each block by
/// kind (titles → headings, text → paragraphs, tables → their HTML verbatim, formulas → MathJax); the
/// markdown concatenator joins per-page renderings in order with a blank line by default (paragraph-aware,
/// Python parity), or an explicit separator on request. These tests build a small hand-assembled result and
/// assert the emitted output's well-formedness, content and ordering.
/// </summary>
public class StructureHtmlExportTests
{
    private static OcrBoundingBox Box(double x1, double y1, double x2, double y2) => new(x1, y1, x2, y2);

    private static StructureResult Build(params StructureBlock[] blocks)
        => new() { Blocks = blocks, SourceWidth = 600, SourceHeight = 800 };

    private static StructureResult Sample() => Build(
        new StructureBlock(StructureBlockType.DocTitle, Box(0, 0, 600, 40), Order: 0, Text: "My Document"),
        new StructureBlock(StructureBlockType.Text, Box(0, 50, 600, 120), Order: 1, Text: "Intro paragraph."),
        new StructureBlock(StructureBlockType.Title, Box(0, 130, 600, 160), Order: 2, Text: "Results"),
        new StructureBlock(StructureBlockType.Table, Box(0, 170, 600, 300), Order: 3,
            TableHtml: "<table><tr><td>1</td><td>2</td></tr></table>"),
        new StructureBlock(StructureBlockType.Formula, Box(0, 310, 600, 360), Order: 4,
            Latex: "E = mc^2"));

    /// <summary>
    /// Extracts the <c>&lt;body&gt;…&lt;/body&gt;</c> region and parses it as XML to assert the body markup
    /// is well-formed (all tags balanced/closed, all text escaped). The full document is HTML5 (DOCTYPE +
    /// script), which is not strict XML, so we validate the body — the part the exporter assembles.
    /// </summary>
    private static XElement ParseBody(string html)
    {
        int start = html.IndexOf("<body>", StringComparison.Ordinal);
        int end = html.IndexOf("</body>", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "document must contain a <body> element");
        string body = html.Substring(start, (end - start) + "</body>".Length);
        // Throws XmlException if the body is not well-formed; that is the assertion.
        return XDocument.Parse(body).Root!;
    }

    [Fact]
    public void ToHtml_emits_a_complete_html5_document()
    {
        var html = Sample().ToHtml();

        Assert.StartsWith("<!DOCTYPE html>", html.TrimStart());
        Assert.Contains("<html", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("<head>", html);
        Assert.Contains("</html>", html);
        Assert.Contains("<title>My Document</title>", html);
    }

    [Fact]
    public void ToHtml_body_is_well_formed_xml()
    {
        var html = Sample().ToHtml();
        var body = ParseBody(html); // throws if not well-formed

        Assert.Equal("body", body.Name.LocalName);
    }

    [Fact]
    public void ToHtml_maps_titles_to_headings_and_text_to_paragraphs()
    {
        var html = Sample().ToHtml();

        Assert.Contains("<h1>My Document</h1>", html);
        Assert.Contains("<h2>Results</h2>", html);
        Assert.Contains("<p>Intro paragraph.</p>", html);
    }

    [Fact]
    public void ToHtml_emits_table_html_verbatim_into_the_body()
    {
        var html = Sample().ToHtml();

        Assert.Contains("<table><tr><td>1</td><td>2</td></tr></table>", html);

        // The table must actually parse as a <table> element inside the body.
        var body = ParseBody(html);
        Assert.Contains(body.Descendants(), e => e.Name.LocalName == "table");
    }

    [Fact]
    public void ToHtml_wraps_formula_for_mathjax_and_keeps_raw_latex()
    {
        var html = Sample().ToHtml();

        Assert.Contains("data-latex=\"E = mc^2\"", html);
        Assert.Contains("\\[ E = mc^2 \\]", html);
        // The MathJax CDN script is included (optional, but present).
        Assert.Contains("mathjax", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToHtml_renders_figure_placeholder_with_bbox()
    {
        var result = Build(
            new StructureBlock(StructureBlockType.Figure, Box(10, 20, 110, 220), Order: 0, Text: "Figure 1"));
        var html = result.ToHtml();

        Assert.Contains("<figure", html);
        Assert.Contains("data-type=\"Figure\"", html);
        Assert.Contains("data-bbox=\"10 20 110 220\"", html);
        Assert.Contains("Figure 1", html);
        ParseBody(html); // well-formed
    }

    [Fact]
    public void ToHtml_escapes_text_content()
    {
        var result = Build(
            new StructureBlock(StructureBlockType.Text, Box(0, 0, 600, 40), Order: 0,
                Text: "a < b && c > d \"q\""));
        var html = result.ToHtml();

        Assert.Contains("a &lt; b &amp;&amp; c &gt; d", html);
        Assert.DoesNotContain("<p>a < b", html);
        ParseBody(html); // still well-formed after escaping
    }

    [Fact]
    public void ToHtml_renders_blocks_in_reading_order_not_list_order()
    {
        var result = Build(
            new StructureBlock(StructureBlockType.Formula, Box(0, 310, 600, 360), Order: 2, Latex: "b"),
            new StructureBlock(StructureBlockType.DocTitle, Box(0, 0, 600, 40), Order: 0, Text: "Head"),
            new StructureBlock(StructureBlockType.Text, Box(0, 50, 600, 120), Order: 1, Text: "Body"));

        var html = result.ToHtml();

        int head = html.IndexOf("Head", StringComparison.Ordinal);
        int bodyText = html.IndexOf("Body", StringComparison.Ordinal);
        int formula = html.IndexOf("data-latex=\"b\"", StringComparison.Ordinal);
        Assert.True(head < bodyText, "title must precede body");
        Assert.True(bodyText < formula, "body must precede formula");
    }

    [Fact]
    public void ToHtml_drops_malformed_table_markup()
    {
        var result = Build(
            new StructureBlock(StructureBlockType.Table, Box(0, 0, 600, 40), Order: 0,
                TableHtml: "<table><tr><td>oops", Text: "fallback text"));
        var html = result.ToHtml();

        // Truncated table markup must NOT be injected; we degrade to the fallback text and stay well-formed.
        Assert.DoesNotContain("<table><tr><td>oops", html);
        Assert.Contains("fallback text", html);
        ParseBody(html);
    }

    [Fact]
    public void ToHtml_empty_result_still_produces_a_well_formed_document()
    {
        var html = StructureResult.Empty.ToHtml();
        Assert.Contains("<body>", html);
        ParseBody(html);
    }

    // -------------------- ConcatenateMarkdownPages --------------------

    [Fact]
    public void ConcatenateMarkdownPages_joins_results_in_order_with_blank_lines_by_default()
    {
        var page1 = Build(new StructureBlock(StructureBlockType.DocTitle, Box(0, 0, 1, 1), Order: 0, Text: "Page One"));
        var page2 = Build(new StructureBlock(StructureBlockType.DocTitle, Box(0, 0, 1, 1), Order: 0, Text: "Page Two"));
        var page3 = Build(new StructureBlock(StructureBlockType.DocTitle, Box(0, 0, 1, 1), Order: 0, Text: "Page Three"));

        var md = new[] { page1, page2, page3 }.ConcatenateMarkdownPages();

        int p1 = md.IndexOf("Page One", StringComparison.Ordinal);
        int p2 = md.IndexOf("Page Two", StringComparison.Ordinal);
        int p3 = md.IndexOf("Page Three", StringComparison.Ordinal);
        Assert.True(p1 < p2 && p2 < p3, "pages must be concatenated in order");

        // Python-parity default: blank-line joins, no horizontal rules.
        Assert.DoesNotContain("---", md);
        Assert.Equal(2, CountOccurrences(md, "\n\n"));
    }

    [Fact]
    public void ConcatenateMarkdownPages_page_separator_option_restores_the_horizontal_rule()
    {
        var page1 = Build(new StructureBlock(StructureBlockType.DocTitle, Box(0, 0, 1, 1), Order: 0, Text: "Page One"));
        var page2 = Build(new StructureBlock(StructureBlockType.DocTitle, Box(0, 0, 1, 1), Order: 0, Text: "Page Two"));

        var md = new[] { page1, page2 }.ConcatenateMarkdownPages(
            new MarkdownRenderOptions { PageSeparator = StructureMarkdownExtensions.PageSeparator });

        Assert.Equal(1, CountOccurrences(md, StructureMarkdownExtensions.PageSeparator));
    }

    [Fact]
    public void ConcatenateMarkdownPages_string_overload_joins_with_blank_line_by_default()
    {
        // Pre-rendered strings carry no continuation flags: blank-line join by default, explicit separator
        // (e.g. the old horizontal rule) on request.
        Assert.Equal("# A\n\n# B", new[] { "# A", "# B" }.ConcatenateMarkdownPages());
        Assert.Equal(
            "# A" + StructureMarkdownExtensions.PageSeparator + "# B",
            new[] { "# A", "# B" }.ConcatenateMarkdownPages(StructureMarkdownExtensions.PageSeparator));
    }

    [Fact]
    public void ConcatenateMarkdownPages_skips_null_and_empty_pages()
    {
        var md = new string?[] { null, "# A", "   ", "", "# B" }.ConcatenateMarkdownPages();

        Assert.Equal("# A\n\n# B", md);
        // Exactly one separator survives (between the two non-empty pages), no leading/trailing joiners.
        Assert.Equal(1, CountOccurrences(md, "\n\n"));
    }

    [Fact]
    public void ConcatenateMarkdownPages_empty_sequence_is_empty()
    {
        Assert.Equal(string.Empty, Array.Empty<string?>().ConcatenateMarkdownPages());
        Assert.Equal(string.Empty, Array.Empty<StructureResult>().ConcatenateMarkdownPages());
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
