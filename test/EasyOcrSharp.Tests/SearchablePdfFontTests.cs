using System.Text;
using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Pdf.Internal;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// The Unicode text layer of a searchable PDF: the pure pieces (decision table, glyph hex, /W array,
/// /ToUnicode CMap, subset tag) run everywhere with no font and no network; the handful of tests that
/// need a real TrueType file on the machine are <see cref="SkippableFactAttribute"/>.
/// </summary>
public class SearchablePdfFontTests
{
    // ---- helpers -------------------------------------------------------------------------------

    private static OcrLine Line(string text, double minX, double minY, double maxX, double maxY, params string[] words)
    {
        var poly = new OcrPoint[] { new(minX, minY), new(maxX, minY), new(maxX, maxY), new(minX, maxY) };
        var wordList = new List<OcrWord>();
        if (words.Length > 0)
        {
            double step = (maxX - minX) / words.Length;
            for (int i = 0; i < words.Length; i++)
            {
                var wordPoly = new OcrPoint[]
                {
                    new(minX + (i * step), minY),
                    new(minX + ((i + 1) * step), minY),
                    new(minX + ((i + 1) * step), maxY),
                    new(minX + (i * step), maxY),
                };
                wordList.Add(new OcrWord
                {
                    Text = words[i],
                    Confidence = 0.9,
                    BoundingPolygon = wordPoly,
                    BoundingBox = OcrBoundingBox.FromPoints(wordPoly),
                });
            }
        }

        return new OcrLine
        {
            Text = text,
            Confidence = 0.9,
            BoundingPolygon = poly,
            BoundingBox = OcrBoundingBox.FromPoints(poly),
            Words = wordList,
        };
    }

    private static OcrResult Result(params OcrLine[] lines) => new()
    {
        FullText = string.Join("\n", lines.Select(l => l.Text)),
        Lines = lines,
        Languages = new[] { "xx" },
    };

    private static (byte[] Bytes, SearchablePdfBuilder Builder) BuildPdf(OcrResult ocr, PdfOcrOptions? options = null)
    {
        using var page = new Image<Rgb24>(240, 80, new Rgb24(255, 255, 255));
        var builder = new SearchablePdfBuilder(options);
        builder.AddPage(page, ocr, 150, 80);
        return (builder.Build(), builder);
    }

