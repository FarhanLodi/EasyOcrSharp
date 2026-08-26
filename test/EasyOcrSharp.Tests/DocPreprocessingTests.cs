using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Model-free tests for the new preprocessing steps (sharpen + model-based document orientation /
/// unwarp) and the document-analysis options mapping. Nothing here downloads a model.
/// </summary>
public class DocPreprocessingTests
{
    // ---- new preprocessing flags: defaults must not change existing behavior ----

    [Fact]
    public void PreprocessingOptions_new_steps_default_off_and_flip_IsAnyEnabled()
    {
        Assert.False(PreprocessingOptions.None.Sharpen);
        Assert.False(PreprocessingOptions.None.DocumentOrientation);
        Assert.False(PreprocessingOptions.None.DocumentUnwarp);
        Assert.Equal(1.0f, PreprocessingOptions.None.SharpenAmount);
        Assert.False(PreprocessingOptions.None.IsAnyEnabled);

        Assert.True(new PreprocessingOptions { Sharpen = true }.IsAnyEnabled);
        Assert.True(new PreprocessingOptions { DocumentOrientation = true }.IsAnyEnabled);
        Assert.True(new PreprocessingOptions { DocumentUnwarp = true }.IsAnyEnabled);
    }

    [Fact]
    public void Apply_sharpen_returns_fresh_image_and_changes_edge_pixels()
    {
        using var img = new Image<Rgb24>(60, 40, new Rgb24(255, 255, 255));
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 30; x < 60; x++) row[x] = new Rgb24(80, 80, 80);
            }
        });

        using var result = ImagePreprocessor.Apply(img, new PreprocessingOptions { Sharpen = true });
        Assert.NotSame(img, result);
        Assert.Equal(img.Width, result.Width);

        // An unsharp mask overshoots on both sides of the vertical edge at x=30.
        bool changed = false;
        result.ProcessPixelRows(acc =>
        {
            var row = acc.GetRowSpan(20);
            for (int x = 27; x < 34 && !changed; x++)
            {
                var expected = x < 30 ? (byte)255 : (byte)80;
                if (row[x].R != expected) changed = true;
            }
        });
        Assert.True(changed, "sharpening should alter pixels around a hard edge");
    }

    [Fact]
    public void DocPreprocessor_with_no_sessions_returns_untouched_clone()
    {
        using var img = new Image<Rgb24>(50, 70, new Rgb24(10, 20, 30));
        using var pre = new DocPreprocessor(orientation: null, unwarp: null);
        var (processed, rotation) = pre.Apply(img, useOrientation: true, useUnwarp: true);
        using (processed)
        {
            Assert.NotSame(img, processed); // caller-owned fresh image even when nothing ran
            Assert.Equal(0, rotation);
            Assert.Equal(img.Width, processed.Width);
            Assert.Equal(img.Height, processed.Height);
            Assert.Equal(new Rgb24(10, 20, 30), processed[5, 5]);
        }
    }

    [Fact]
    public void DocPreprocess_model_assets_have_checksums_and_hosted_urls()
    {
        // Downloads are verified fail-closed, so a missing checksum would brick the feature.
        Assert.False(string.IsNullOrEmpty(DocPreprocessModelRegistry.DocOrientationClassifier.Sha256));
        Assert.False(string.IsNullOrEmpty(DocPreprocessModelRegistry.DocUnwarp.Sha256));
        Assert.StartsWith("https://", DocPreprocessModelRegistry.DocOrientationClassifier.Url);
        Assert.EndsWith(DocPreprocessModelRegistry.DocUnwarp.FileName, DocPreprocessModelRegistry.DocUnwarp.Url);
    }

    // ---- document-analysis options ----

    [Fact]
    public void DocumentAnalysisOptions_defaults_enable_all_recognizers_and_no_page_correction()
    {
        var d = DocumentAnalysisOptions.Default;
        Assert.False(d.DocumentOrientation);
        Assert.False(d.DocumentUnwarp);
        Assert.True(d.RecognizeTables);
        Assert.True(d.RecognizeFormulas);
        Assert.True(d.RecognizeSeals);
        Assert.Equal(DocumentTableModel.SlanetPlus, d.TableModel);
        Assert.Null(d.Languages);
    }

    [Fact]
    public void ToStructureOptions_maps_flags_table_model_and_languages()
    {
        var mapped = EasyOcrService.ToStructureOptions(new DocumentAnalysisOptions
        {
            DocumentOrientation = true,
            DocumentUnwarp = true,
            RecognizeFormulas = false,
            TableModel = DocumentTableModel.SlaNeXt,
            Languages = new[] { "en", "not-a-language", "ru" },
        }, logger: null);

        Assert.True(mapped.UseDocOrientation);
        Assert.True(mapped.UseUnwarp);
        Assert.True(mapped.RecognizeTables);
        Assert.False(mapped.RecognizeFormulas);
        Assert.Equal(EasyOcrSharp.Structure.TableRecognitionModel.SlaNeXt, mapped.TableModel);
        // "not-a-language" is skipped; the two valid codes survive in order.
        Assert.Equal(2, mapped.Languages.Count);
    }

    [Fact]
    public void ToStructureOptions_null_uses_defaults_and_default_language_pack()
    {
        var mapped = EasyOcrService.ToStructureOptions(null, logger: null);
        Assert.False(mapped.UseDocOrientation);
        Assert.True(mapped.RecognizeTables);
        // No languages requested → the structure engine's own default pack applies (ch, covers en/ja).
        Assert.Equal(EasyOcrSharp.Structure.StructureOptions.Default.Languages, mapped.Languages);
    }

    [Fact]
    public void Service_constructs_and_disposes_without_touching_analysis_models()
    {
        // The document analyzer must stay lazy: constructing and disposing the service downloads nothing.
        using var service = new EasyOcrService(new EasyOcrServiceOptions
        {
            ExecutionProvider = OcrExecutionProvider.Cpu,
            ModelCachePath = Path.Combine(Path.GetTempPath(), "easyocrsharp-tests-no-download"),
        });
        Assert.False(service.UseGpu);
    }
}
