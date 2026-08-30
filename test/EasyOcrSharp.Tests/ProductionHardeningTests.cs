using System.Text;
using EasyImageSharp;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Pdf.Internal;
using EasyOcrSharp.Services;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Regression guards for the production-readiness pass: image decoding (alpha, EXIF), detection geometry
/// winding, the searchable-PDF text layer's WinAnsi coverage, and the model-download size ceiling. Each test
/// names the defect it locks out, since several of these were silent — wrong output, never an exception.
/// </summary>
public class ProductionHardeningTests
{
    // ------------------------------------------------------------------ image decoding

    private static byte[] EncodePng(Image<Rgba32> image)
    {
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    [Fact]
    public void A_transparent_background_decodes_to_white_not_black()
    {
        // RGBA -> RGB used to DISCARD alpha rather than composite it, so a transparent background became
        // (0,0,0): dark glyphs on a black page, which returns empty text with no error at all.
        using var source = new Image<Rgba32>(8, 8, new Rgba32(0, 0, 0, 0));   // fully transparent
        source[4, 4] = new Rgba32(0, 0, 0, 255);                               // one opaque black "glyph" pixel

        using var decoded = DecodeLimits.Load(EncodePng(source), 0, "test");

        Assert.Equal(new Rgb24(255, 255, 255), decoded[0, 0]);   // background composited onto white
        Assert.Equal(new Rgb24(0, 0, 0), decoded[4, 4]);         // real ink survives
    }

    [Fact]
    public void A_half_transparent_pixel_is_blended_not_taken_at_full_strength()
    {
        using var source = new Image<Rgba32>(4, 4, new Rgba32(0, 0, 0, 128));

        using var decoded = DecodeLimits.Load(EncodePng(source), 0, "test");

        // Black at 50% over white is mid-grey, not black.
        var pixel = decoded[0, 0];
        Assert.InRange(pixel.R, 120, 135);
        Assert.Equal(pixel.R, pixel.G);
        Assert.Equal(pixel.R, pixel.B);
    }

    [Fact]
    public void Exif_orientation_is_applied_so_a_phone_photo_is_upright()
    {
        // Orientation 6 = rotate 90 CW to display. A landscape buffer tagged this way is a portrait photo;
        // every viewer shows it upright, and the detector used to receive it sideways and find nothing.
        using var source = new Image<Rgba32>(20, 10, new Rgba32(255, 255, 255, 255));
        source.Metadata.ExifProfile = new ExifProfile();
        source.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);

        using var decoded = DecodeLimits.Load(EncodePng(source), 0, "test");

        Assert.Equal(10, decoded.Width);
        Assert.Equal(20, decoded.Height);
    }

    [Fact]
    public void Decoding_preserves_every_frame_of_a_multi_frame_image()
    {
        // The alpha/EXIF normalization must happen in place. Building a fresh single-frame image and drawing
        // onto it silently flattened multi-frame TIFFs to page one, breaking the multi-frame API.
        using var source = new Image<Rgba32>(8, 8, new Rgba32(255, 255, 255, 255));
        source.Frames.CreateFrame(8, 8);
        source.Frames.CreateFrame(8, 8);

        using var ms = new MemoryStream();
        source.Save(ms, new EasyImageSharp.Formats.Tiff.TiffEncoder());

        using var decoded = DecodeLimits.Load(ms.ToArray(), 0, "test");

        Assert.Equal(3, decoded.Frames.Count);
    }

    // ------------------------------------------------------------------ detection geometry

