using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// End-to-end accuracy tests that require the ONNX models. They are excluded from CI
/// (category "Integration") because the models are large downloads; run locally with
/// EASYOCRSHARP_CACHE pointing at a folder containing the .onnx + .vocab.json files:
///   dotnet test --filter Category=Integration
/// </summary>
[Trait("Category", "Integration")]
public class OcrIntegrationTests
{
    private static string? FindAsset(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", name);
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public async Task Reads_clean_printed_text_accurately()
    {
        var sample = FindAsset("sample.png");
        Assert.True(sample is not null, "sample.png asset not found — ensure it is copied to output.");

        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(sample!, new[] { "en" });

        Assert.Contains("Hello", result.FullText);
        Assert.Contains("World", result.FullText);
        Assert.Contains("EasyOcrSharp", result.FullText);
        Assert.All(result.Lines, l => Assert.True(l.Confidence > 0.5));
    }

    [Fact]
    public async Task Word_grouping_returns_more_regions_than_line_grouping()
    {
        var sample = FindAsset("sample.png");
        Assert.True(sample is not null, "sample.png asset not found.");

        await using var ocr = new EasyOcrService();
        var byLine = await ocr.ExtractTextFromImage(sample!, new[] { "en" },
            new RecognitionOptions { Grouping = TextGrouping.Line });
        var byWord = await ocr.ExtractTextFromImage(sample!, new[] { "en" },
            new RecognitionOptions { Grouping = TextGrouping.Word });

        Assert.True(byWord.Lines.Count >= byLine.Lines.Count);
    }

    [Fact]
    public async Task Region_of_interest_restricts_ocr_and_reports_original_coordinates()
    {
        var sample = FindAsset("sample.png");
        Assert.True(sample is not null, "sample.png asset not found.");

        await using var ocr = new EasyOcrService();

        // sample.png has "Hello World" in the top half and "EasyOcrSharp 2024" in the bottom half.
        var bottom = await ocr.ExtractTextFromImage(sample!, new[] { "en" },
            new RecognitionOptions { Region = OcrRegion.Fraction(0, 0.5, 1, 0.5) });

        Assert.Contains("EasyOcrSharp", bottom.FullText);
        Assert.DoesNotContain("Hello", bottom.FullText);
        // Coordinates are translated back to the full image, so y is in the lower half.
        Assert.All(bottom.Lines, l => Assert.True(l.BoundingBox.MinY > 50));
    }

    [Fact]
    public async Task Auto_detect_identifies_latin_for_english_sample()
    {
        var sample = FindAsset("sample.png");
        Assert.True(sample is not null, "sample.png asset not found.");

        await using var ocr = new EasyOcrService();
        var langs = await ocr.DetectLanguagesAsync(sample!);

        Assert.Contains("en", langs); // latin pack's representative
    }

    [Fact]
    public async Task Auto_detect_language_option_reads_text_without_explicit_codes()
    {
        var sample = FindAsset("sample.png");
        Assert.True(sample is not null, "sample.png asset not found.");

        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(sample!, Array.Empty<string>(),
            new RecognitionOptions { AutoDetectLanguage = true });

        Assert.Contains("Hello", result.FullText);
    }

    [Fact]
    public async Task Orientation_detection_reads_upside_down_image()
    {
        var sample = FindAsset("sample.png");
        Assert.True(sample is not null, "sample.png asset not found.");

        using var upside = await Image.LoadAsync<Rgb24>(sample!);
        upside.Mutate(c => c.Rotate(180));

        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(upside, new[] { "en" },
            new RecognitionOptions { Preprocessing = new PreprocessingOptions { DetectOrientation = true } });

        Assert.Contains("Hello", result.FullText);
    }
}
