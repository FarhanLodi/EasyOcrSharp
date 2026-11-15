using EasyOcrSharp.Export;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Model-backed checks for the detection and input options — <see cref="DetectionOptions.TileLargeImages"/>,
/// <see cref="DetectionOptions.MinTextHeight"/>, <see cref="DetectionOptions.EnhanceContrast"/>,
/// <see cref="PreprocessingOptions.CorrectNonSquarePixels"/> and the <see cref="PreprocessingOptions.Deskew"/>
/// coordinate fix. They pin invariants (options off = today's output, coordinates on the caller's image, no
/// seam duplicates) and the wins that are not in doubt; the before/after accuracy numbers come from
/// <see cref="DetectionInputBenchmarkTests"/>. Needs the CRAFT + latin models; skips when the corpus is absent.
/// </summary>
[Trait("Category", "Integration")]
[Collection(OcrIntegrationCollection.Name)]
public sealed class DetectionInputIntegrationTests
{
    private static readonly string[] En = { "en" };
    private readonly ITestOutputHelper _out;

    public DetectionInputIntegrationTests(ITestOutputHelper output) => _out = output;

    private static Image<Rgb24> LoadOrSkip(string name)
    {
        var path = TestAssets.Image(name);
        Skip.If(path is null, $"{name} not present");
        return DecodeLimits.Load(File.ReadAllBytes(path!), 0, "test");
    }

    /// <summary>Every new switch at its "off" value, spelled out.</summary>
    private static RecognitionOptions AllOff => new()
    {
        Detection = new DetectionOptions { TileLargeImages = false, MinTextHeight = 0, EnhanceContrast = false },
        Preprocessing = new PreprocessingOptions { CorrectNonSquarePixels = false },
    };

    // ---- options off / not triggered: exactly today's output -----------------------------------------------------

    [SkippableTheory]
    [InlineData("sample.png")]
    [InlineData("001_plain_text.png")]
    [InlineData("024_tables.png")]
    [InlineData("057_receipts_invoices.png")]
    public async Task Defaults_and_every_switch_off_give_identical_results(string name)
    {
        using var page = LoadOrSkip(name);
        await using var ocr = new EasyOcrService();

        var byDefault = await ocr.ExtractTextFromImage(page, En);
        var allOff = await ocr.ExtractTextFromImage(page, En, AllOff);

        // CorrectNonSquarePixels defaults on; on a square-pixel page it must change nothing.
        Assert.True(DegradedImages.SameLines(byDefault, allOff), $"{name}: default result differs from all-off");
        Assert.Equal(0f, byDefault.DeskewAngle);
        Assert.DoesNotContain("DeskewAngle", byDefault.ToJson());
    }

    [SkippableTheory]
    [InlineData("001_plain_text.png")]
    [InlineData("057_receipts_invoices.png")]
    public async Task Tiling_on_a_page_that_fits_the_canvas_changes_nothing(string name)
    {
        using var page = LoadOrSkip(name);
        await using var ocr = new EasyOcrService();

        var off = await ocr.ExtractTextFromImage(page, En);
        var on = await ocr.ExtractTextFromImage(page, En, new RecognitionOptions { Detection = new DetectionOptions { TileLargeImages = true } });

        Assert.True(DegradedImages.SameLines(off, on));
    }

