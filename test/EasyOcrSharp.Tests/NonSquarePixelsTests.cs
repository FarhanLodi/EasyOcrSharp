using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Model-free tests for <see cref="PreprocessingOptions.CorrectNonSquarePixels"/>: which images are resampled
/// (and, as importantly, which are provably left alone), the resampling math, and the way coordinates are
/// scaled back onto the caller's grid.
/// </summary>
public class NonSquarePixelsTests
{
    [Fact]
    public void Correction_is_on_by_default_and_not_counted_as_a_cleanup_step()
    {
        Assert.True(PreprocessingOptions.None.CorrectNonSquarePixels);
        Assert.True(RecognitionOptions.Default.Preprocessing.CorrectNonSquarePixels);
        Assert.False(PreprocessingOptions.None.IsAnyEnabled);
    }

    [Theory]
    [InlineData(204, 98, 1.0, 204.0 / 98)]   // standard fax
    [InlineData(98, 204, 204.0 / 98, 1.0)]   // transposed fax
    [InlineData(200, 100, 1.0, 2.0)]
    public void TryGetScale_brings_the_lower_resolution_axis_up_to_the_higher(double x, double y, double sx, double sy)
    {
        Assert.True(NonSquarePixels.TryGetScale(x, y, out double scaleX, out double scaleY));
        Assert.Equal(sx, scaleX, 9);
        Assert.Equal(sy, scaleY, 9);
    }

    [Theory]
    [InlineData(96, 96)]          // EasyImageSharp's default when a file carries no resolution
    [InlineData(300, 300)]
    [InlineData(72, 72)]
    [InlineData(1, 1)]            // aspect-ratio-only metadata (JFIF unit 0, PNG unit 0)
    [InlineData(204, 196)]        // "fine" fax: 4 % apart, within tolerance
    [InlineData(300, 330)]        // 10 %
    [InlineData(0, 96)]
    [InlineData(96, 0)]
    [InlineData(-96, 96)]
    [InlineData(double.NaN, 96)]
    [InlineData(double.PositiveInfinity, 96)]
    [InlineData(500, 100)]        // 5:1 — implausible, treated as broken metadata
    public void TryGetScale_declines_square_unknown_and_implausible_resolutions(double x, double y)
    {
        Assert.False(NonSquarePixels.TryGetScale(x, y, out double sx, out double sy));
        Assert.Equal(1.0, sx);
        Assert.Equal(1.0, sy);
    }

    [Fact]
    public void Images_without_or_with_square_resolution_get_no_copy()
    {
        using var noMetadata = new Image<Rgb24>(300, 200, new Rgb24(255, 255, 255));
        Assert.Null(NonSquarePixels.CreateSquarePixelCopy(noMetadata));

        using var square = new Image<Rgb24>(300, 200, new Rgb24(255, 255, 255));
        square.Metadata.SetResolution(300, 300, PixelResolutionUnit.PixelsPerInch);
        Assert.Null(NonSquarePixels.CreateSquarePixelCopy(square));
    }

    [Fact]
    public void A_fax_page_is_resampled_to_square_pixels()
    {
        using var fax = new Image<Rgb24>(1728, 1100, new Rgb24(255, 255, 255));
        fax.Metadata.SetResolution(204, 98, PixelResolutionUnit.PixelsPerInch);

        using var copy = NonSquarePixels.CreateSquarePixelCopy(fax);

        Assert.NotNull(copy);
        Assert.Equal(1728, copy!.Width);
        Assert.Equal((int)Math.Round(1100 * 204.0 / 98), copy.Height);
        // The caller's image is untouched.
        Assert.Equal(1100, fax.Height);
    }

    [Fact]
    public void Per_frame_exif_resolution_wins_over_image_metadata()
    {
        // A page cloned out of a multi-page TIFF carries its own resolution tags on the frame.
        using var page = new Image<Rgb24>(400, 200, new Rgb24(255, 255, 255));
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.XResolution, new Rational(204, 1));
        exif.SetValue(ExifTag.YResolution, new Rational(98, 1));
        page.Frames.RootFrame.Metadata.ExifProfile = exif;

