using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure;
using EasyOcrSharp.Structure.Export;
using Xunit;

using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for a multi-line table cell surviving every exporter.
/// The table recognizer separates a cell's visual lines with <c>&lt;br&gt;</c>; Markdown and HTML render that
/// tag as-is, while the OOXML writers have to translate it — Word into a <c>&lt;w:br/&gt;</c> run and Excel
/// into a newline on a wrap-text-styled cell. Before this, <c>OoxmlHtmlTable</c> stripped the tag with the
/// rest of the markup and the cell's lines ran together.
/// </summary>
public class MultiLineCellExportTests
{
    private static OcrBoundingBox Box(double x1, double y1, double x2, double y2) => new(x1, y1, x2, y2);

    private const string MultiLineTableHtml =
        "<html><body><table>" +
        "<tr><td>ONNX Runtime</td><td>Notes</td></tr>" +
        "<tr><td>1.26.x</td><td>Available in PyPI and NuGet.<br>Default GPU package build before 1.27.</td></tr>" +
        "</table></body></html>";

    private static StructureResult Sample() => new()
    {
        Blocks = new[]
        {
            new StructureBlock(StructureBlockType.Table, Box(0, 0, 600, 300), Order: 0, TableHtml: MultiLineTableHtml),
        },
        SourceWidth = 600,
        SourceHeight = 800,
    };