    [SkippableTheory]
    [InlineData("001_plain_text.png")]
    [InlineData("024_tables.png")]
    public async Task Small_text_rescue_leaves_full_resolution_pages_alone_when_it_does_not_trigger(string name)
    {
        using var page = LoadOrSkip(name);
        await using var ocr = new EasyOcrService();

        // Measure what the rescue would see: the raw CRAFT boxes (word grouping, no NMS).
        var raw = await ocr.DetectRegionsAsync(page, new RecognitionOptions
        {
            Grouping = TextGrouping.Word,
            Detection = new DetectionOptions { NmsIouThreshold = 0 },
        });
        double median = DetectionTiling.MedianShortSide(raw.Select(r => r.BoundingPolygon.ToArray()).ToArray());
        double magnification = DetectionTiling.RescueMagnification(median, 16, Math.Max(page.Width, page.Height), 2560, 1.0);
        _out.WriteLine($"{name}: median box short side {median:F1}px, rescue magnification {magnification:F2}");
        Skip.If(magnification > 0, $"{name}: the rescue triggers at 16 px (median {median:F1}); identity does not apply");

        var off = await ocr.ExtractTextFromImage(page, En);
        var on = await ocr.ExtractTextFromImage(page, En, new RecognitionOptions { Detection = new DetectionOptions { MinTextHeight = 16 } });
        Assert.True(DegradedImages.SameLines(off, on));
    }

    // ---- tiling -------------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_tall_stitched_receipt_is_read_in_tiles_without_seam_duplicates()
    {
        var names = Enumerable.Range(57, 10).Select(i => $"{i:000}_receipts_invoices.png").ToArray();
        Skip.If(names.Any(n => TestAssets.Image(n) is null), "receipt pages not present");

        var pieces = names.Select(n => { using var p = LoadOrSkip(n); return DegradedImages.CropToContent(p); }).ToList();
        try
        {
            using var tall = DegradedImages.StackVertically(pieces);
            Assert.True(DetectionTiling.ShouldTile(tall.Width, tall.Height, 2560, 1.0, out _), $"{tall.Width}x{tall.Height} should tile");

            await using var ocr = new EasyOcrService();
            var transcripts = new List<string>();
            foreach (var piece in pieces) transcripts.Add((await ocr.ExtractTextFromImage(piece, En)).FullText);
            var reference = string.Join("\n", transcripts);

            var off = await ocr.ExtractTextFromImage(tall, En);
            var on = await ocr.ExtractTextFromImage(tall, En, new RecognitionOptions { Detection = new DetectionOptions { TileLargeImages = true } });

            double recallOff = DegradedImages.TokenRecall(reference, off.FullText);
            double recallOn = DegradedImages.TokenRecall(reference, on.FullText);
            _out.WriteLine($"tall receipt {tall.Width}x{tall.Height}: lines {off.Lines.Count} -> {on.Lines.Count}, " +
                           $"token recall {recallOff:P1} -> {recallOn:P1}, mean conf {DegradedImages.MeanConfidence(off):F3} -> {DegradedImages.MeanConfidence(on):F3}");

            Assert.True(recallOn >= recallOff, $"tiling lost tokens: {recallOff:P1} -> {recallOn:P1}");
            Assert.True(DegradedImages.BoxesInside(on, tall.Width, tall.Height));

            // No line read twice across a seam: identical text with overlapping boxes.
            var duplicates = on.Lines
                .SelectMany((a, i) => on.Lines.Skip(i + 1).Where(b => b.Text == a.Text && Iou(a.BoundingBox, b.BoundingBox) > 0.3).Select(b => a.Text))
                .ToList();
            Assert.True(duplicates.Count == 0, "seam duplicates: " + string.Join(" | ", duplicates));
        }
        finally
        {
            foreach (var p in pieces) p.Dispose();
        }
    }

    // ---- small text / contrast -----------------------------------------------------------------------------------

