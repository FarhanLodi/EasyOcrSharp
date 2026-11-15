using EasyOcrSharp.Extraction;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;
using static EasyOcrSharp.Tests.ExtractionTestPages;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Model-free tests for <see cref="ZoneOcrExtensions"/>: a recording <see cref="IEasyOcrService"/> stands in
/// for the engine, so these verify exactly which existing service call each zone becomes (Region-restricted
/// detection or RecognizeRegionsAsync over the zone rectangle), how options and allowlists are layered,
/// and how results are assembled.
/// </summary>
public class ZoneOcrTests
{
    private static readonly string[] English = { "en" };

    [Fact]
    public async Task Detect_zone_runs_region_restricted_ocr_with_its_own_allowlist()
    {
        var service = new RecordingOcrService
        {
            OnDetect = _ => new[] { Line("World", 60, 10, 50, 20, 0.8), Line("Hello", 0, 10, 50, 20, 0.9) },
        };
        using var image = new Image<Rgb24>(200, 100);
        var zone = new OcrZone("greeting", OcrRegion.Pixels(0, 0, 120, 40), ZoneMode.Detect, Allowlist: "HeloWrd");
        var baseOptions = new RecognitionOptions
        {
            Allowlist = "XYZ",
            MinConfidence = 0.3,
            Region = OcrRegion.Pixels(0, 0, 5, 5), // overridden by the zone
        };

        var result = await service.RecognizeZonesAsync(image, new[] { zone }, English, baseOptions);

        var options = Assert.Single(service.DetectOptions);
        Assert.Equal(zone.Region, options!.Region);
        Assert.Equal("HeloWrd", options.Allowlist);
        Assert.Equal(0.3, options.MinConfidence);
        Assert.Empty(service.RegionCalls);

        var greeting = result["greeting"];
        Assert.Equal("Hello World", greeting.Text);              // reading order, not detection order
        Assert.Equal((0.8 * 5 + 0.9 * 5) / 10, greeting.Confidence, 10);
        Assert.Equal(2, greeting.Lines.Count);
        Assert.Equal(new OcrBoundingBox(0, 0, 120, 40), greeting.PixelBounds);
        Assert.Same(zone, greeting.Zone);
    }

    [Fact]
    public async Task SingleLine_zone_recognizes_the_resolved_rectangle_without_detection()
    {
        var service = new RecordingOcrService { OnRegion = _ => new[] { Line("ABC", 100, 50, 100, 50) } };
        using var image = new Image<Rgb24>(200, 100);
        var zone = new OcrZone("code", OcrRegion.Fraction(0.5, 0.5, 0.5, 0.5), ZoneMode.SingleLine);

        var result = await service.RecognizeZonesAsync(
            image, new[] { zone }, English, new RecognitionOptions { Allowlist = "ABC", Region = OcrRegion.Pixels(1, 1, 5, 5) });

        Assert.Empty(service.DetectOptions);
        var (polygon, options) = Assert.Single(service.RegionCalls);
        Assert.Equal(
            new[] { new OcrPoint(100, 50), new OcrPoint(200, 50), new OcrPoint(200, 100), new OcrPoint(100, 100) },
            polygon);
        Assert.Null(options!.Region);          // the rectangle is the region; no second crop
        Assert.Equal("ABC", options.Allowlist); // zone has no allowlist: the call's applies

        Assert.Equal("ABC", result["code"].Text);
        Assert.Equal(new OcrBoundingBox(100, 50, 200, 100), result["code"].PixelBounds);
    }

    [Fact]
    public async Task SingleLine_zone_joins_multiple_returned_lines_with_spaces()
    {
        var service = new RecordingOcrService
        {
            OnRegion = _ => new[] { Line("12", 0, 0, 20, 20), Line("  ", 30, 0, 10, 20), Line("34", 50, 0, 20, 20) },
        };
        using var image = new Image<Rgb24>(100, 40);

        var result = await service.RecognizeZonesAsync(
            image, new[] { new OcrZone("n", OcrRegion.Pixels(0, 0, 100, 40), ZoneMode.SingleLine) }, English);

        Assert.Equal("12 34", result["n"].Text);
    }

