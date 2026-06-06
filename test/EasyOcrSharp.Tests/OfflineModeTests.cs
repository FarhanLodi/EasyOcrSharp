using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Real-engine tests for download-resilience and the health check that need <b>no network</b> and no
/// models — they exercise the offline/missing-cache code paths against a fresh empty cache directory.
/// </summary>
public class OfflineModeTests
{
    private static string FreshCacheDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "easyocr_offline_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Offline_mode_throws_clearly_when_model_is_missing()
    {
        var cache = FreshCacheDir();
        try
        {
            await using var ocr = new EasyOcrService(new EasyOcrServiceOptions
            {
                ModelCachePath = cache,
                Download = new ModelDownloadOptions { Offline = true },
            });

            using var image = new Image<Rgb24>(64, 64, new Rgb24(255, 255, 255));
            var ex = await Assert.ThrowsAsync<EasyOcrSharpException>(
                () => ocr.ExtractTextFromImage(image, new[] { "en" }));

            Assert.Contains("offline", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task HealthCheck_reports_degraded_when_models_not_yet_cached()
    {
        var cache = FreshCacheDir();
        try
        {
            var options = new EasyOcrServiceOptions { ModelCachePath = cache };
            var check = new EasyOcrHealthCheck(options, new[] { "en" });

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Degraded, result.Status);
            Assert.True(result.Data.ContainsKey("missing"));
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task HealthCheck_reports_unhealthy_when_offline_and_models_missing()
    {
        var cache = FreshCacheDir();
        try
        {
            var options = new EasyOcrServiceOptions
            {
                ModelCachePath = cache,
                Download = new ModelDownloadOptions { Offline = true },
            };
            var check = new EasyOcrHealthCheck(options, new[] { "en" });

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }
}
