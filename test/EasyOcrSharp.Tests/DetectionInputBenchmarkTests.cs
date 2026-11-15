using System.Diagnostics;
using System.Text;
using EasyOcrSharp.Evaluation;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Before/after measurement harness for the detection and input options. For each option it OCRs the variant
/// the option targets — a tall stitched receipt and a wide banner (<see cref="DetectionOptions.TileLargeImages"/>),
/// half-scale pages (<see cref="DetectionOptions.MinTextHeight"/>), faded unevenly lit pages
/// (<see cref="DetectionOptions.EnhanceContrast"/>), 204×98 DPI fax renditions
/// (<see cref="PreprocessingOptions.CorrectNonSquarePixels"/>) and tilted pages (<see cref="PreprocessingOptions.Deskew"/>
/// coordinates) — with the option off and on, plus the clean pages with the option on, and reports lines found,
/// mean confidence, token recall and CER against the clean page's default-options transcript, known strings
/// recovered, whether a clean page came out identical to the option off, and seconds per call.
/// </summary>
/// <remarks>
/// <para>
/// Results go to the test output and are appended to <c>detection-input-benchmark.md</c> next to the test
/// assembly (or to the path in <c>EASYOCRSHARP_BENCH_REPORT</c>). The page subset is two pages per category by
/// default; <c>EASYOCRSHARP_BENCH_PAGES=all</c> runs the whole 100-page corpus. Timings are wall-clock on a
/// shared machine and only indicative.
/// </para>
/// <para>
/// Assertions are limited to what must hold regardless of accuracy: options that cannot trigger on a page
/// leave it byte-identical, and deskewed coordinates sit closer to the ink than the canvas coordinates the
/// library used to return. Quality is reported, not gated — floors belong in tests once the numbers are known.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Category", "Benchmark")]
[Collection(OcrIntegrationCollection.Name)]
public sealed class DetectionInputBenchmarkTests : IClassFixture<DetectionInputBenchmarkTests.Baseline>
{
    private static readonly string[] En = { "en" };

    /// <summary>Two pages per corpus category.</summary>
    private static readonly string[] DefaultPages =
    {
        "001_plain_text", "003_plain_text", "014_multi_column", "019_multi_column", "024_tables", "030_tables",
        "038_forms", "042_forms", "048_seals_stamps", "052_seals_stamps", "057_receipts_invoices", "060_receipts_invoices",
        "069_low_quality", "071_low_quality", "077_rotated_perspective", "079_rotated_perspective", "082_dense_mixed",
        "085_dense_mixed", "092_handwriting_like", "094_handwriting_like", "097_numbers_codes", "099_numbers_codes",
    };

    private readonly Baseline _baseline;
    private readonly ITestOutputHelper _out;

    public DetectionInputBenchmarkTests(Baseline baseline, ITestOutputHelper output)
    {
        _baseline = baseline;
        _out = output;
    }

    /// <summary>One measured OCR call.</summary>
    public sealed record Row(
        string Variant, string Page, string Option, int Lines, double MeanConfidence, double TokenRecall, double Cer,
        int KnownRecovered, int KnownPresent, bool? IdenticalToOff, double Seconds);

    /// <summary>Shares one service and the clean default-options transcripts across the cases.</summary>
    public sealed class Baseline : IAsyncDisposable
    {
        private readonly Dictionary<string, OcrResult> _clean = new(StringComparer.Ordinal);

        public EasyOcrService Ocr { get; } = new();

        public async Task<OcrResult> CleanAsync(string page, Image<Rgb24> image)
        {
            if (_clean.TryGetValue(page, out var cached)) return cached;
            var result = await Ocr.ExtractTextFromImage(image, En).ConfigureAwait(false);
            _clean[page] = result;
            return result;
        }

        public ValueTask DisposeAsync() => Ocr.DisposeAsync();
    }

    private static string[] Pages()
    {
        var selection = Environment.GetEnvironmentVariable("EASYOCRSHARP_BENCH_PAGES");
        var available = CorpusPages().ToHashSet(StringComparer.Ordinal);
        string[] pages = string.Equals(selection, "all", StringComparison.OrdinalIgnoreCase)
            ? available.OrderBy(p => p, StringComparer.Ordinal).ToArray()
            : DefaultPages.Where(available.Contains).ToArray();
        Skip.If(pages.Length == 0, "100-image benchmark corpus (test/assets/NNN_category.png) not present.");
        return pages;
    }

