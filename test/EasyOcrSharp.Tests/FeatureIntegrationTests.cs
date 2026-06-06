using System.Diagnostics;
using System.Diagnostics.Metrics;
using EasyOcrSharp.Diagnostics;
using EasyOcrSharp.Export;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// End-to-end tests for the 2.2.0 features. They run the real OCR engine (no mocks) and download the
/// ONNX models on first run. Tagged "Integration"; a plain <c>dotnet test</c> runs them, while
/// <c>--filter Category!=Integration</c> skips them. Fixture: <c>assets/sample.png</c>
/// ("Hello World" over "EasyOcrSharp 2024").
/// </summary>
[Trait("Category", "Integration")]
public class FeatureIntegrationTests
{
    private static string Sample()
    {
        var path = TestAssets.Image("sample.png");
        Assert.True(path is not null, "assets/sample.png not found — ensure it is copied to the test output.");
        return path!;
    }

    // ---- Feature 3: allowlist / blocklist ----

    [Fact]
    public async Task Allowlist_digits_keeps_numbers_and_drops_letters()
    {
        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(Sample(), new[] { "en" },
            new RecognitionOptions { Allowlist = "0123456789" });

        var norm = TestAssets.Normalize(result.FullText);
        Assert.Contains("2024", norm);
        Assert.DoesNotContain("HELLO", norm);
        Assert.DoesNotContain("WORLD", norm);
    }

    [Fact]
    public async Task Blocklist_digits_keeps_letters_and_drops_numbers()
    {
        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(Sample(), new[] { "en" },
            new RecognitionOptions { Blocklist = "0123456789" });

        var norm = TestAssets.Normalize(result.FullText);
        Assert.Contains("HELLO", norm);
        Assert.DoesNotContain("2024", norm);
    }

    // ---- Feature 3: detection thresholds still produce text ----

    [Fact]
    public async Task Custom_detection_thresholds_still_read_text()
    {
        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(Sample(), new[] { "en" }, new RecognitionOptions
        {
            Detection = new DetectionOptions { TextThreshold = 0.6, LowText = 0.3, MagRatio = 1.5 },
        });

        Assert.Contains("HELLO", TestAssets.Normalize(result.FullText));
    }

    // ---- Feature 8: detection-only + visualization ----

    [Fact]
    public async Task DetectRegions_returns_in_bounds_regions_without_recognition()
    {
        await using var ocr = new EasyOcrService();
        using var image = await Image.LoadAsync<Rgb24>(Sample());

        var regions = await ocr.DetectRegionsAsync(Sample());

        Assert.NotEmpty(regions);
        foreach (var r in regions)
        {
            Assert.True(r.BoundingBox.MinX >= 0 && r.BoundingBox.MaxX <= image.Width + 1);
            Assert.True(r.BoundingBox.MinY >= 0 && r.BoundingBox.MaxY <= image.Height + 1);
        }
    }

    [Fact]
    public async Task DrawAnnotations_marks_recognized_boxes_on_real_image()
    {
        await using var ocr = new EasyOcrService();
        using var image = await Image.LoadAsync<Rgb24>(Sample());
        var result = await ocr.ExtractTextFromImage(image, new[] { "en" });

        using var annotated = image.DrawAnnotations(result, new Rgb24(255, 0, 0), thickness: 2);

        Assert.NotSame(image, annotated);
        bool red = false;
        for (int x = 0; x < annotated.Width && !red; x++)
            for (int y = 0; y < annotated.Height && !red; y++)
                if (annotated[x, y] is { R: 255, G: 0, B: 0 }) red = true;
        Assert.True(red, "Expected annotation outlines on the image.");
    }

    // ---- Feature 9: exporters over real OCR output ----