    [Fact]
    public async Task Detect_zone_text_puts_stacked_lines_on_separate_rows()
    {
        var service = new RecordingOcrService
        {
            OnDetect = _ => new[] { Line("Line two", 0, 30, 80, 20), Line("Line one", 0, 0, 80, 20) },
        };
        using var image = new Image<Rgb24>(200, 100);

        var result = await service.RecognizeZonesAsync(
            image, new[] { new OcrZone("block", OcrRegion.Fraction(0, 0, 1, 1)) }, English);

        Assert.Equal("Line one\nLine two", result["block"].Text);
    }

    [Fact]
    public async Task Zone_outside_the_image_yields_an_empty_result_without_calling_the_service()
    {
        var service = new RecordingOcrService();
        using var image = new Image<Rgb24>(200, 100);
        var zones = new[]
        {
            new OcrZone("outside", OcrRegion.Pixels(500, 500, 10, 10)),
            new OcrZone("sliver", OcrRegion.Pixels(10, 10, 1, 40), ZoneMode.SingleLine),
        };

        var result = await service.RecognizeZonesAsync(image, zones, English);

        Assert.Empty(service.DetectOptions);
        Assert.Empty(service.RegionCalls);
        foreach (var zone in result.Zones.Values)
        {
            Assert.Equal(string.Empty, zone.Text);
            Assert.Equal(0, zone.Confidence);
            Assert.Empty(zone.Lines);
            Assert.Equal(200, zone.Result.SourceWidth);
        }
        Assert.Equal(OcrBoundingBox.Empty, result["outside"].PixelBounds);
    }

    [Fact]
    public async Task Results_keep_zone_order_and_expose_a_value_map()
    {
        var service = new RecordingOcrService
        {
            OnDetect = o => new[] { Line(o!.Region!.Value.X < 50 ? "INV-7" : "2026-03-12", 0, 0, 50, 20) },
        };
        using var image = new Image<Rgb24>(300, 100);
        var zones = new[]
        {
            new OcrZone("number", OcrRegion.Pixels(0, 0, 100, 30)),
            new OcrZone("date", OcrRegion.Pixels(150, 0, 100, 30)),
        };

        var result = await service.RecognizeZonesAsync(image, zones, new[] { "en", "de" });

        Assert.Equal(new[] { "number", "date" }, result.Zones.Keys);
        Assert.Equal(new[] { "INV-7", "2026-03-12" }, result.ToValueMap().Values);
        Assert.Equal("INV-7", result.ToValueMap()["number"]);
        Assert.Equal(300, result.SourceWidth);
        Assert.Equal(100, result.SourceHeight);
        Assert.True(result.Duration >= TimeSpan.Zero);
        Assert.All(service.Languages, l => Assert.Equal(new[] { "en", "de" }, l));
        Assert.Throws<KeyNotFoundException>(() => result["missing"]);
    }

