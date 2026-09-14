using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// <see cref="EasyOcrServiceOptions.DetectorModelPath"/>: the CRAFT detector loaded straight from disk, with no
/// download and no checksum — the detector-side counterpart of <see cref="CustomRecognizer"/>.
/// </summary>
public class LocalDetectorModelTests
{
    [Fact]
    public void Path_is_normalized_and_blank_means_the_registry_model()
    {
        var relative = new EasyOcrServiceOptions { DetectorModelPath = "models/craft.onnx" }.ToEngineOptions();
        Assert.Equal(Path.GetFullPath("models/craft.onnx"), relative.DetectorModelPath);

        Assert.Null(new EasyOcrServiceOptions { DetectorModelPath = "  " }.ToEngineOptions().DetectorModelPath);
        Assert.Null(new EasyOcrServiceOptions().ToEngineOptions().DetectorModelPath);
    }

    [Fact]
    public async Task Missing_file_fails_with_a_clear_error_instead_of_downloading()
    {
        var missing = Path.Combine(Path.GetTempPath(), "easyocrsharp-no-such-detector-" + Guid.NewGuid().ToString("N") + ".onnx");
        var cache = Directory.CreateTempSubdirectory("easyocrsharp-empty-cache-");
        try
        {
            await using var ocr = new EasyOcrService(new EasyOcrServiceOptions
            {
                DetectorModelPath = missing,
                ModelCachePath = cache.FullName,
                Download = { Offline = true },
            });
            using var image = new Image<Rgb24>(32, 32, new Rgb24(255, 255, 255));

            var ex = await Assert.ThrowsAsync<EasyOcrSharpException>(() => ocr.DetectRegionsAsync(image));
            Assert.Contains(missing, ex.Message);
            Assert.Contains("DetectorModelPath", ex.Message);
        }
        finally
        {
            cache.Delete(recursive: true);
        }
    }
}

/// <summary>Runs a real local detector file with an empty cache and offline mode on, proving no registry lookup happens.</summary>
[Trait("Category", "Integration")]
[Collection(OcrIntegrationCollection.Name)]
public class LocalDetectorModelIntegrationTests
{
    [SkippableFact]
    public async Task Local_detector_file_is_used_with_an_empty_offline_cache()
    {
        var cached = Path.Combine(
            Environment.GetEnvironmentVariable("EASYOCRSHARP_CACHE") is { Length: > 0 } env
                ? env
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyOcrSharp", "models"),
            "craft_mlt_25k.onnx");
        Skip.IfNot(File.Exists(cached), "craft_mlt_25k.onnx is not in the model cache.");
        var sample = TestAssets.Image("sample.png");
        Skip.If(sample is null, "assets/sample.png not found.");

        var cache = Directory.CreateTempSubdirectory("easyocrsharp-empty-cache-");
        try
        {
            await using var ocr = new EasyOcrService(new EasyOcrServiceOptions
            {
                DetectorModelPath = cached,
                ModelCachePath = cache.FullName,
                Download = { Offline = true },
            });
            using var image = await Image.LoadAsync<Rgb24>(sample!);

            var regions = await ocr.DetectRegionsAsync(image);

            Assert.NotEmpty(regions);
            Assert.Empty(Directory.EnumerateFileSystemEntries(cache.FullName));
        }
        finally
        {
            cache.Delete(recursive: true);
        }
    }
}