    [SkippableTheory]
    [InlineData("001_plain_text.png")]
    [InlineData("085_dense_mixed.png")]
    public async Task Small_text_rescue_on_a_half_scale_page_keeps_coordinates_on_the_page(string name)
    {
        using var page = LoadOrSkip(name);
        using var half = DegradedImages.HalfScale(page);
        await using var ocr = new EasyOcrService();

        var reference = await ocr.ExtractTextFromImage(page, En);
        var off = await ocr.ExtractTextFromImage(half, En);
        var on = await ocr.ExtractTextFromImage(half, En, new RecognitionOptions { Detection = new DetectionOptions { MinTextHeight = 16 } });

        _out.WriteLine($"{name} half-scale: lines {off.Lines.Count} -> {on.Lines.Count}, token recall " +
                       $"{DegradedImages.TokenRecall(reference.FullText, off.FullText):P1} -> {DegradedImages.TokenRecall(reference.FullText, on.FullText):P1}, " +
                       $"mean conf {DegradedImages.MeanConfidence(off):F3} -> {DegradedImages.MeanConfidence(on):F3}");

        Assert.NotEmpty(on.Lines);
        Assert.Equal(half.Width, on.SourceWidth);
        Assert.True(DegradedImages.BoxesInside(on, half.Width, half.Height));
    }

    [SkippableTheory]
    [InlineData("001_plain_text.png")]
    [InlineData("069_low_quality.png")]
    public async Task Contrast_enhanced_detection_on_a_faded_page_reads_the_original_pixels(string name)
    {
        using var page = LoadOrSkip(name);
        using var faded = DegradedImages.Faded(page);
        await using var ocr = new EasyOcrService();

        var reference = await ocr.ExtractTextFromImage(page, En);
        var off = await ocr.ExtractTextFromImage(faded, En);
        var on = await ocr.ExtractTextFromImage(faded, En, new RecognitionOptions { Detection = new DetectionOptions { EnhanceContrast = true } });

        _out.WriteLine($"{name} faded: lines {off.Lines.Count} -> {on.Lines.Count}, token recall " +
                       $"{DegradedImages.TokenRecall(reference.FullText, off.FullText):P1} -> {DegradedImages.TokenRecall(reference.FullText, on.FullText):P1}, " +
                       $"mean conf {DegradedImages.MeanConfidence(off):F3} -> {DegradedImages.MeanConfidence(on):F3}");

        Assert.NotEmpty(on.Lines);
        Assert.True(DegradedImages.BoxesInside(on, faded.Width, faded.Height));

        // Recognition is not handed the enhanced copy: recognizing the detected regions directly on the faded
        // pixels reproduces the result.
        var regions = await ocr.DetectRegionsAsync(faded, new RecognitionOptions { Detection = new DetectionOptions { EnhanceContrast = true } });
        var direct = await ocr.RecognizeRegionsAsync(faded, regions, En);
        Assert.Equal(on.FullText, direct.FullText);
    }

    // ---- non-square pixels ---------------------------------------------------------------------------------------

    [SkippableTheory]
    [InlineData("001_plain_text.png")]
    [InlineData("057_receipts_invoices.png")]
    public async Task A_fax_page_is_read_square_and_reported_on_its_own_grid(string name)
    {
        using var page = LoadOrSkip(name);
        using var fax = DegradedImages.Fax(page);
        await using var ocr = new EasyOcrService();

        var reference = await ocr.ExtractTextFromImage(page, En);
        var off = await ocr.ExtractTextFromImage(fax, En, new RecognitionOptions { Preprocessing = new PreprocessingOptions { CorrectNonSquarePixels = false } });
        var on = await ocr.ExtractTextFromImage(fax, En, new RecognitionOptions { WordLevelDetail = WordLevelDetail.Words });

        double recallOff = DegradedImages.TokenRecall(reference.FullText, off.FullText);
        double recallOn = DegradedImages.TokenRecall(reference.FullText, on.FullText);
        _out.WriteLine($"{name} 204x98 fax ({fax.Width}x{fax.Height}): lines {off.Lines.Count} -> {on.Lines.Count}, " +
                       $"token recall {recallOff:P1} -> {recallOn:P1}, mean conf {DegradedImages.MeanConfidence(off):F3} -> {DegradedImages.MeanConfidence(on):F3}");

        Assert.Equal(fax.Width, on.SourceWidth);
        Assert.Equal(fax.Height, on.SourceHeight);
        Assert.True(DegradedImages.BoxesInside(on, fax.Width, fax.Height));
        // Expected to gain (squashed glyphs read poorly); a two-point tolerance keeps resampling noise from flaking it.
        Assert.True(recallOn + 0.02 >= recallOff, $"square-pixel correction lost tokens: {recallOff:P1} -> {recallOn:P1}");

        // Lines land where the same text sits on the clean page, scaled onto the fax grid.
        double sy = (double)fax.Height / page.Height;
        foreach (var line in on.Lines)
        {
            var twin = reference.Lines.FirstOrDefault(r => r.Text == line.Text);
            if (twin is null) continue;
            Assert.True(Math.Abs(line.BoundingBox.CenterX - twin.BoundingBox.CenterX) < 12
                        && Math.Abs(line.BoundingBox.CenterY - (twin.BoundingBox.CenterY * sy)) < 8,
                $"'{line.Text}' at {line.BoundingBox} vs clean {twin.BoundingBox} (×{sy:F3} in y)");
            foreach (var word in line.Words)
            {
                Assert.True(Inside(word.BoundingBox, line.BoundingBox, 3), $"word '{word.Text}' {word.BoundingBox} outside its line {line.BoundingBox}");
            }
        }
    }