    [Fact]
    public async Task Invalid_zone_definitions_are_rejected_before_any_ocr()
    {
        var service = new RecordingOcrService();
        using var image = new Image<Rgb24>(10, 10);
        var region = OcrRegion.Pixels(0, 0, 5, 5);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RecognizeZonesAsync(
            image, new[] { new OcrZone("a", region), new OcrZone("a", region) }, English));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RecognizeZonesAsync(
            image, new[] { new OcrZone(" ", region) }, English));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RecognizeZonesAsync(
            image, new OcrZone[] { null! }, English));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RecognizeZonesAsync(
            image, new[] { new OcrZone("m", region, (ZoneMode)7) }, English));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RecognizeZonesAsync(
            image, new[] { new OcrZone("nan", OcrRegion.Pixels(double.NaN, 0, 5, 5)) }, English));

        Assert.Empty(service.DetectOptions);
    }

    [Fact]
    public async Task Null_arguments_are_rejected()
    {
        var service = new RecordingOcrService();
        using var image = new Image<Rgb24>(10, 10);
        var zones = new[] { new OcrZone("a", OcrRegion.Pixels(0, 0, 5, 5)) };

        await Assert.ThrowsAsync<ArgumentNullException>(() => ((IEasyOcrService)null!).RecognizeZonesAsync(image, zones, English));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.RecognizeZonesAsync((Image<Rgb24>)null!, zones, English));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.RecognizeZonesAsync(image, null!, English));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.RecognizeZonesAsync(image, zones, null!));
    }

    [Fact]
    public async Task Cancellation_is_observed_between_zones()
    {
        var service = new RecordingOcrService();
        using var image = new Image<Rgb24>(10, 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RecognizeZonesAsync(
            image, new[] { new OcrZone("a", OcrRegion.Pixels(0, 0, 5, 5)) }, English, cancellationToken: cts.Token));
        Assert.Empty(service.DetectOptions);
    }

    [Fact]
    public async Task Path_overload_decodes_the_file_once_and_reads_every_zone()
    {
        var path = Path.Combine(Path.GetTempPath(), "easyocr_zone_" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var source = new Image<Rgb24>(120, 60, new Rgb24(255, 255, 255)))
            {
                await source.SaveAsync(path);
            }

            var service = new RecordingOcrService { OnDetect = _ => new[] { Line("x", 0, 0, 10, 20) } };
            var result = await service.RecognizeZonesAsync(
                path,
                new[] { new OcrZone("left", OcrRegion.Fraction(0, 0, 0.5, 1)), new OcrZone("right", OcrRegion.Fraction(0.5, 0, 0.5, 1)) },
                English);

            Assert.Equal(120, result.SourceWidth);
            Assert.Equal(60, result.SourceHeight);
            Assert.Equal(2, service.DetectOptions.Count);
            Assert.Equal(new OcrBoundingBox(60, 0, 120, 60), result["right"].PixelBounds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Path_overload_validates_the_path()
    {
        var service = new RecordingOcrService();
        var zones = new[] { new OcrZone("a", OcrRegion.Pixels(0, 0, 5, 5)) };

        await Assert.ThrowsAsync<ArgumentException>(() => service.RecognizeZonesAsync("  ", zones, English));
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.RecognizeZonesAsync(
            Path.Combine(Path.GetTempPath(), "missing_" + Guid.NewGuid().ToString("N") + ".png"), zones, English));
    }

    /// <summary>Records every call the zone reader makes and answers with caller-supplied lines.</summary>
    private sealed class RecordingOcrService : IEasyOcrService
    {
        public List<RecognitionOptions?> DetectOptions { get; } = new();

        public List<(IReadOnlyList<OcrPoint> Polygon, RecognitionOptions? Options)> RegionCalls { get; } = new();

        public List<string[]> Languages { get; } = new();

        public Func<RecognitionOptions?, IReadOnlyList<OcrLine>> OnDetect { get; init; } = _ => Array.Empty<OcrLine>();

        public Func<IReadOnlyList<OcrPoint>, IReadOnlyList<OcrLine>> OnRegion { get; init; } = _ => Array.Empty<OcrLine>();

        private static OcrResult Result(IReadOnlyList<OcrLine> lines, Image<Rgb24> image, string[] languages) => new()
        {
            FullText = string.Join('\n', lines.Select(l => l.Text)),
            Lines = lines,
            Languages = languages,
            SourceWidth = image.Width,
            SourceHeight = image.Height,
        };

        public Task<OcrResult> ExtractTextFromImage(
            Image<Rgb24> image, IEnumerable<string> languages, RecognitionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var langs = languages.ToArray();
            DetectOptions.Add(options);
            Languages.Add(langs);
            return Task.FromResult(Result(OnDetect(options), image, langs));
        }

        public Task<OcrResult> RecognizeRegionsAsync(
            Image<Rgb24> image, IEnumerable<IReadOnlyList<OcrPoint>> regions, IEnumerable<string> languages,
            RecognitionOptions? options = null, CancellationToken cancellationToken = default)
        {
            var langs = languages.ToArray();
            var polygons = regions.ToList();
            foreach (var polygon in polygons) RegionCalls.Add((polygon, options));
            Languages.Add(langs);
            return Task.FromResult(Result(OnRegion(polygons[0]), image, langs));
        }

        public Task<OcrResult> ExtractTextFromImage(string imagePath, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OcrResult> ExtractTextFromImage(Stream imageStream, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OcrResult> ExtractTextFromImage(byte[] imageBytes, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OcrResult> ExtractTextFromImage(ReadOnlyMemory<byte> imageBytes, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> DetectLanguagesAsync(Image<Rgb24> image, IEnumerable<string>? candidates = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> DetectLanguagesAsync(string imagePath, IEnumerable<string>? candidates = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
