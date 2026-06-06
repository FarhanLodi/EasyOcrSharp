using System.Text;
using EasyOcrSharp.Export;
using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf.Internal;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Unicode behaviour across the output layers. The OCR data model and all text exporters are fully
/// Unicode; the searchable-PDF <i>invisible text layer</i> is deliberately Latin-1 (WinAnsi base-14
/// font), so non-Latin glyphs collapse to '?' there only. These tests lock in both facts so a future
/// Identity-H/ToUnicode upgrade has to consciously update them.
/// </summary>
public class SearchablePdfUnicodeTests
{
    private static OcrResult OneLine(string text)
    {
        var poly = new OcrPoint[] { new(10, 10), new(190, 10), new(190, 40), new(10, 40) };
        return new OcrResult
        {
            FullText = text,
            Lines = new[] { new OcrLine { Text = text, Confidence = 0.9, BoundingPolygon = poly, BoundingBox = OcrBoundingBox.FromPoints(poly) } },
            Languages = new[] { "xx" },
        };
    }

    private static string BuildPdfText(OcrResult ocr)
    {
        using var page = new Image<Rgb24>(200, 60, new Rgb24(255, 255, 255));
        var builder = new SearchablePdfBuilder();
        builder.AddPage(page, ocr, 150, 80);
        return Encoding.Latin1.GetString(builder.Build());
    }

    [Fact]
    public void DataModel_and_json_preserve_full_unicode()
    {
        // The recognized text itself is never lossy — only the PDF text layer is.
        var result = OneLine("Привет 世界 café");
        var json = result.ToJson();

        var back = System.Text.Json.JsonSerializer.Deserialize(json, EasyOcrJsonContext.Default.OcrResult);
        Assert.Equal("Привет 世界 café", back!.FullText);
        Assert.Equal("Привет 世界 café", back.Lines[0].Text);
    }

    [Fact]
    public void Latin1_special_characters_are_escaped_and_preserved_in_pdf_layer()
    {
        // Accented Latin-1 (ç, °) survive as raw WinAnsi bytes; PDF-syntax characters ( ) and \ are
        // escaped, not dropped.
        var pdf = BuildPdfText(OneLine("Façade (n°1) \\ ok"));

        Assert.Contains("Façade", pdf); // ç (0xE7) preserved
        Assert.Contains("n°1", pdf);    // ° (0xB0) preserved
        Assert.Contains(@"\(", pdf);          // '(' escaped
        Assert.Contains(@"\)", pdf);          // ')' escaped
        Assert.Contains(@"\\", pdf);          // '\' escaped
        // The document still parses as a structurally valid PDF.
        Assert.StartsWith("%PDF-1.7", pdf);
        Assert.Contains("%%EOF", pdf);
    }

    [Fact]
    public void NonLatin_text_collapses_to_placeholder_in_pdf_layer_only()
    {
        var pdf = BuildPdfText(OneLine("Привет 世界"));

        int tj = pdf.IndexOf(") Tj", StringComparison.Ordinal);
        Assert.True(tj > 0, "expected a Tj text-show operator");
        int open = pdf.LastIndexOf('(', tj);
        var shown = pdf.Substring(open + 1, tj - open - 1);

        // Known limitation: outside Latin-1 every glyph becomes '?'. Documented, intentional.
        Assert.Matches("^[? ]+$", shown);
        Assert.Contains("??", shown);
    }
}