    [Fact]
    public async Task Exporters_produce_valid_documents_from_real_ocr()
    {
        await using var ocr = new EasyOcrService();
        using var image = await Image.LoadAsync<Rgb24>(Sample());
        var result = await ocr.ExtractTextFromImage(image, new[] { "en" });

        var hocr = result.ToHocr(image.Width, image.Height);
        var alto = result.ToAlto(image.Width, image.Height);
        var tsv = result.ToTsv();
        var json = result.ToJson(indented: true);

        Assert.Contains("class='ocr_line'", hocr);
        Assert.NotNull(System.Xml.Linq.XDocument.Parse(alto).Root); // well-formed XML
        Assert.StartsWith("level\tpage_num", tsv);

        var back = System.Text.Json.JsonSerializer.Deserialize(json, EasyOcrJsonContext.Default.OcrResult);
        Assert.Equal(result.FullText, back!.FullText);
        Assert.Contains("HELLO", TestAssets.Normalize(result.FullText));
    }

    // ---- Feature 7: batch over real files ----

    [Fact]
    public async Task Batch_processes_multiple_real_images_concurrently()
    {
        var dir = Path.Combine(Path.GetTempPath(), "easyocr_batch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var paths = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                var dst = Path.Combine(dir, $"page_{i}.png");
                File.Copy(Sample(), dst);
                paths.Add(dst);
            }

            await using var ocr = new EasyOcrService();
            var results = new List<OcrBatchResult>();
            await foreach (var r in ocr.ExtractTextFromImagesAsync(paths, new[] { "en" }, maxConcurrency: 2))
                results.Add(r);

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.True(r.Succeeded, r.Error?.Message));
            Assert.All(results, r => Assert.Contains("HELLO", TestAssets.Normalize(r.Result!.FullText)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- Feature 4: metrics + tracing ----

    [Fact]
    public async Task Metrics_and_tracing_are_emitted_during_ocr()
    {
        long operations = 0;
        long lines = 0;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (inst, listener) =>
        {
            if (inst.Meter.Name == EasyOcrDiagnostics.MeterName) listener.EnableMeasurementEvents(inst);
        };
        meterListener.SetMeasurementEventCallback<long>((inst, value, tags, state) =>
        {
            if (inst.Name == "easyocr.operations") Interlocked.Add(ref operations, value);
            if (inst.Name == "easyocr.lines") Interlocked.Add(ref lines, value);
        });
        meterListener.Start();

        var stoppedActivities = new List<string>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == EasyOcrDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => stoppedActivities.Add(a.OperationName),
        };
        ActivitySource.AddActivityListener(activityListener);

        await using (var ocr = new EasyOcrService())
        {
            await ocr.ExtractTextFromImage(Sample(), new[] { "en" });
        }

        meterListener.Dispose(); // flush pending measurements
        Assert.True(operations >= 1, "Expected the operations counter to record at least one OCR.");
        Assert.True(lines >= 1, "Expected the lines counter to record recognized lines.");
        Assert.Contains("EasyOcr.Extract", stoppedActivities);
    }

    // ---- Feature 4: health check ----

    [Fact]
    public async Task HealthCheck_is_healthy_after_models_are_cached()
    {
        // Trigger a download into the default cache, then probe it.
        await using (var ocr = new EasyOcrService())
        {
            await ocr.ExtractTextFromImage(Sample(), new[] { "en" });
        }

        var check = new EasyOcrHealthCheck(new EasyOcrServiceOptions(), new[] { "en" });
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    // ---- Feature 6: execution provider falls back to CPU ----

    [Fact]
    public async Task Requesting_unavailable_gpu_provider_falls_back_and_still_reads()
    {
        await using var ocr = new EasyOcrService(new EasyOcrServiceOptions
        {
            ExecutionProvider = OcrExecutionProvider.Cuda, // not present in CI → graceful CPU fallback
        });

        var result = await ocr.ExtractTextFromImage(Sample(), new[] { "en" });

        Assert.True(result.UsedGpu); // accelerator was requested
        Assert.Contains("HELLO", TestAssets.Normalize(result.FullText));
    }
}