    [SkippableFact]
    public async Task A_fax_png_on_disk_is_corrected_through_the_file_overload()
    {
        using var page = LoadOrSkip("001_plain_text.png");
        using var fax = DegradedImages.Fax(page);
        var path = Path.Combine(Path.GetTempPath(), $"easyocr-fax-{Guid.NewGuid():N}.png");
        try
        {
            fax.SaveAsPng(path);
            await using var ocr = new EasyOcrService();
            var fromFile = await ocr.ExtractTextFromImage(path, En);
            var fromImage = await ocr.ExtractTextFromImage(fax, En);

            Assert.Equal(fax.Height, fromFile.SourceHeight);
            Assert.Equal(fromImage.FullText, fromFile.FullText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- deskew --------------------------------------------------------------------------------------------------

    [SkippableTheory]
    [InlineData("001_plain_text.png", 4f)]
    [InlineData("037_forms.png", -3f)]
    public async Task Deskewed_lines_and_words_are_reported_on_the_tilted_input(string name, float tilt)
    {
        using var page = LoadOrSkip(name);
        using var tilted = DegradedImages.Tilted(page, tilt);
        await using var ocr = new EasyOcrService();

        var plain = await ocr.ExtractTextFromImage(tilted, En);
        var deskewed = await ocr.ExtractTextFromImage(tilted, En, new RecognitionOptions
        {
            Preprocessing = new PreprocessingOptions { Deskew = true },
            WordLevelDetail = WordLevelDetail.Words,
        });

        // The synthetic pages are not perfectly straight to begin with: EasyImageSharp's projection deskew
        // turns them by about -0.8° (identically on 1.1.0 and 1.2.0). The deskew of the tilted page is that
        // native angle less the tilt applied here.
        var native = await ocr.ExtractTextFromImage(page, En, new RecognitionOptions
        {
            Preprocessing = new PreprocessingOptions { Deskew = true },
        });
        float expected = native.DeskewAngle - tilt;

        _out.WriteLine($"{name} tilted {tilt}°: DeskewAngle {deskewed.DeskewAngle:F2} (native {native.DeskewAngle:F2}, expected {expected:F2}), lines {plain.Lines.Count} / {deskewed.Lines.Count}");
        Assert.InRange(deskewed.DeskewAngle, expected - 0.6f, expected + 0.6f);
        Assert.Equal(tilted.Width, deskewed.SourceWidth);
        Assert.True(DegradedImages.BoxesInside(deskewed, tilted.Width, tilted.Height));

        // Before the fix these sat ~(canvas growth)/2 right of and below their ink.
        // Lines are paired by text, so text that occurs more than once on the page (037_forms.png has
        // "Farhan Lodi" twice) is skipped rather than paired with the wrong occurrence.
        int matched = 0;
        foreach (var line in deskewed.Lines)
        {
            if (deskewed.Lines.Count(l => l.Text == line.Text) != 1) continue;
            var twins = plain.Lines.Where(p => p.Text == line.Text).ToList();
            if (twins.Count != 1) continue;
            var twin = twins[0];
            matched++;
            double tolerance = Math.Max(10, 0.6 * twin.BoundingBox.Height);
            Assert.True(Math.Abs(line.BoundingBox.CenterX - twin.BoundingBox.CenterX) < tolerance
                        && Math.Abs(line.BoundingBox.CenterY - twin.BoundingBox.CenterY) < tolerance,
                $"'{line.Text}': deskewed {line.BoundingBox} vs undeskewed {twin.BoundingBox}");
            foreach (var word in line.Words)
            {
                Assert.True(Inside(word.BoundingBox, line.BoundingBox, 4), $"word '{word.Text}' {word.BoundingBox} outside its line {line.BoundingBox}");
            }
        }
        Assert.True(matched > 0, "no line read identically with and without deskew");
        Assert.Contains("DeskewAngle", deskewed.ToJson());
    }

    [SkippableFact]
    public async Task Deskew_with_a_region_reads_the_region_of_the_unrotated_image()
    {
        using var page = LoadOrSkip("001_plain_text.png");
        using var tilted = DegradedImages.Tilted(page, 4f);
        await using var ocr = new EasyOcrService();

        var top = await ocr.ExtractTextFromImage(tilted, En, new RecognitionOptions
        {
            Preprocessing = new PreprocessingOptions { Deskew = true },
            Region = OcrRegion.Fraction(0, 0, 1, 0.4),
        });

        Assert.NotEmpty(top.Lines);
        // The rotated region's bounds reach a little past the region (4° over the page width).
        double limit = (0.4 * tilted.Height) + (Math.Sin(4 * Math.PI / 180) * tilted.Width) + 10;
        Assert.All(top.Lines, l => Assert.True(l.BoundingBox.CenterY < limit, $"'{l.Text}' at {l.BoundingBox} is outside the region"));
    }

    [SkippableFact]
    public async Task Streamed_deskewed_lines_match_the_buffered_ones()
    {
        using var page = LoadOrSkip("001_plain_text.png");
        using var tilted = DegradedImages.Tilted(page, 4f);
        var options = new RecognitionOptions { Preprocessing = new PreprocessingOptions { Deskew = true } };
        await using var ocr = new EasyOcrService();

        var buffered = await ocr.ExtractTextFromImage(tilted, En, options);
        var streamed = new List<OcrLine>();
        await foreach (var line in ocr.ExtractTextStreamAsync(tilted, En, options)) streamed.Add(line);

        Assert.Equal(buffered.Lines.Count, streamed.Count);
        foreach (var line in streamed)
        {
            var twin = buffered.Lines.FirstOrDefault(b => b.Text == line.Text);
            if (twin is null) continue;
            Assert.True(Math.Abs(line.BoundingBox.CenterX - twin.BoundingBox.CenterX) < 1
                        && Math.Abs(line.BoundingBox.CenterY - twin.BoundingBox.CenterY) < 1,
                $"'{line.Text}': streamed {line.BoundingBox} vs buffered {twin.BoundingBox}");
        }
    }

    private static bool Inside(OcrBoundingBox inner, OcrBoundingBox outer, double slack)
        => inner.CenterX >= outer.MinX - slack && inner.CenterX <= outer.MaxX + slack
        && inner.CenterY >= outer.MinY - slack && inner.CenterY <= outer.MaxY + slack;

    private static double Iou(OcrBoundingBox a, OcrBoundingBox b)
    {
        double ix = Math.Max(0, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX));
        double iy = Math.Max(0, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));
        double inter = ix * iy;
        double union = (a.Width * a.Height) + (b.Width * b.Height) - inter;
        return union <= 0 ? 0 : inter / union;
    }
}