    private static IReadOnlyDictionary<string, string> ReadZip(byte[] package)
    {
        var parts = new Dictionary<string, string>();
        using var ms = new MemoryStream(package);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            using var s = entry.Open();
            using var reader = new StreamReader(s);
            parts[entry.FullName] = reader.ReadToEnd();
        }
        return parts;
    }

    // ---- the shared HTML → grid parser -----------------------------------------------------------------

    [Fact]
    public void The_grid_parser_turns_br_into_a_newline()
    {
        var grid = OoxmlHtmlTable.Parse(MultiLineTableHtml);

        Assert.NotNull(grid);
        Assert.Equal(
            "Available in PyPI and NuGet.\nDefault GPU package build before 1.27.",
            grid!.Cells[1, 1]!.Text);
    }

    [Theory]
    [InlineData("<td>a<br>b</td>")]
    [InlineData("<td>a<br/>b</td>")]
    [InlineData("<td>a<br />b</td>")]
    [InlineData("<td>a<BR>b</td>")]
    public void Every_spelling_of_br_becomes_a_newline(string cell)
    {
        var grid = OoxmlHtmlTable.Parse("<table><tr>" + cell + "</tr></table>");

        Assert.NotNull(grid);
        Assert.Equal("a\nb", grid!.Cells[0, 0]!.Text);
    }

    [Fact]
    public void Other_inline_tags_are_still_stripped_without_leaving_a_break()
    {
        var grid = OoxmlHtmlTable.Parse("<table><tr><td><b>bold</b> and <i>italic</i></td></tr></table>");

        Assert.NotNull(grid);
        Assert.Equal("bold and italic", grid!.Cells[0, 0]!.Text);
    }

    // ---- Word ------------------------------------------------------------------------------------------

    [Fact]
    public void Word_renders_the_cell_break_as_a_w_br_run()
    {
        var parts = ReadZip(Sample().ToDocx());
        string document = parts["word/document.xml"];

        // Well-formed, and the two lines are separate runs joined by an explicit break.
        XDocument.Parse(document);
        Assert.Contains("<w:r><w:br/></w:r>", document);
        Assert.Contains("Available in PyPI and NuGet.", document);
        Assert.Contains("Default GPU package build before 1.27.", document);
        // A literal newline inside <w:t> would render as a space in Word, so none must survive.
        Assert.DoesNotContain("\n", document);
    }

    [Fact]
    public void Word_leaves_single_line_cells_as_one_run()
    {
        var single = new StructureResult
        {
            Blocks = new[]
            {
                new StructureBlock(StructureBlockType.Table, Box(0, 0, 600, 300), Order: 0,
                    TableHtml: "<table><tr><td>Name</td><td>Score</td></tr></table>"),
            },
        };

        string document = ReadZip(single.ToDocx())["word/document.xml"];

        Assert.DoesNotContain("<w:br/>", document);
    }

    // ---- Excel -----------------------------------------------------------------------------------------

    [Fact]
    public void Excel_keeps_the_newline_and_wraps_the_cell()
    {
        var parts = ReadZip(Sample().ToXlsx());
        string sheet = parts["xl/worksheets/sheet1.xml"];

        XDocument.Parse(sheet);
        Assert.Contains("Available in PyPI and NuGet.\nDefault GPU package build before 1.27.", sheet);

        // The multi-line cell (B2) carries the wrap style; the single-line ones do not.
        var cells = XDocument.Parse(sheet).Descendants()
            .Where(e => e.Name.LocalName == "c")
            .ToDictionary(e => (string)e.Attribute("r")!, e => (string?)e.Attribute("s"));

        Assert.Equal("1", cells["B2"]);
        Assert.Null(cells["A1"]);
    }

    [Fact]
    public void Excel_ships_a_well_formed_styles_part_wired_to_the_workbook()
    {
        var parts = ReadZip(Sample().ToXlsx());

        Assert.True(parts.ContainsKey("xl/styles.xml"));
        var styles = XDocument.Parse(parts["xl/styles.xml"]);

        // cellXfs[1] is the wrap format the worksheet references with s="1".
        var formats = styles.Descendants().First(e => e.Name.LocalName == "cellXfs").Elements().ToList();
        Assert.Equal(2, formats.Count);
        var alignment = formats[1].Elements().First(e => e.Name.LocalName == "alignment");
        Assert.Equal("1", (string?)alignment.Attribute("wrapText"));

        // Declared in [Content_Types].xml and related from the workbook, or Excel refuses to open the file.
        Assert.Contains("/xl/styles.xml", parts["[Content_Types].xml"]);
        Assert.Contains("styles.xml", parts["xl/_rels/workbook.xml.rels"]);

        // The sheet relationship ids must stay 1..sheetCount — the styles rel goes after them.
        var rels = XDocument.Parse(parts["xl/_rels/workbook.xml.rels"]).Root!.Elements().ToList();
        Assert.EndsWith("/worksheet", (string)rels[0].Attribute("Type")!);
        Assert.Equal("rId1", (string)rels[0].Attribute("Id")!);
        Assert.EndsWith("/styles", (string)rels[^1].Attribute("Type")!);
    }

    // ---- Markdown / HTML ---------------------------------------------------------------------------------

    [Fact]
    public void Markdown_and_html_carry_the_break_through_verbatim()
    {
        var result = Sample();

        Assert.Contains("<br>", result.ToMarkdown());
        Assert.Contains("<br>", result.ToHtml());
    }

    /// <summary>
    /// The recognizer returns PaddleOCR's whole <c>&lt;html&gt;&lt;body&gt;&lt;table&gt;…</c> document, which
    /// both text exporters have to unwrap: the Markdown one would otherwise paste a second HTML document into
    /// the page, and the HTML one's table-rooted validity check rejected the wrapped form outright and
    /// degraded every real table to its fallback text.
    /// </summary>
    [Fact]
    public void The_recognizers_html_body_wrapper_is_stripped_by_the_text_exporters()
    {
        var result = Sample();

        string markdown = result.ToMarkdown();
        Assert.StartsWith("<table border=\"1\">", markdown);   // pretty tables are on by default
        Assert.EndsWith("</table>", markdown);
        Assert.DoesNotContain("<body>", markdown);

        string html = result.ToHtml();
        // The table survived as markup rather than being escaped into a <p> fallback...
        Assert.Contains("<td>ONNX Runtime</td>", html);
        // ...and the exporter's own <body> is the only one in the document.
        Assert.Equal(1, html.Split("<body").Length - 1);
    }

    [Fact]
    public void Markup_that_is_not_a_table_still_reaches_the_exporters_fallback()
    {
        var truncated = new StructureResult
        {
            Blocks = new[]
            {
                new StructureBlock(StructureBlockType.Table, Box(0, 0, 600, 300), Order: 0,
                    TableHtml: "<table><tr><td>oops", Text: "fallback text"),
            },
        };

        Assert.Contains("fallback text", truncated.ToHtml());
    }
}
