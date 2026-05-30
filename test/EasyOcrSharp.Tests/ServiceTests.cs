using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EasyOcrSharp.Tests;

public class ServiceTests
{
    [Fact]
    public void AddEasyOcrSharp_registers_singleton_service()
    {
        var services = new ServiceCollection();
        services.AddEasyOcrSharp(o => o.ModelCachePath = "/tmp/easyocr-test");

        using var provider = services.BuildServiceProvider();
        var a = provider.GetRequiredService<IEasyOcrService>();
        var b = provider.GetRequiredService<IEasyOcrService>();

        Assert.NotNull(a);
        Assert.Same(a, b); // singleton
    }

    [Fact]
    public void RecognitionOptions_default_values()
    {
        var o = RecognitionOptions.Default;
        Assert.Equal(TextGrouping.Line, o.Grouping);
        Assert.True(o.AdjustContrast);
        Assert.Equal(0.0, o.MinConfidence);
        Assert.True(o.MaxDegreeOfParallelism >= 1);
    }

    [Fact]
    public async Task ExtractTextFromImage_throws_on_missing_file()
    {
        await using var ocr = new EasyOcrService();
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => ocr.ExtractTextFromImage("does-not-exist-12345.png", new[] { "en" }));
    }

    [Fact]
    public async Task ExtractTextFromImage_throws_on_empty_languages_without_autodetect()
    {
        await using var ocr = new EasyOcrService();
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
        // Empty languages and no auto-detect must be rejected (before any model download).
        await Assert.ThrowsAsync<ArgumentException>(
            () => ocr.ExtractTextFromImage(image, Array.Empty<string>()));
    }
}
