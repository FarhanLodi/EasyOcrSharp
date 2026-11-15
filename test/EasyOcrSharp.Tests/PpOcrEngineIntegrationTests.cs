using System.Diagnostics.Metrics;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// End-to-end tests of the opt-in PP-OCRv5 engine (<see cref="RecognitionOptions.Engine"/> =
/// <see cref="RecognitionEngine.PpOcrV5"/>) through every routed entry point — all five
/// <c>ExtractTextFromImage</c> overloads, <c>DetectRegionsAsync</c>, both <c>RecognizeRegionsAsync</c>
/// overloads and all four <c>ExtractTextStreamAsync</c> overloads — plus the options the engine honors and
/// the session sharing with <c>AnalyzeDocumentAsync</c>. Integration: the PP-OCRv5 models (DB detector,
/// text-line classifier, recognizer packs) download on first use.
/// </summary>
[Trait("Category", "Integration")]
[Collection(OcrIntegrationCollection.Name)]
public sealed class PpOcrEngineIntegrationTests
{
    private static readonly RecognitionOptions Paddle = new() { Engine = RecognitionEngine.PpOcrV5 };
    private static readonly string[] English = ["en"];

    private static string Sample()
    {
        var path = TestAssets.Image("sample.png");
        Assert.True(path is not null, "assets/sample.png not found — ensure it is copied to the test output.");
        return path!;
    }

    private static void AssertReadsSample(string text)
    {
        var normalized = TestAssets.Normalize(text);
        Assert.Contains("HELLO", normalized);
        Assert.Contains("WORLD", normalized);
        Assert.Contains("EASYOCRSHARP", normalized);
    }

    // ---- ExtractTextFromImage: every input overload ----

    [Theory]
    [InlineData("path")]
    [InlineData("stream")]
    [InlineData("bytes")]
    [InlineData("memory")]
    [InlineData("image")]
    public async Task Every_ExtractTextFromImage_overload_reads_the_sample(string input)
    {
        var path = Sample();
        await using var ocr = new EasyOcrService();

        OcrResult result;
        switch (input)
        {
            case "path":
                result = await ocr.ExtractTextFromImage(path, English, Paddle);
                break;
            case "stream":
                await using (var stream = File.OpenRead(path))
                    result = await ocr.ExtractTextFromImage(stream, English, Paddle);
                break;
            case "bytes":
                result = await ocr.ExtractTextFromImage(await File.ReadAllBytesAsync(path), English, Paddle);
                break;
            case "memory":
                result = await ocr.ExtractTextFromImage(new ReadOnlyMemory<byte>(await File.ReadAllBytesAsync(path)), English, Paddle);
                break;
            default:
                using (var image = await Image.LoadAsync<Rgb24>(path))
                    result = await ocr.ExtractTextFromImage(image, English, Paddle);
                break;
        }

        AssertReadsSample(result.FullText);
        Assert.NotEmpty(result.Lines);
        Assert.Equal(English, result.Languages);
        Assert.Equal(600, result.SourceWidth);
        Assert.Equal(200, result.SourceHeight);
        Assert.NotEqual(OcrExecutionProvider.Auto, result.ExecutionProvider);
        Assert.Equal(result.ExecutionProvider != OcrExecutionProvider.Cpu, result.UsedGpu);
        Assert.All(result.Lines, line =>
        {
            Assert.False(string.IsNullOrWhiteSpace(line.Text));
            Assert.InRange(line.Confidence, 0.0, 1.0);
            Assert.InRange(line.BoundingBox.MinX, -1, 601);
            Assert.InRange(line.BoundingBox.MaxY, -1, 201);
            // Word boxes are opt-in (WordLevelDetail) on either engine.
            Assert.Empty(line.Words);
        });
    }