        var (x, y) = NonSquarePixels.GetResolution(page);
        Assert.Equal(204, x, 6);
        Assert.Equal(98, y, 6);
    }

    [Fact]
    public void A_png_declaring_fax_resolution_is_detected_after_a_real_decode()
    {
        using var fax = new Image<Rgb24>(600, 300, new Rgb24(255, 255, 255));
        fax.Metadata.SetResolution(204, 98, PixelResolutionUnit.PixelsPerInch);
        using var png = new MemoryStream();
        fax.SaveAsPng(png);

        using var decoded = DecodeLimits.Load(png.ToArray(), 0, "test");
        using var copy = NonSquarePixels.CreateSquarePixelCopy(decoded);

        Assert.NotNull(copy);
        Assert.Equal(600, copy!.Width);
        Assert.InRange(copy.Height, 623, 626); // 300 × 2.0816, after the PNG pHYs pixels-per-metre rounding
    }

    /// <summary>
    /// The default-on decision rests on this: every image the suite OCRs — the 100-page corpus and the other
    /// fixtures — decodes with square or absent resolution, so the correction leaves all of them on the exact
    /// pre-existing path (no copy is made).
    /// </summary>
    [SkippableFact]
    public void Every_bundled_fixture_is_left_on_the_unchanged_path()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "assets");
        Skip.If(!Directory.Exists(dir), "test assets not copied");
        var files = Directory.EnumerateFiles(dir, "*.png").ToList();
        Skip.If(files.Count == 0, "no PNG fixtures present");

        var corrected = new List<string>();
        foreach (var file in files)
        {
            using var image = DecodeLimits.Load(File.ReadAllBytes(file), 0, "test");
            using var copy = NonSquarePixels.CreateSquarePixelCopy(image);
            if (copy is not null) corrected.Add(Path.GetFileName(file));
        }

        Assert.True(corrected.Count == 0, "fixtures that would be resampled: " + string.Join(", ", corrected));
    }

    [Fact]
    public void Orientation_transposing_decode_swaps_the_resolution_axes()
    {
        using var image = new Image<Rgba32>(40, 20);
        image.Metadata.SetResolution(204, 98, PixelResolutionUnit.PixelsPerInch);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.XResolution, new Rational(204, 1));
        exif.SetValue(ExifTag.YResolution, new Rational(98, 1));
        image.Frames.RootFrame.Metadata.ExifProfile = exif;

        DecodeLimits.SwapResolutionAxes(image);

        Assert.Equal(98, image.Metadata.HorizontalResolution, 6);
        Assert.Equal(204, image.Metadata.VerticalResolution, 6);
        Assert.True(exif.TryGetValue(ExifTag.XResolution, out var x));
        Assert.Equal(98, x!.Value.ToDouble(), 6);
    }

    [Theory]
    [InlineData(0, 1.0, 0.5)]
    [InlineData(180, 1.0, 0.5)]
    [InlineData(90, 0.5, 1.0)]
    [InlineData(270, 0.5, 1.0)]
    [InlineData(-90, 0.5, 1.0)]
    public void Mapping_factors_follow_the_document_rotation(int rotation, double fx, double fy)
    {
        // A 1000×500 page squared to 1000×1000: y was stretched 2×, so y maps back by 0.5.
        var mapping = SquarePixelMapping.For(1000, 500, 1000, 1000, rotation);
        Assert.Equal(fx, mapping.FactorX, 9);
        Assert.Equal(fy, mapping.FactorY, 9);
    }

    [Fact]
    public void Region_on_the_square_copy_maps_back_to_the_caller_region()
    {
        var mapping = SquarePixelMapping.For(1000, 500, 1000, 1000);
        var region = OcrRegion.Pixels(100, 50, 300, 200);

        var onCopy = mapping.RegionToSquare(region);
        Assert.Equal(new OcrPoint(100, 100), new OcrPoint(onCopy.X, onCopy.Y));
        Assert.Equal(400, onCopy.Height, 9);

        var back = mapping.ToSource(new OcrPoint(onCopy.X + onCopy.Width, onCopy.Y + onCopy.Height));
        Assert.Equal(400, back.X, 9);
        Assert.Equal(250, back.Y, 9);

        var fraction = OcrRegion.Fraction(0, 0.5, 1, 0.5);
        Assert.Equal(fraction, mapping.RegionToSquare(fraction));
    }

    [Fact]
    public void Result_lines_words_and_characters_are_scaled_back_onto_the_caller_grid()
    {
        var mapping = SquarePixelMapping.For(1728, 1100, 1728, 2290);
        var line = LineAt(100, 400, 500, 440, withWords: true);
        var result = new OcrResult
        {
            FullText = line.Text,
            Lines = new[] { line },
            Languages = new[] { "en" },
            SourceWidth = 1728,
            SourceHeight = 2290,
        };

        var mapped = EasyOcrService.ToCallerFrame(result, null, 0f, mapping);

        Assert.Equal(1728, mapped.SourceWidth);
        Assert.Equal(1100, mapped.SourceHeight);
        Assert.Equal(result.FullText, mapped.FullText);
        double f = 1100.0 / 2290;
        var box = mapped.Lines[0].BoundingBox;
        Assert.Equal(400 * f, box.MinY, 6);
        Assert.Equal(440 * f, box.MaxY, 6);
        Assert.Equal(100, box.MinX, 6);
        Assert.Equal(400 * f, mapped.Lines[0].Words[0].BoundingBox.MinY, 6);
        Assert.Equal(440 * f, mapped.Lines[0].Characters[0].BoundingBox.MaxY, 6);
        Assert.Equal(0f, mapped.DeskewAngle);
    }

    [Fact]
    public void Nothing_to_map_returns_the_very_same_result()
    {
        var result = new OcrResult { FullText = "x", Lines = new[] { LineAt(0, 0, 10, 10, withWords: true) }, Languages = new[] { "en" } };
        Assert.Same(result, EasyOcrService.ToCallerFrame(result, null, 0f, null));
    }

    [Fact]
    public void LineGeometry_maps_every_coordinate_and_keeps_geometry_less_entries()
    {
        var line = LineAt(10, 20, 110, 40, withWords: true);
        var newline = new OcrChar { Value = "\n", Confidence = 0 };
        line = line with { Characters = line.Characters.Append(newline).ToArray() };

        var moved = LineGeometry.Transform(line, p => new OcrPoint(p.X * 2, p.Y + 5));

        Assert.Equal(new OcrBoundingBox(20, 25, 220, 45), moved.BoundingBox);
        Assert.Equal(new OcrBoundingBox(20, 25, 120, 45), moved.Words[0].BoundingBox);
        Assert.Equal(new OcrBoundingBox(20, 25, 40, 45), moved.Characters[0].BoundingBox);
        Assert.Same(newline, moved.Characters[^1]);
        Assert.Equal(line.Text, moved.Text);
    }

    [Fact]
    public void TranslateLines_also_moves_word_and_character_boxes()
    {
        var line = LineAt(10, 20, 110, 40, withWords: true);
        var t = EasyOcrService.TranslateLines(new[] { line }, 5, 7)[0];

        Assert.Equal(new OcrBoundingBox(15, 27, 65, 47), t.Words[0].BoundingBox);
        Assert.Equal(new OcrBoundingBox(15, 27, 25, 47), t.Characters[0].BoundingBox);
    }

    /// <summary>A line with one word spanning its left half and one character spanning its first tenth.</summary>
    internal static OcrLine LineAt(double minX, double minY, double maxX, double maxY, bool withWords)
    {
        static OcrPoint[] Quad(double x0, double y0, double x1, double y1)
            => new[] { new OcrPoint(x0, y0), new OcrPoint(x1, y0), new OcrPoint(x1, y1), new OcrPoint(x0, y1) };

        double midX = minX + ((maxX - minX) / 2), tenthX = minX + ((maxX - minX) / 10);
        var line = new OcrLine
        {
            Text = "word rest",
            Confidence = 0.9,
            BoundingPolygon = Quad(minX, minY, maxX, maxY),
            BoundingBox = new OcrBoundingBox(minX, minY, maxX, maxY),
        };
        if (!withWords) return line;

        return line with
        {
            Words = new[]
            {
                new OcrWord { Text = "word", Confidence = 0.9, BoundingPolygon = Quad(minX, minY, midX, maxY), BoundingBox = new OcrBoundingBox(minX, minY, midX, maxY) },
            },
            Characters = new[]
            {
                new OcrChar { Value = "w", Confidence = 0.9, BoundingPolygon = Quad(minX, minY, tenthX, maxY), BoundingBox = new OcrBoundingBox(minX, minY, tenthX, maxY) },
            },
        };
    }
}
