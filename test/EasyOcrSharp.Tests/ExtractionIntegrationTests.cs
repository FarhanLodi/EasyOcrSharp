using EasyOcrSharp.Extraction;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// End-to-end checks of the extraction helpers on real model output: zone OCR on <c>sample.png</c>
/// ("Hello World" in the top half, "EasyOcrSharp 2024" in the bottom half), the pure helpers over a real
/// receipt, and model pre-download against the already-populated cache in <b>report-only / offline</b>
/// mode — the real cache is only ever read, never written or deleted.
/// Run with: <c>dotnet test --filter Category=Integration</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(OcrIntegrationCollection.Name)]
public class ExtractionIntegrationTests
{
    private static readonly string[] English = { "en" };

    [Fact]
    public async Task Zones_read_named_regions_of_a_real_image_in_full_image_coordinates()
    {
        var sample = TestAssets.Image("sample.png");
        Assert.True(sample is not null, "sample.png asset not found — ensure it is copied to output.");

        await using var ocr = new EasyOcrService();
        var zones = new[]
        {
            new OcrZone("top", OcrRegion.Fraction(0, 0, 1, 0.5)),
            new OcrZone("bottom", OcrRegion.Fraction(0, 0.5, 1, 0.5)),
            new OcrZone("outside", OcrRegion.Pixels(100_000, 100_000, 50, 50)),
        };

        var result = await ocr.RecognizeZonesAsync(sample!, zones, English);

        Assert.Contains("HELLO", TestAssets.Normalize(result["top"].Text));
        Assert.DoesNotContain("EASYOCRSHARP", TestAssets.Normalize(result["top"].Text));
        Assert.Contains("EASYOCRSHARP", TestAssets.Normalize(result["bottom"].Text));
        Assert.Equal(string.Empty, result["outside"].Text);

        // Boxes come back in the full image's coordinates, inside each zone.
        double half = result.SourceHeight / 2.0;
        Assert.All(result["bottom"].Lines, l => Assert.True(l.BoundingBox.MinY >= half - 2));
        Assert.All(result["top"].Lines, l => Assert.True(l.BoundingBox.MaxY <= half + 2));
        Assert.True(result["bottom"].Confidence > 0.3);
    }

    [Fact]
    public async Task SingleLine_zone_reads_a_line_located_by_a_previous_pass_without_detection()
    {
        var sample = TestAssets.Image("sample.png");
        Assert.True(sample is not null, "sample.png asset not found.");

        await using var ocr = new EasyOcrService();
        using var image = Image.Load<Rgb24>(sample!);
        var full = await ocr.ExtractTextFromImage(image, English);
        var hello = full.Lines.First(l => TestAssets.Normalize(l.Text).Contains("HELLO"));

        // Pad the detected box a little and read it as one line, restricted to letters and space.
        var b = hello.BoundingBox;
        var zone = new OcrZone(
            "greeting",
            OcrRegion.Pixels(b.MinX - 4, b.MinY - 4, b.Width + 8, b.Height + 8),
            ZoneMode.SingleLine,
            Allowlist: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz ");

        var result = await ocr.RecognizeZonesAsync(image, new[] { zone }, English);

        Assert.Contains("HELLO", TestAssets.Normalize(result["greeting"].Text));
        Assert.All(result["greeting"].Text, c => Assert.True(char.IsAsciiLetter(c) || c == ' '));
    }

    [Fact]
    public async Task Pure_helpers_work_on_a_real_receipt()
    {
        var receipt = TestAssets.Image("057_receipts_invoices.png");
        Assert.True(receipt is not null, "057_receipts_invoices.png asset not found.");

        await using var ocr = new EasyOcrService();
        var result = await ocr.ExtractTextFromImage(receipt!, English);
        Assert.NotEmpty(result.Lines);

        var quality = result.GetQuality();
        Assert.Equal(result.Lines.Count, quality.LineCount);
        Assert.InRange(quality.MeanConfidence, 0, 1);
        Assert.InRange(quality.CharacterWeightedConfidence, quality.MinConfidence, 1);

        var layout = result.ToLayoutText();
        Assert.False(string.IsNullOrWhiteSpace(layout));
        Assert.All(result.Lines.Where(l => l.Text.Trim().Length > 0 && !l.Text.Contains('\n')),
            l => Assert.Contains(l.Text.Trim(), layout));

        foreach (var match in result.FindMatches(OcrPatterns.All))
        {
            var box = match.Line.BoundingBox;
            Assert.Equal(match.Value, match.Line.Text.Substring(match.Index, match.Length));
            Assert.InRange(match.BoundingBox.MinX, box.MinX - 0.5, box.MaxX + 0.5);
            Assert.InRange(match.BoundingBox.MaxX, box.MinX - 0.5, box.MaxX + 0.5);
        }
    }

    [SkippableFact]
    public async Task Pre_download_against_the_populated_cache_reports_every_file_cached_and_writes_nothing()
    {
        // Report-only first: skip (rather than download) when this machine's cache is not populated.
        var check = EasyOcrModels.CheckCache(English, EasyOcrModelSet.Ocr);
        Skip.IfNot(check.IsComplete, $"The default model cache '{check.CacheDirectory}' does not hold the English models.");

        var stamps = check.Files.ToDictionary(f => f.Path, f => File.GetLastWriteTimeUtc(f.Path));

        // Offline: a file that unexpectedly went missing fails instead of downloading.
        var report = await EasyOcrModels.DownloadAsync(English, EasyOcrModelSet.Ocr, new ModelDownloadOptions { Offline = true });

        Assert.True(report.IsComplete);
        Assert.Equal(check.Files.Count, report.CachedCount);
        Assert.Equal(0, report.DownloadedCount);
        Assert.Equal(check.Files.Select(f => f.Path), report.Files.Select(f => f.Path));
        Assert.Equal(check.TotalBytes, report.TotalBytes);
        Assert.All(report.Files, f => Assert.Equal(stamps[f.Path], File.GetLastWriteTimeUtc(f.Path)));
        report.EnsureSuccess();
    }

    [SkippableFact]
    public async Task Pre_download_of_cached_document_analysis_models_matches_the_cache_check()
    {
        var check = EasyOcrModels.CheckCache(English, EasyOcrModelSet.StructureCore);
        Skip.IfNot(check.IsComplete, "The structure cache does not hold the default document-analysis core models.");

        var report = await EasyOcrModels.DownloadAsync(English, EasyOcrModelSet.StructureCore, new ModelDownloadOptions { Offline = true });

        Assert.True(report.IsComplete);
        Assert.Equal(check.Files.Select(f => (f.FileName, f.Path, f.SizeBytes)), report.Files.Select(f => (f.FileName, f.Path, f.SizeBytes)));
    }

    [Fact]
    public void Cache_check_of_an_unhosted_language_set_lists_missing_files_without_network()
    {
        // A fresh, never-created directory: everything is missing, and nothing is created or fetched.
        var dir = Path.Combine(Path.GetTempPath(), "easyocr_integration_" + Guid.NewGuid().ToString("N"));
        var report = EasyOcrModels.CheckCache(
            new[] { "en", "th", "ko" }, EasyOcrModelSet.All, new EasyOcrServiceOptions { ModelCachePath = dir });

        Assert.Equal(report.Files.Count, report.MissingCount);
        Assert.False(Directory.Exists(dir));
        Assert.Throws<ModelDownloadException>(report.EnsureSuccess);
    }
}