    [Fact]
    public async Task Word_level_detail_returns_word_boxes_inside_their_lines()
    {
        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(Sample(), English, Paddle with { WordLevelDetail = WordLevelDetail.Words });

        AssertReadsSample(result.FullText);
        Assert.Contains(result.Lines, line => line.Words.Count > 0);
        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                Assert.False(string.IsNullOrWhiteSpace(word.Text));
                Assert.Contains(word.Text, line.Text);
                Assert.InRange(word.BoundingBox.MinX, line.BoundingBox.MinX - 3, line.BoundingBox.MaxX + 3);
                Assert.InRange(word.BoundingBox.MinY, line.BoundingBox.MinY - 3, line.BoundingBox.MaxY + 3);
            }
        }
    }

    [Fact]
    public async Task Deskewed_pages_report_boxes_on_the_input_image()
    {
        using var source = await Image.LoadAsync<Rgb24>(Sample());
        using var tilted = source.Clone(ctx => ctx.Rotate(4f, KnownResamplers.Bicubic, Color.White));

        await using var ocr = new EasyOcrService();
        var straight = await ocr.ExtractTextFromImage(tilted, English, Paddle);
        var deskewed = await ocr.ExtractTextFromImage(tilted, English,
            Paddle with { Preprocessing = new PreprocessingOptions { Deskew = true } });

        Assert.NotEqual(0f, deskewed.DeskewAngle);
        Assert.Equal(tilted.Width, deskewed.SourceWidth);
        Assert.Equal(tilted.Height, deskewed.SourceHeight);
        // Mapped back, every line sits on the tilted image's ink, near where the no-deskew pass found it.
        Assert.All(deskewed.Lines, line =>
        {
            Assert.InRange(line.BoundingBox.MinX, -2, tilted.Width + 2);
            Assert.InRange(line.BoundingBox.MaxY, -2, tilted.Height + 2);
        });
        foreach (var line in straight.Lines)
        {
            var match = deskewed.Lines.FirstOrDefault(l => l.Text == line.Text);
            if (match is null) continue;
            Assert.InRange(Math.Abs(match.BoundingBox.CenterX - line.BoundingBox.CenterX), 0, 12);
            Assert.InRange(Math.Abs(match.BoundingBox.CenterY - line.BoundingBox.CenterY), 0, 12);
        }
    }

    [Fact]
    public async Task PP_OCRv5_meets_the_accuracy_gate_on_the_ground_truth_sample()
    {
        var reference = (await File.ReadAllTextAsync(TestAssets.Image("sample.gt.txt")
            ?? throw new InvalidOperationException("assets/sample.gt.txt missing"))).Trim();

        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(Sample(), English, Paddle);

        // The same 15% CER bar AccuracyRegressionTests holds the default engine to.
        double cer = TextMetrics.CharacterErrorRate(reference, result.FullText.Trim());
        Assert.True(cer <= 0.15, $"CER {cer:P1} > 15%. Output: <{result.FullText}>");
    }

    [Fact]
    public async Task The_default_engine_is_untouched_by_the_new_option()
    {
        await using var ocr = new EasyOcrService();
        var implicitDefault = await ocr.ExtractTextFromImage(Sample(), English);
        var explicitDefault = await ocr.ExtractTextFromImage(Sample(), English, new RecognitionOptions { Engine = RecognitionEngine.EasyOcr });

        Assert.Equal(implicitDefault.FullText, explicitDefault.FullText);
        Assert.Equal(implicitDefault.Lines.Select(l => l.BoundingBox), explicitDefault.Lines.Select(l => l.BoundingBox));
    }

    // ---- honored options ----

    [Fact]
    public async Task Region_restricts_detection_and_reports_original_coordinates()
    {
        await using var ocr = new EasyOcrService();
        using var image = await Image.LoadAsync<Rgb24>(Sample());
        var region = OcrRegion.Fraction(0, 0.5, 1, 0.5);
        var (_, ry, _, _) = region.Resolve(image.Width, image.Height);

        var full = await ocr.ExtractTextFromImage(image, English, Paddle);
        var lower = await ocr.ExtractTextFromImage(image, English, Paddle with { Region = region });

        Assert.NotEmpty(lower.Lines);
        Assert.True(lower.Lines.Count <= full.Lines.Count);
        Assert.All(lower.Lines, line => Assert.True(line.BoundingBox.MinY >= ry - 2,
            $"line '{line.Text}' at y={line.BoundingBox.MinY} lies above the region top {ry}"));
    }

    [Fact]
    public async Task Allowlist_restricts_the_emitted_characters()
    {
        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(Sample(), English, Paddle with { Allowlist = "0123456789" });

        Assert.All(result.Lines, line => Assert.True(line.Text.All(c => char.IsDigit(c) || char.IsWhiteSpace(c)),
            $"'{line.Text}' contains a character outside the allowlist"));
        Assert.Contains("2024", result.FullText);
    }

    [Fact]
    public async Task MinConfidence_drops_lines_below_the_threshold()
    {
        await using var ocr = new EasyOcrService();
        var all = await ocr.ExtractTextFromImage(Sample(), English, Paddle);
        var none = await ocr.ExtractTextFromImage(Sample(), English, Paddle with { MinConfidence = 1.01 });

        Assert.NotEmpty(all.Lines);
        Assert.Empty(none.Lines);
        Assert.Equal(string.Empty, none.FullText);
    }

    [Fact]
    public async Task Paragraph_grouping_merges_lines()
    {
        await using var ocr = new EasyOcrService();
        var lines = await ocr.ExtractTextFromImage(Sample(), English, Paddle);
        var paragraphs = await ocr.ExtractTextFromImage(Sample(), English, Paddle with { Grouping = TextGrouping.Paragraph });

        Assert.True(paragraphs.Lines.Count <= lines.Lines.Count);
        AssertReadsSample(paragraphs.FullText);
    }

    [Fact]
    public async Task Preprocessing_steps_run_before_PP_OCRv5()
    {
        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(Sample(), English, Paddle with
        {
            Preprocessing = new PreprocessingOptions { Denoise = true, Sharpen = true },
        });

        AssertReadsSample(result.FullText);
    }

    [Fact]
    public async Task DetectOrientation_recovers_a_page_turned_sideways()
    {
        await using var ocr = new EasyOcrService();
        using var source = await Image.LoadAsync<Rgb24>(Sample());
        using var rotated = source.Clone(ctx => ctx.Rotate(RotateMode.Rotate90));

        var result = await ocr.ExtractTextFromImage(rotated, English, Paddle with
        {
            Preprocessing = new PreprocessingOptions { DetectOrientation = true },
        });

        AssertReadsSample(result.FullText);
        // Boxes are mapped back into the frame of the image that was passed in.
        Assert.Equal(rotated.Width, result.SourceWidth);
        Assert.All(result.Lines, line => Assert.InRange(line.BoundingBox.MaxX, -1, rotated.Width + 1));
    }

    [Fact]
    public async Task An_upside_down_page_is_read_by_the_text_line_orientation_classifier()
    {
        // RotationInfo is an EasyOCR option; PP-OCRv5's text-line classifier covers 180° on its own.
        await using var ocr = new EasyOcrService();
        using var source = await Image.LoadAsync<Rgb24>(Sample());
        using var flipped = source.Clone(ctx => ctx.Rotate(RotateMode.Rotate180));

        var result = await ocr.ExtractTextFromImage(flipped, English, Paddle);

        Assert.Contains("HELLO", TestAssets.Normalize(result.FullText));
    }

    [Fact]
    public async Task Auto_detection_reports_the_detected_language()
    {
        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(Sample(), Array.Empty<string>(), Paddle with { AutoDetectLanguage = true });

        AssertReadsSample(result.FullText);
        Assert.Contains("en", result.Languages);
    }

    [Fact]
    public async Task Right_to_left_reading_direction_is_accepted()
    {
        await using var ocr = new EasyOcrService();
        var ltr = await ocr.ExtractTextFromImage(Sample(), English, Paddle);
        var rtl = await ocr.ExtractTextFromImage(Sample(), English, Paddle with { ReadingDirection = TextReadingDirection.RightToLeft });

        // Same lines, possibly in a different order.
        Assert.Equal(ltr.Lines.Select(l => l.Text).Order(), rtl.Lines.Select(l => l.Text).Order());
    }

    // ---- DetectRegionsAsync / RecognizeRegionsAsync ----

    [Fact]
    public async Task DetectRegionsAsync_returns_DB_text_line_regions_for_image_and_path()
    {
        await using var ocr = new EasyOcrService();
        using var image = await Image.LoadAsync<Rgb24>(Sample());

        var fromImage = await ocr.DetectRegionsAsync(image, Paddle);
        var fromPath = await ocr.DetectRegionsAsync(Sample(), Paddle);

        Assert.NotEmpty(fromImage);
        Assert.Equal(fromImage.Count, fromPath.Count);
        Assert.All(fromImage, region =>
        {
            Assert.True(region.BoundingPolygon.Count >= 4);
            Assert.InRange(region.BoundingBox.MinX, -1, image.Width + 1);
            Assert.InRange(region.BoundingBox.MaxY, -1, image.Height + 1);
        });
    }

    [Fact]
    public async Task DetectRegionsAsync_honors_the_region_of_interest()
    {
        await using var ocr = new EasyOcrService();
        using var image = await Image.LoadAsync<Rgb24>(Sample());
        var region = OcrRegion.Fraction(0, 0.5, 1, 0.5);
        var (_, ry, _, _) = region.Resolve(image.Width, image.Height);

        var regions = await ocr.DetectRegionsAsync(image, Paddle with { Region = region });

        Assert.NotEmpty(regions);
        Assert.All(regions, r => Assert.True(r.BoundingBox.MinY >= ry - 2));
    }

    [Fact]
    public async Task RecognizeRegionsAsync_reads_PP_OCRv5_regions_with_both_overloads()
    {
        await using var ocr = new EasyOcrService();
        using var image = await Image.LoadAsync<Rgb24>(Sample());
        var regions = await ocr.DetectRegionsAsync(image, Paddle);

        var viaRegions = await ocr.RecognizeRegionsAsync(image, regions, English, Paddle);
        var viaPolygons = await ocr.RecognizeRegionsAsync(image, regions.Select(r => r.BoundingPolygon), English, Paddle);

        AssertReadsSample(viaRegions.FullText);
        Assert.Equal(viaRegions.FullText, viaPolygons.FullText);
        // One reading per supplied region at most — never merged across regions.
        Assert.True(viaRegions.Lines.Count <= regions.Count);
        Assert.NotEqual(OcrExecutionProvider.Auto, viaRegions.ExecutionProvider);
    }

    [Fact]
    public async Task RecognizeRegionsAsync_reads_CRAFT_regions_too()
    {
        // The engines interoperate: regions from the default engine's detector, read by PP-OCRv5.
        await using var ocr = new EasyOcrService();
        using var image = await Image.LoadAsync<Rgb24>(Sample());
        var craft = await ocr.DetectRegionsAsync(image);

        var result = await ocr.RecognizeRegionsAsync(image, craft, English, Paddle);

        Assert.Contains("HELLO", TestAssets.Normalize(result.FullText));
    }

    // ---- ExtractTextStreamAsync: every input overload ----

    [Theory]
    [InlineData("image")]
    [InlineData("path")]
    [InlineData("stream")]
    [InlineData("bytes")]
    public async Task Every_ExtractTextStreamAsync_overload_streams_the_sample(string input)
    {
        var path = Sample();
        await using var ocr = new EasyOcrService();
        using var image = await Image.LoadAsync<Rgb24>(path);
        await using var file = File.OpenRead(path);

        var stream = input switch
        {
            "image" => ocr.ExtractTextStreamAsync(image, English, Paddle),
            "path" => ocr.ExtractTextStreamAsync(path, English, Paddle),
            "stream" => ocr.ExtractTextStreamAsync(file, English, Paddle),
            _ => ocr.ExtractTextStreamAsync(await File.ReadAllBytesAsync(path), English, Paddle),
        };

        var lines = new List<OcrLine>();
        await foreach (var line in stream) lines.Add(line);

        Assert.NotEmpty(lines);
        AssertReadsSample(string.Join("\n", lines.Select(l => l.Text)));
    }

    [Fact]
    public async Task Streamed_paragraphs_match_the_buffered_text()
    {
        await using var ocr = new EasyOcrService();
        using var image = await Image.LoadAsync<Rgb24>(Sample());
        var options = Paddle with { Grouping = TextGrouping.Paragraph };

        var streamed = new List<OcrLine>();
        await foreach (var line in ocr.ExtractTextStreamAsync(image, English, options)) streamed.Add(line);

        Assert.NotEmpty(streamed);
        AssertReadsSample(string.Join("\n", streamed.Select(l => l.Text)));
    }

    // ---- multi-script ----

    public static TheoryData<string, string, string> ScriptPanels => new()
    {
        { "ru_exit", "ru,en", "ВЫХОД" },
        { "zh_street", "ch_sim,en", "中山路" },
        { "es_bakery", "es", "ESPIGA" },
        { "de_caution", "de", "VORSICHT" },
    };

    [SkippableTheory]
    [MemberData(nameof(ScriptPanels))]
    public async Task Script_packs_read_real_signs(string panelName, string languages, string expected)
    {
        var collage = EngineBenchmarkData.RepoAsset("ocr_test4.png");
        Skip.If(collage is null, "test/assets/ocr_test4.png not found.");

        var panel = EngineBenchmarkData.SignPanels.Single(p => p.Name == panelName);
        using var image = await Image.LoadAsync<Rgb24>(collage!);
        using var crop = EngineBenchmarkData.CropPanel(image, panel);

        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(crop, languages.Split(','), Paddle);

        Assert.Contains(TestAssets.Normalize(expected), TestAssets.Normalize(result.FullText));
    }

    [SkippableFact]
    public async Task A_request_spanning_two_scripts_reads_both()
    {
        var collage = EngineBenchmarkData.RepoAsset("ocr_test4.png");
        Skip.If(collage is null, "test/assets/ocr_test4.png not found.");

        // The Russian EXIT sign carries both Cyrillic and Latin: ru + ch_sim needs two packs, and the
        // per-line best-of must still recover the Cyrillic line.
        var panel = EngineBenchmarkData.SignPanels.Single(p => p.Name == "ru_exit");
        using var image = await Image.LoadAsync<Rgb24>(collage!);
        using var crop = EngineBenchmarkData.CropPanel(image, panel);

        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(crop, ["ch_sim", "ru"], Paddle);

        var normalized = TestAssets.Normalize(result.FullText);
        Assert.Contains("ВЫХОД", normalized);
        Assert.Equal(new[] { "ch_sim", "ru" }, result.Languages);
    }

    // ---- shared sessions, disposal ----

    [Fact]
    public async Task Document_analysis_and_PP_OCRv5_share_one_detector_session()
    {
        int detectorLoads = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == EasyOcrSharp.Structure.Engine.StructureDiagnostics.MeterName && instrument.Name == "paddleocr.model.loads")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "model" && Equals(tag.Value, "det")) Interlocked.Add(ref detectorLoads, (int)value);
            }
        });
        listener.Start();

        await using var ocr = new EasyOcrService();
        var analysis = await ocr.AnalyzeDocumentAsync(Sample());
        var text = await ocr.ExtractTextFromImage(Sample(), English, Paddle);
        var regions = await ocr.DetectRegionsAsync(Sample(), Paddle);

        Assert.NotNull(analysis);
        AssertReadsSample(text.FullText);
        Assert.NotEmpty(regions);
        Assert.Equal(1, Volatile.Read(ref detectorLoads));
    }

    [Fact]
    public async Task Disposing_after_PP_OCRv5_use_releases_the_shared_sessions_and_rejects_new_calls()
    {
        var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(Sample(), English, Paddle);
        AssertReadsSample(result.FullText);

        await ocr.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => ocr.ExtractTextFromImage(Sample(), English, Paddle));
    }

    [Fact]
    public async Task Concurrent_calls_on_both_engines_are_safe()
    {
        await using var ocr = new EasyOcrService();
        var calls = Enumerable.Range(0, 6).Select(i => ocr.ExtractTextFromImage(
            Sample(), English, i % 2 == 0 ? Paddle : RecognitionOptions.Default)).ToArray();

        var results = await Task.WhenAll(calls);

        Assert.All(results, r => Assert.Contains("HELLO", TestAssets.Normalize(r.FullText)));
    }
}