    /// <summary>The corpus page names (no extension), in file order.</summary>
    private static IEnumerable<string> CorpusPages()
        => OcrDatasetBenchmarkTests.CorpusFiles().Select(p => Path.GetFileNameWithoutExtension(p));

    private static Image<Rgb24> Load(string page)
        => DecodeLimits.Load(File.ReadAllBytes(Path.Combine(OcrDatasetBenchmarkTests.CorpusDir, page + ".png")), 0, "benchmark");

    private async Task<(OcrResult Result, Row Row)> MeasureAsync(
        string variant, string page, string option, Image<Rgb24> image, RecognitionOptions? options, string reference, OcrResult? off = null)
    {
        var sw = Stopwatch.StartNew();
        var result = await _baseline.Ocr.ExtractTextFromImage(image, En, options);
        sw.Stop();

        var (known, present) = DegradedImages.KnownStringHits(reference, result.FullText);
        double cer;
        try
        {
            cer = TextAccuracyMetrics.CharacterErrorRate(reference, result.FullText, TextComparisonOptions.Relaxed);
        }
        catch (Exception)
        {
            cer = double.NaN; // comparison too large for the edit-distance budget
        }

        var row = new Row(variant, page, option, result.Lines.Count, DegradedImages.MeanConfidence(result),
            DegradedImages.TokenRecall(reference, result.FullText), cer, known, present,
            off is null ? null : DegradedImages.SameLines(off, result), sw.Elapsed.TotalSeconds);
        return (result, row);
    }

    // ---- TileLargeImages -----------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task TileLargeImages_on_tall_and_wide_pages()
    {
        var pages = Pages();
        var rows = new List<Row>();
        var tiled = new RecognitionOptions { Detection = new DetectionOptions { TileLargeImages = true } };

        // Tall: every receipt page, cropped to its content and stacked (~8–10k px).
        var receipts = CorpusPages().Where(p => p.EndsWith("receipts_invoices", StringComparison.Ordinal)).ToArray();
        // Wide: four plain-text pages side by side (~5k px) — tiled along x, so seams cut through text lines.
        var banners = CorpusPages().Where(p => p.EndsWith("plain_text", StringComparison.Ordinal)).Take(4).ToArray();

        foreach (var (variant, names, wide) in new[] { ("tall receipt", receipts, false), ("wide banner", banners, true) })
        {
            if (names.Length < 2) continue;
            var pieces = names.Select(n => { using var p = Load(n); return DegradedImages.CropToContent(p); }).ToList();
            try
            {
                using var stacked = wide ? DegradedImages.StackHorizontally(pieces) : DegradedImages.StackVertically(pieces);
                var transcripts = new List<string>();
                for (int i = 0; i < pieces.Count; i++) transcripts.Add((await _baseline.CleanAsync(names[i] + "#crop", pieces[i])).FullText);
                string reference = string.Join("\n", transcripts);

                string label = $"{variant} {stacked.Width}x{stacked.Height}";
                var (off, offRow) = await MeasureAsync(label, $"{names.Length} pages", "off", stacked, null, reference);
                var (_, onRow) = await MeasureAsync(label, $"{names.Length} pages", "TileLargeImages", stacked, tiled, reference, off);
                rows.Add(offRow);
                rows.Add(onRow);
            }
            finally
            {
                foreach (var p in pieces) p.Dispose();
            }
        }

        // Clean pages: none of them is large enough to tile, so the option must change nothing.
        foreach (var page in pages)
        {
            using var image = Load(page);
            var clean = await _baseline.CleanAsync(page, image);
            var (_, row) = await MeasureAsync("clean", page, "TileLargeImages", image, tiled, clean.FullText, clean);
            rows.Add(row);
        }

        Report("TileLargeImages", rows);
        Assert.All(rows.Where(r => r.Variant == "clean"), r => Assert.True(r.IdenticalToOff, $"{r.Page} changed with tiling on"));
    }

