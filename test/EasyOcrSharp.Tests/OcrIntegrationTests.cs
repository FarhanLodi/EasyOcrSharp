using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
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
}