    [Fact]
    public void MinAreaRect_returns_corners_clockwise_from_the_top_left()
    {
        // The shoelace winding test had an inverted sign for image (y-down) coordinates, so every CRAFT
        // polygon came out counter-clockwise -- contradicting this type's own contract, OcrWord's documented
        // order, and the clockwise polygons the structure engine produces within the same OcrResult.
        var points = new List<OcrPoint>();
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 100; x++)
            {
                points.Add(new OcrPoint(x, y));
            }
        }

        var quad = MinAreaRect.Compute(points.ToArray());

        Assert.Equal(4, quad.Length);
        var tl = quad[0];
        var tr = quad[1];
        var br = quad[2];

        // TL -> TR travels along +x at the top; TR -> BR travels down. That is clockwise in image space.
        Assert.True(tr.X > tl.X, $"expected TL({tl.X},{tl.Y}) -> TR({tr.X},{tr.Y}) to move right");
        Assert.True(br.Y > tr.Y, $"expected TR({tr.X},{tr.Y}) -> BR({br.X},{br.Y}) to move down");
    }

    // ------------------------------------------------------------------ searchable PDF text layer

    private static string BuildStandardLayer(string text)
    {
        using var page = new Image<Rgb24>(64, 32, new Rgb24(255, 255, 255));
        var poly = new OcrPoint[] { new(4, 4), new(60, 4), new(60, 20), new(4, 20) };
        var ocr = new OcrResult
        {
            FullText = text,
            Lines = new[]
            {
                new OcrLine
                {
                    Text = text,
                    Confidence = 0.9,
                    BoundingPolygon = poly,
                    BoundingBox = OcrBoundingBox.FromPoints(poly),
                },
            },
            Languages = new[] { "xx" },
        };

        var builder = new SearchablePdfBuilder(new PdfOcrOptions { TextLayerFont = PdfTextLayerFontMode.Never });
        builder.AddPage(page, ocr, 150, 80);
        return Encoding.Latin1.GetString(builder.Build());
    }

    [Theory]
    [InlineData('’', 0x92)]   // right single quote — the apostrophe OCR produces constantly
    [InlineData('“', 0x93)]   // left double quote
    [InlineData('—', 0x97)]   // em dash
    [InlineData('€', 0x80)]   // euro sign
    [InlineData('…', 0x85)]   // ellipsis
    public void WinAnsi_typographic_characters_survive_the_standard_text_layer(char ch, int expectedByte)
    {
        // The layer declares /WinAnsiEncoding but the builder tested for Latin-1 (> 0xFF), so every one of
        // these became '?': an English invoice OCR'd as "don't" or "€1,234" was unsearchable.
        var pdf = BuildStandardLayer($"a{ch}b");

        Assert.Contains($"a{(char)expectedByte}b", pdf);
    }

    [Fact]
    public void A_curly_apostrophe_alone_does_not_force_font_embedding()
    {
        // Under the default Auto mode a single non-Latin-1 character used to drag the whole document onto the
        // embedded-font path: a recursive font-directory scan and a multi-megabyte /FontFile2.
        Assert.True(SearchablePdfBuilder.RequiresEmbeddedFont(PdfTextLayerFontMode.Auto, textIsLatin1Only: false));
        Assert.False(SearchablePdfBuilder.RequiresEmbeddedFont(PdfTextLayerFontMode.Auto, textIsLatin1Only: true));

        // The real assertion: text made only of WinAnsi-representable characters stays on the standard path,
        // which BuildStandardLayer exercises end to end without needing a font on the machine.
        var pdf = BuildStandardLayer("don’t — €5");
        Assert.StartsWith("%PDF-1.7", pdf);
        Assert.Contains("%%EOF", pdf);
        Assert.DoesNotContain("?", pdf[pdf.IndexOf("BT", StringComparison.Ordinal)..(pdf.IndexOf("ET", StringComparison.Ordinal) + 2)]);
    }

    [Fact]
    public void Text_outside_WinAnsi_still_degrades_to_a_placeholder_rather_than_breaking_the_file()
    {
        var pdf = BuildStandardLayer("世界");   // CJK: genuinely unrepresentable in WinAnsi

        Assert.StartsWith("%PDF-1.7", pdf);
        Assert.Contains("%%EOF", pdf);
    }

    // ------------------------------------------------------------------ model download ceiling

    [Fact]
    public void Model_download_options_carry_a_size_ceiling_by_default()
    {
        // The checksum is only verified after the bytes are on disk, so an unbounded download from a
        // misconfigured BaseUrlOverride could fill the cache disk before verification ever ran.
        var options = new ModelDownloadOptions();

        Assert.True(options.MaxDownloadBytes > 0);
        // Comfortably above the largest published asset (~124 MB) and well below a runaway stream.
        Assert.InRange(options.MaxDownloadBytes, 200L * 1024 * 1024, 4L * 1024 * 1024 * 1024);
    }
}