    private static string Text(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    /// <summary>A system font able to render the given text, or null when the machine has none.</summary>
    private static TrueTypeFont? ProbeFont(string sample)
    {
        var codePoints = new HashSet<int>();
        foreach (var rune in sample.EnumerateRunes())
        {
            codePoints.Add(rune.Value);
        }

        return SystemFontProbe.Resolve(null, codePoints);
    }

    // ---- decision table ------------------------------------------------------------------------

    [Theory]
    [InlineData(PdfTextLayerFontMode.Auto, true, false)]    // Latin-1 only -> keep Helvetica
    [InlineData(PdfTextLayerFontMode.Auto, false, true)]    // needs a real font
    [InlineData(PdfTextLayerFontMode.Never, true, false)]
    [InlineData(PdfTextLayerFontMode.Never, false, false)]  // opted out: '?' placeholders instead
    [InlineData(PdfTextLayerFontMode.Always, true, true)]
    [InlineData(PdfTextLayerFontMode.Always, false, true)]
    public void Font_embedding_decision_table(PdfTextLayerFontMode mode, bool latin1Only, bool expected)
    {
        Assert.Equal(expected, SearchablePdfBuilder.RequiresEmbeddedFont(mode, latin1Only));
    }

    [Fact]
    public void Latin1_text_keeps_the_base14_layer_byte_for_byte()
    {
        var ocr = Result(Line("Facade (n1) total 42.00 EUR", 10, 10, 220, 40));

        var auto = BuildPdf(ocr, new PdfOcrOptions { TextLayerFont = PdfTextLayerFontMode.Auto });
        var never = BuildPdf(ocr, new PdfOcrOptions { TextLayerFont = PdfTextLayerFontMode.Never });

        Assert.Equal(never.Bytes, auto.Bytes);
        Assert.Equal(PdfTextLayerFontStatus.Standard, auto.Builder.TextLayerFontStatus);
        Assert.Null(auto.Builder.TextLayerFontPath);

        var text = Text(auto.Bytes);
        Assert.Contains("/BaseFont /Helvetica", text);
        Assert.Contains("/Encoding /WinAnsiEncoding", text);
        Assert.Contains("(Facade \\(n1\\) total 42.00 EUR) Tj", text);
        Assert.DoesNotContain("/Type0", text);
        Assert.DoesNotContain("/FontFile2", text);
    }

    [Fact]
    public void Opting_out_of_embedding_keeps_the_placeholder_behaviour()
    {
        var (bytes, builder) = BuildPdf(
            Result(Line("日本語のテキスト", 10, 10, 220, 40)),
            new PdfOcrOptions { TextLayerFont = PdfTextLayerFontMode.Never });

        var text = Text(bytes);
        Assert.Equal(PdfTextLayerFontStatus.Standard, builder.TextLayerFontStatus);
        Assert.Contains("/BaseFont /Helvetica", text);
        Assert.Contains("(????????) Tj", text);
        Assert.StartsWith("%PDF-1.7", text);
        Assert.EndsWith("%%EOF\n", text);
    }

    [Fact]
    public void An_unusable_explicit_font_never_throws()
    {
        var options = new PdfOcrOptions
        {
            TextLayerFontPath = Path.Combine(Path.GetTempPath(), "no-such-font-" + Guid.NewGuid().ToString("N") + ".ttf"),
        };

        var (bytes, builder) = BuildPdf(Result(Line("Ελληνικά", 10, 10, 220, 40)), options);

        // Either a system font stepped in or nothing did, but the document is always written.
        Assert.True(builder.TextLayerFontStatus is PdfTextLayerFontStatus.Embedded or PdfTextLayerFontStatus.Unavailable);
        Assert.StartsWith("%PDF-1.7", Text(bytes));
        Assert.EndsWith("%%EOF\n", Text(bytes));
    }

    [Fact]
    public void Options_validate_the_new_font_settings()
    {
        Assert.Throws<ArgumentException>(() => new PdfOcrOptions { TextLayerFontPath = "   " }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfOcrOptions { TextLayerFont = (PdfTextLayerFontMode)99 }.Validate());

        var defaults = new PdfOcrOptions();
        defaults.Validate();
        Assert.Equal(PdfTextLayerFontMode.Auto, defaults.TextLayerFont);
        Assert.Null(defaults.TextLayerFontPath);
    }

    // ---- pure PDF-object builders --------------------------------------------------------------

    [Fact]
    public void Glyph_ids_are_written_as_four_digit_hex()
    {
        Assert.Equal("<00480065006C006C006F>", SearchablePdfBuilder.ToGlyphHex(new ushort[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }));
        Assert.Equal("<>", SearchablePdfBuilder.ToGlyphHex(Array.Empty<ushort>()));
        Assert.Equal("<FFFF0001>", SearchablePdfBuilder.ToGlyphHex(new ushort[] { 0xFFFF, 1 }));
    }

    [Fact]
    public void ToUnicode_values_are_utf16be_including_surrogate_pairs()
    {
        Assert.Equal("0041", SearchablePdfBuilder.ToUtf16BeHex("A"));
        Assert.Equal("4E16754C", SearchablePdfBuilder.ToUtf16BeHex("世界"));
        Assert.Equal("D834DD1E", SearchablePdfBuilder.ToUtf16BeHex(char.ConvertFromUtf32(0x1D11E))); // astral
    }

    [Fact]
    public void Width_array_groups_consecutive_glyph_ids()
    {
        var widths = new Dictionary<ushort, int> { [10] = 250, [3] = 500, [4] = 600, [5] = 600 };

        Assert.Equal("[3 [500 600 600] 10 [250]]", SearchablePdfBuilder.BuildWidthArray(widths));
        Assert.Equal("[]", SearchablePdfBuilder.BuildWidthArray(new Dictionary<ushort, int>()));
        Assert.Equal("[7 [432]]", SearchablePdfBuilder.BuildWidthArray(new Dictionary<ushort, int> { [7] = 432 }));
    }

    [Fact]
    public void ToUnicode_cmap_is_well_formed_and_blocks_at_100_entries()
    {
        var map = new Dictionary<ushort, string>();
        for (ushort gid = 1; gid <= 150; gid++)
        {
            map[gid] = ((char)('A' + (gid % 26))).ToString();
        }

        var cmap = SearchablePdfBuilder.BuildToUnicodeCMap(map);

        Assert.Contains("/CMapType 2 def", cmap);
        Assert.Contains("/CMapName /Adobe-Identity-UCS def", cmap);
        Assert.Contains("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange", cmap);
        Assert.Contains("100 beginbfchar", cmap);   // first full block
        Assert.Contains("50 beginbfchar", cmap);    // remainder
        Assert.Equal(2, cmap.Split("beginbfchar").Length - 1);
        Assert.Equal(2, cmap.Split("endbfchar").Length - 1);
        Assert.EndsWith("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n", cmap);

        // Entries are ordered by glyph id and carry the UTF-16BE value.
        Assert.Contains("<0001> <0042>\n", cmap);
        Assert.Contains("<0096> <", cmap);
    }

    [Fact]
    public void Subset_tag_is_six_uppercase_letters_and_deterministic()
    {
        var tag = SearchablePdfBuilder.SubsetTag("Arial", new ushort[] { 3, 40, 41 });

        Assert.Equal(6, tag.Length);
        Assert.All(tag, c => Assert.True(c is >= 'A' and <= 'Z', $"'{c}' is not an uppercase letter"));
        Assert.Equal(tag, SearchablePdfBuilder.SubsetTag("Arial", new ushort[] { 3, 40, 41 }));
        Assert.NotEqual(tag, SearchablePdfBuilder.SubsetTag("Arial", new ushort[] { 3, 40, 42 }));
        Assert.NotEqual(tag, SearchablePdfBuilder.SubsetTag("Tahoma", new ushort[] { 3, 40, 41 }));
    }

    // ---- font-dependent behaviour --------------------------------------------------------------

    [SkippableFact]
    public void NonLatin_text_is_written_through_an_embedded_identity_h_font()
    {
        const string sample = "Привет Ελληνικά";
        Skip.If(ProbeFont(sample) is null, "No installed TrueType font covers Cyrillic + Greek on this machine.");

        var (bytes, builder) = BuildPdf(Result(Line(sample, 10, 10, 220, 40)));
        var text = Text(bytes);

        Assert.Equal(PdfTextLayerFontStatus.Embedded, builder.TextLayerFontStatus);
        Assert.NotNull(builder.TextLayerFontPath);
        Assert.True(File.Exists(builder.TextLayerFontPath));

        Assert.Contains("/Subtype /Type0", text);
        Assert.Contains("/Encoding /Identity-H", text);
        Assert.Contains("/Subtype /CIDFontType2", text);
        Assert.Contains("/CIDToGIDMap /Identity", text);
        Assert.Contains("/FontFile2", text);
        Assert.Contains("/Length1", text);
        Assert.Contains("/ToUnicode", text);
        Assert.Contains("/W [", text);
        Assert.Contains("beginbfchar", text);
        Assert.Contains("3 Tr", text);              // still invisible
        Assert.Contains("> Tj", text);              // hex string, not a literal string
        Assert.DoesNotContain("?) Tj", text);        // and no '?' placeholder run anywhere
        Assert.DoesNotContain("/Helvetica", text);
        Assert.StartsWith("%PDF-1.7", text);
        Assert.EndsWith("%%EOF\n", text);
    }

    [SkippableFact]
    public void Word_geometry_produces_one_positioned_run_per_word()
    {
        const string sample = "Привет мир друзья";
        Skip.If(ProbeFont(sample) is null, "No installed TrueType font covers Cyrillic on this machine.");

        var withWords = Result(Line(sample, 10, 10, 220, 40, "Привет", "мир", "друзья"));
        var withoutWords = Result(Line(sample, 10, 10, 220, 40));

        string wordText = Text(BuildPdf(withWords).Bytes);
        string lineText = Text(BuildPdf(withoutWords).Bytes);

        Assert.Equal(3, wordText.Split("> Tj").Length - 1);
        Assert.Equal(1, lineText.Split("> Tj").Length - 1);
        Assert.Contains(" Tz", wordText);   // width-matched to the recognized ink
    }

    [SkippableFact]
    public void Subsetting_keeps_the_requested_glyphs_and_shrinks_the_font()
    {
        var font = ProbeFont("Hello");
        Skip.If(font is null, "No usable TrueType font found on this machine.");

        Assert.True(font!.TryGetGlyphId('H', out ushort h));
        Assert.True(font.TryGetGlyphId('e', out ushort e));

        var subsetBytes = font.CreateSubset(new[] { h, e });

        // A subset intentionally ships without a 'cmap': the PDF maps characters to glyphs itself via
        // Identity-H and /CIDToGIDMap /Identity, so the parser has to be told not to insist on one.
        var subset = TrueTypeFont.TryParse(subsetBytes, font.FilePath, requireCmap: false);

        Assert.NotNull(subset);
        Assert.Equal(font.GlyphCount, subset!.GlyphCount);
        Assert.Equal(font.UnitsPerEm, subset.UnitsPerEm);
        Assert.Equal(font.GetAdvanceWidth(h), subset.GetAdvanceWidth(h));
        Assert.True(subsetBytes.Length < new FileInfo(font.FilePath).Length,
            "the subset should be smaller than the font it came from");
    }

    [Fact]
    public void Probing_reports_no_coverage_for_a_file_that_is_not_a_font()
    {
        var temp = Path.Combine(Path.GetTempPath(), "not-a-font-" + Guid.NewGuid().ToString("N") + ".ttf");
        File.WriteAllBytes(temp, Encoding.ASCII.GetBytes("this is definitely not an sfnt file at all"));
        try
        {
            Assert.Equal(-1, TrueTypeFont.CountCoverage(temp, new[] { (int)'A' }));
            Assert.Null(TrueTypeFont.TryLoad(temp));
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