    // ---- MinTextHeight -------------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task MinTextHeight_on_half_scale_and_clean_pages()
    {
        var rows = new List<Row>();
        foreach (var page in Pages())
        {
            using var image = Load(page);
            var clean = await _baseline.CleanAsync(page, image);
            using var half = DegradedImages.HalfScale(image);

            var (off, offRow) = await MeasureAsync("half-scale", page, "off", half, null, clean.FullText);
            rows.Add(offRow);
            foreach (int threshold in new[] { 12, 16, 20 })
            {
                var options = new RecognitionOptions { Detection = new DetectionOptions { MinTextHeight = threshold } };
                rows.Add((await MeasureAsync("half-scale", page, $"MinTextHeight={threshold}", half, options, clean.FullText, off)).Row);
            }

            var on16 = new RecognitionOptions { Detection = new DetectionOptions { MinTextHeight = 16 } };
            rows.Add((await MeasureAsync("clean", page, "MinTextHeight=16", image, on16, clean.FullText, clean)).Row);
        }

        Report("MinTextHeight", rows);
    }

    // ---- EnhanceContrast -----------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task EnhanceContrast_on_faded_and_clean_pages()
    {
        var rows = new List<Row>();
        var enhanced = new RecognitionOptions { Detection = new DetectionOptions { EnhanceContrast = true } };
        foreach (var page in Pages())
        {
            using var image = Load(page);
            var clean = await _baseline.CleanAsync(page, image);
            using var faded = DegradedImages.Faded(image);

            var (off, offRow) = await MeasureAsync("faded", page, "off", faded, null, clean.FullText);
            rows.Add(offRow);
            rows.Add((await MeasureAsync("faded", page, "EnhanceContrast", faded, enhanced, clean.FullText, off)).Row);
            rows.Add((await MeasureAsync("clean", page, "EnhanceContrast", image, enhanced, clean.FullText, clean)).Row);
        }

        Report("EnhanceContrast", rows);
    }

    // ---- CorrectNonSquarePixels ----------------------------------------------------------------------------------

    [SkippableFact]
    public async Task CorrectNonSquarePixels_on_fax_and_clean_pages()
    {
        var rows = new List<Row>();
        var uncorrected = new RecognitionOptions { Preprocessing = new PreprocessingOptions { CorrectNonSquarePixels = false } };
        foreach (var page in Pages())
        {
            using var image = Load(page);
            var clean = await _baseline.CleanAsync(page, image);
            using var fax = DegradedImages.Fax(image);

            var (off, offRow) = await MeasureAsync("204x98 fax", page, "off", fax, uncorrected, clean.FullText);
            rows.Add(offRow);
            rows.Add((await MeasureAsync("204x98 fax", page, "CorrectNonSquarePixels", fax, null, clean.FullText, off)).Row);

            // Square pixels: the default-on correction must be a no-op.
            var (cleanOff, _) = await MeasureAsync("clean", page, "off", image, uncorrected, clean.FullText);
            rows.Add((await MeasureAsync("clean", page, "CorrectNonSquarePixels", image, null, clean.FullText, cleanOff)).Row);
        }

        Report("CorrectNonSquarePixels", rows);
        Assert.All(rows.Where(r => r.Variant == "clean"), r => Assert.True(r.IdenticalToOff, $"{r.Page} changed by the square-pixel correction"));
    }

    // ---- Deskew coordinates --------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Deskew_coordinates_against_the_undeskewed_reading()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| page | tilt | DeskewAngle | matched lines | mean err (new) px | max err (new) px | mean err (old canvas) px | max err (old canvas) px | recall vs clean |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");

