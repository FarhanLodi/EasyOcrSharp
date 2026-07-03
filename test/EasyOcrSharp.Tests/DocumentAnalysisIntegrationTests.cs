using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using PaddleOcrNet.Structure;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// End-to-end tests for document-structure analysis (layout/tables via the PaddleOcrNet engine) and
/// the model-based document preprocessing. They download models on first run, so they carry the same
/// "Integration" trait (and CI exclusion) as <see cref="OcrIntegrationTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public class DocumentAnalysisIntegrationTests
{
    private static string? FindAsset(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", name);
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public async Task AnalyzeDocumentAsync_returns_layout_blocks_for_a_document_page()
    {
        var sample = FindAsset("structure_sample.png");
        Assert.True(sample is not null, "structure_sample.png asset not found — ensure it is copied to output.");

        await using var ocr = new EasyOcrService();
        var result = await ocr.AnalyzeDocumentAsync(sample!, new DocumentAnalysisOptions
        {
            // Keep the first run lean: layout + text + tables only.
            RecognizeFormulas = false,
            RecognizeSeals = false,
        });

        Assert.NotEmpty(result.Blocks);
        Assert.True(result.SourceWidth > 0 && result.SourceHeight > 0);

        // The exporters ride along on the result object.
        var markdown = result.ToMarkdown();
        Assert.False(string.IsNullOrWhiteSpace(markdown));

        // If the page contains a table, its structure must have been recovered as HTML.
        foreach (var block in result.Blocks.Where(b => b.Type == StructureBlockType.Table))
        {
            Assert.False(string.IsNullOrWhiteSpace(block.TableHtml));
        }
    }

    [Fact]
    public async Task Document_orientation_model_corrects_a_rotated_page()
    {
        var sample = FindAsset("sample.png");
        Assert.True(sample is not null, "sample.png asset not found.");

        using var original = Image.Load<Rgb24>(sample!);
        using var rotated = original.Clone(c => c.Rotate(RotateMode.Rotate180));

        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(rotated, new[] { "en" },
            new RecognitionOptions
            {
                Preprocessing = new PreprocessingOptions { DocumentOrientation = true },
            });

        Assert.Contains("Hello", result.FullText);
        Assert.Contains("World", result.FullText);
    }

    [Fact]
    public async Task Sharpen_preprocessing_still_reads_text()
    {
        var sample = FindAsset("sample.png");
        Assert.True(sample is not null, "sample.png asset not found.");

        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(sample!, new[] { "en" },
            new RecognitionOptions
            {
                Preprocessing = new PreprocessingOptions { Sharpen = true },
            });

        Assert.Contains("Hello", result.FullText);
    }
}