        var deskew = new RecognitionOptions { Preprocessing = new PreprocessingOptions { Deskew = true } };
        var errorsNew = new List<double>();
        var errorsOld = new List<double>();
        foreach (var page in Pages())
        {
            using var image = Load(page);
            var clean = await _baseline.CleanAsync(page, image);
            foreach (float tilt in new[] { 3f, -4f })
            {
                using var tilted = DegradedImages.Tilted(image, tilt);
                var plain = await _baseline.Ocr.ExtractTextFromImage(tilted, En);
                var fixedResult = await _baseline.Ocr.ExtractTextFromImage(tilted, En, deskew);

                // What the pipeline used to return: the same boxes on the deskew canvas.
                DeskewCanvas? canvas = null;
                if (fixedResult.DeskewAngle != 0f)
                {
                    var (cw, ch) = DeskewRotation.PredictCanvasSize(tilted.Width, tilted.Height, fixedResult.DeskewAngle);
                    canvas = new DeskewCanvas(fixedResult.DeskewAngle, cw, ch, tilted.Width, tilted.Height);
                }

                var pageNew = new List<double>();
                var pageOld = new List<double>();
                foreach (var line in fixedResult.Lines)
                {
                    var twin = plain.Lines.FirstOrDefault(p => p.Text == line.Text);
                    if (twin is null) continue;
                    var center = new OcrPoint(twin.BoundingBox.CenterX, twin.BoundingBox.CenterY);
                    pageNew.Add(Distance(new OcrPoint(line.BoundingBox.CenterX, line.BoundingBox.CenterY), center));
                    if (canvas is { } c)
                    {
                        var old = OcrBoundingBox.FromPoints(line.BoundingPolygon.Select(c.ToCanvas));
                        pageOld.Add(Distance(new OcrPoint(old.CenterX, old.CenterY), center));
                    }
                }
                errorsNew.AddRange(pageNew);
                errorsOld.AddRange(pageOld);

                sb.AppendLine($"| {page} | {tilt:+0;-0}° | {fixedResult.DeskewAngle:F2} | {pageNew.Count} | {Mean(pageNew):F1} | {Max(pageNew):F1} | " +
                              $"{Mean(pageOld):F1} | {Max(pageOld):F1} | {DegradedImages.TokenRecall(clean.FullText, fixedResult.FullText):P1} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Overall: {errorsNew.Count} matched lines, mean centre error {Mean(errorsNew):F1} px now vs {Mean(errorsOld):F1} px on the old canvas frame.");
        Write("Deskew coordinates", sb.ToString());

        if (errorsOld.Count > 0)
        {
            Assert.True(Mean(errorsNew) < Mean(errorsOld), $"deskewed boxes are not closer to the ink: {Mean(errorsNew):F1} vs {Mean(errorsOld):F1}");
        }
    }

    // ---- reporting -----------------------------------------------------------------------------------------------

    private void Report(string option, IReadOnlyList<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| variant | page | option | lines | mean conf | token recall | CER | known strings | identical to off | s |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            sb.AppendLine($"| {r.Variant} | {r.Page} | {r.Option} | {r.Lines} | {r.MeanConfidence:F3} | {r.TokenRecall:P1} | " +
                          $"{(double.IsNaN(r.Cer) ? "n/a" : r.Cer.ToString("P1"))} | {r.KnownRecovered}/{r.KnownPresent} | " +
                          $"{(r.IdenticalToOff is null ? "" : r.IdenticalToOff.Value ? "yes" : "no")} | {r.Seconds:F1} |");
        }

        sb.AppendLine();
        sb.AppendLine("Summary (mean over pages):");
        foreach (var g in rows.GroupBy(r => (r.Variant.Split(' ')[0], r.Option)))
        {
            var list = g.ToList();
            var cers = list.Where(r => !double.IsNaN(r.Cer)).ToList();
            sb.AppendLine($"- {g.Key.Item1} / {g.Key.Option}: n={list.Count}, lines {list.Average(r => r.Lines):F1}, mean conf {list.Average(r => r.MeanConfidence):F3}, " +
                          $"token recall {list.Average(r => r.TokenRecall):P1}, CER {(cers.Count == 0 ? double.NaN : cers.Average(r => r.Cer)):P1}, " +
                          $"known {list.Sum(r => r.KnownRecovered)}/{list.Sum(r => r.KnownPresent)}" +
                          (list.Any(r => r.IdenticalToOff is not null) ? $", identical {list.Count(r => r.IdenticalToOff == true)}/{list.Count(r => r.IdenticalToOff is not null)}" : "") +
                          $", {list.Average(r => r.Seconds):F1} s/call");
        }

        Write(option, sb.ToString());
    }

    private void Write(string title, string body)
    {
        string section = $"## {title}{Environment.NewLine}{Environment.NewLine}{body}{Environment.NewLine}";
        _out.WriteLine(section);
        var path = Environment.GetEnvironmentVariable("EASYOCRSHARP_BENCH_REPORT")
            ?? Path.Combine(AppContext.BaseDirectory, "detection-input-benchmark.md");
        File.AppendAllText(path, section);
    }

    private static double Distance(OcrPoint a, OcrPoint b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static double Mean(IReadOnlyCollection<double> values) => values.Count == 0 ? double.NaN : values.Average();

    private static double Max(IReadOnlyCollection<double> values) => values.Count == 0 ? double.NaN : values.Max();
}
