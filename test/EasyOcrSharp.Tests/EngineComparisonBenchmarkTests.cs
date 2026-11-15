using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Head-to-head accuracy and latency of the two plain-OCR engines — the default EasyOCR pipeline (CRAFT +
/// CRNN) and the opt-in PP-OCRv5 pipeline (<see cref="RecognitionEngine.PpOcrV5"/>) — on the same inputs,
/// through the same public API and the same service instance. The numbers are decision data for which
/// languages PP-OCRv5 should be recommended (or restricted) for; the assertions only guard that both
/// engines ran.
/// </summary>
/// <remarks>
/// <para>Three suites, each writing a Markdown + JSON report (to <c>EASYOCRSHARP_BENCHMARK_REPORT_DIR</c>,
/// else next to the test assembly) and echoing the Markdown to the test output:</para>
/// <list type="bullet">
/// <item><description><b>Corpus</b> — the 100-page synthetic set (<c>test/assets/NNN_category.png</c>): the
/// recovered known strings per category (the <c>OCR TEST</c> banner, the plain-text pangram / e-mail /
/// product name, the low-quality alphabet and digit probes), line counts, latency, and full CER/WER on the
/// four pages with a hand transcription (tables, forms, receipts, numbers).</description></item>
/// <item><description><b>Real assets</b> — <c>sample.png</c>, <c>sample2.png</c>, the six-language
/// eurotext probe (<c>ocr_test1.png</c>) with full transcriptions, plus printed-word recall on the sign
/// collage (<c>ocr_test3.png</c>) and the scanned form (<c>structure_sample.png</c>).</description></item>
/// <item><description><b>Scripts</b> — the twenty real multilingual sign panels of <c>ocr_test4.png</c>
/// (Han, Kana, Hangul, Cyrillic, Devanagari, Arabic, Thai, Greek, and Latin with French, German, Spanish,
/// Vietnamese, Portuguese, Turkish, Polish and Italian diacritics), each read with its own language codes;
/// order-free character F1 is the primary metric there.</description></item>
/// </list>
/// <para>
/// Timing: both engines are warmed up first (model load excluded), then every input is run through both,
/// alternating which engine goes first, so drift from concurrent load on the machine hits both equally.
/// Timings include image decoding, identically for both engines. Integration: downloads the EasyOCR and
/// PP-OCRv5 models for every script on first run.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Benchmark", "EngineComparison")]
[Collection(OcrIntegrationCollection.Name)]
public sealed class EngineComparisonBenchmarkTests
{
    private static readonly RecognitionOptions EasyOcrOptions = RecognitionOptions.Default;
    private static readonly RecognitionOptions PpOcrOptions = new() { Engine = RecognitionEngine.PpOcrV5 };

    private readonly ITestOutputHelper _out;

    public EngineComparisonBenchmarkTests(ITestOutputHelper output) => _out = output;

    /// <summary>One engine's reading of one input. <see cref="Error"/> is set (and the rest empty) when it threw.</summary>
    public sealed record EngineRun(string Text, int Lines, double MeanConfidence, double Milliseconds, string? Error)
    {
        public bool Failed => Error is not null;
    }

    /// <summary>Accuracy of one run against a reference.</summary>
    public sealed record Score(double? Cer, double? Wer, double? CharF1, double? WordRecall);

    // ---- corpus ---------------------------------------------------------------------------------------------

    /// <summary>One corpus page as read by both engines.</summary>
    public sealed record CorpusPage(
        string Name, string Category, EngineRun EasyOcr, EngineRun PpOcr,
        IReadOnlyDictionary<string, bool> EasyOcrProbes, IReadOnlyDictionary<string, bool> PpOcrProbes,
        Score? EasyOcrScore, Score? PpOcrScore);

    [SkippableFact]
    public async Task Corpus_100_pages_both_engines_interleaved()
    {
        var files = OcrDatasetBenchmarkTests.CorpusFiles();
        Skip.If(files.Length == 0, "100-image benchmark corpus (test/assets/NNN_category.png) not present.");

        await using var ocr = new EasyOcrService();
        await WarmUpAsync(ocr, files[0], ["en"]);

        var references = EngineBenchmarkData.FullTextSamples.ToDictionary(s => s.Asset, StringComparer.OrdinalIgnoreCase);
        var pages = new List<CorpusPage>(files.Length);
        for (int i = 0; i < files.Length; i++)
        {
            var path = files[i];
            var name = Path.GetFileName(path);
            var category = OcrDatasetBenchmarkTests.CategoryOf(name);
            var (easy, pp) = await RunBothAsync(i, options => ocr.ExtractTextFromImage(path, ["en"], options));

            Score? easyScore = null, ppScore = null;
            if (references.TryGetValue(name, out var reference))
            {
                easyScore = ScoreFullText(reference.Reference, easy);
                ppScore = ScoreFullText(reference.Reference, pp);
            }

            pages.Add(new CorpusPage(name, category, easy, pp, Probes(category, easy.Text), Probes(category, pp.Text), easyScore, ppScore));
        }

        var md = new StringBuilder();
        AppendHeader(md, "Corpus (100 synthetic pages, en)", ocr);

        md.AppendLine("| category | n | probe hits EasyOCR | probe hits PP-OCRv5 | lines EasyOCR | lines PP-OCRv5 | ms EasyOCR | ms PP-OCRv5 |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var g in pages.GroupBy(p => p.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {g.Key} | {g.Count()} | {ProbeRate(g, p => p.EasyOcrProbes)} | {ProbeRate(g, p => p.PpOcrProbes)} | " +
                $"{g.Average(p => p.EasyOcr.Lines):0.0} | {g.Average(p => p.PpOcr.Lines):0.0} | " +
                $"{Median(g.Select(p => p.EasyOcr.Milliseconds)):0} | {Median(g.Select(p => p.PpOcr.Milliseconds)):0} |");
        }
        md.AppendLine(CultureInfo.InvariantCulture,
            $"| **all** | {pages.Count} | {ProbeRate(pages, p => p.EasyOcrProbes)} | {ProbeRate(pages, p => p.PpOcrProbes)} | " +
            $"{pages.Average(p => p.EasyOcr.Lines):0.0} | {pages.Average(p => p.PpOcr.Lines):0.0} | " +
            $"{Median(pages.Select(p => p.EasyOcr.Milliseconds)):0} | {Median(pages.Select(p => p.PpOcr.Milliseconds)):0} |");

        md.AppendLine();
        md.AppendLine("Probe hits per string (pages recovering it / pages carrying it):");
        md.AppendLine();
        md.AppendLine("| probe | EasyOCR | PP-OCRv5 |");
        md.AppendLine("|---|---:|---:|");
        foreach (var probe in pages.SelectMany(p => p.EasyOcrProbes.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            var carrying = pages.Where(p => p.EasyOcrProbes.ContainsKey(probe)).ToList();
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {probe} | {carrying.Count(p => p.EasyOcrProbes[probe])}/{carrying.Count} | {carrying.Count(p => p.PpOcrProbes[probe])}/{carrying.Count} |");
        }

        var scored = pages.Where(p => p.EasyOcrScore is not null).ToList();
        if (scored.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("Full-transcription pages (relaxed: case, whitespace and punctuation ignored):");
            md.AppendLine();
            AppendScoreHeader(md);
            foreach (var p in scored) AppendScoreRow(md, p.Name, p.EasyOcrScore!, p.PpOcrScore!, p.EasyOcr, p.PpOcr);
        }

        AppendFailures(md, pages.Select(p => (p.Name, p.EasyOcr, p.PpOcr)));
        Publish("corpus", md, pages);

        Assert.True(pages.Count(p => p.PpOcr.Lines > 0) >= 0.9 * pages.Count,
            "PP-OCRv5 returned no text on more than 10% of the corpus — see the report.");
        Assert.DoesNotContain(pages, p => p.PpOcr.Failed);
    }

    // ---- real assets ----------------------------------------------------------------------------------------

    /// <summary>One real asset as read by both engines.</summary>
    public sealed record AssetResult(string Name, string Asset, string Metric, EngineRun EasyOcr, EngineRun PpOcr, Score EasyOcrScore, Score PpOcrScore);

    [Fact]
    public async Task Real_assets_both_engines_interleaved()
    {
        await using var ocr = new EasyOcrService();
        var sample = EngineBenchmarkData.RepoAsset("sample.png");
        Assert.True(sample is not null, "sample.png not found.");
        await WarmUpAsync(ocr, sample!, ["en"]);

        var results = new List<AssetResult>();
        var missing = new List<string>();
        int index = 0;

        foreach (var s in EngineBenchmarkData.FullTextSamples.Where(s => !char.IsDigit(s.Asset[0])))
        {
            var path = EngineBenchmarkData.RepoAsset(s.Asset);
            if (path is null) { missing.Add(s.Asset); continue; }
            var (easy, pp) = await RunBothAsync(index++, options => ocr.ExtractTextFromImage(path, s.Languages, options));
            results.Add(new AssetResult(s.Name, s.Asset, "full text", easy, pp, ScoreFullText(s.Reference, easy), ScoreFullText(s.Reference, pp)));
        }

        foreach (var s in EngineBenchmarkData.WordBagSamples)
        {
            var path = EngineBenchmarkData.RepoAsset(s.Asset);
            if (path is null) { missing.Add(s.Asset); continue; }
            var (easy, pp) = await RunBothAsync(index++, options => ocr.ExtractTextFromImage(path, s.Languages, options));
            results.Add(new AssetResult(s.Name, s.Asset, "printed-word recall", easy, pp, ScoreWordBag(s.Words, easy), ScoreWordBag(s.Words, pp)));
        }

        var md = new StringBuilder();
        AppendHeader(md, "Real assets (test/assets)", ocr);
        md.AppendLine("Relaxed comparison (case, whitespace and punctuation ignored). Word-bag pages carry only word recall.");
        md.AppendLine();
        AppendScoreHeader(md);
        foreach (var r in results) AppendScoreRow(md, $"{r.Name} ({r.Asset})", r.EasyOcrScore, r.PpOcrScore, r.EasyOcr, r.PpOcr);
        if (missing.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("Missing assets (skipped): " + string.Join(", ", missing));
        }

        AppendFailures(md, results.Select(r => (r.Name, r.EasyOcr, r.PpOcr)));
        Publish("real-assets", md, results);

        Assert.NotEmpty(results);
        Assert.DoesNotContain(results, r => r.PpOcr.Failed);
    }

    // ---- per-script sign panels -----------------------------------------------------------------------------

    /// <summary>One sign panel as read by both engines (EasyOCR is null where it has no recognizer).</summary>
    public sealed record PanelResult(
        string Name, string Script, string[]? EasyOcrLanguages, string[] PpOcrLanguages,
        EngineRun? EasyOcr, EngineRun PpOcr, Score? EasyOcrScore, Score PpOcrScore);

    [SkippableFact]
    public async Task Script_sign_panels_both_engines_interleaved()
    {
        var collagePath = EngineBenchmarkData.RepoAsset("ocr_test4.png");
        Skip.If(collagePath is null, "test/assets/ocr_test4.png not found.");

        using var collage = await Image.LoadAsync<Rgb24>(collagePath!);
        await using var ocr = new EasyOcrService();
        using (var first = EngineBenchmarkData.CropPanel(collage, EngineBenchmarkData.SignPanels[0]))
        {
            await RunAsync(() => ocr.ExtractTextFromImage(first, ["en"], EasyOcrOptions));
            await RunAsync(() => ocr.ExtractTextFromImage(first, ["en"], PpOcrOptions));
        }

        var results = new List<PanelResult>();
        for (int i = 0; i < EngineBenchmarkData.SignPanels.Count; i++)
        {
            var panel = EngineBenchmarkData.SignPanels[i];
            using var crop = EngineBenchmarkData.CropPanel(collage, panel);
            bool rtl = panel.Script == "Arabic";

            EngineRun? easy = null;
            EngineRun pp;
            if (panel.EasyOcrLanguages is { } easyLanguages)
            {
                (easy, pp) = await RunBothAsync(i, options => ocr.ExtractTextFromImage(
                    crop, options.Engine == RecognitionEngine.EasyOcr ? easyLanguages : panel.PpOcrLanguages, options));
            }
            else
            {
                pp = await RunAsync(() => ocr.ExtractTextFromImage(crop, panel.PpOcrLanguages, PpOcrOptions));
            }

            results.Add(new PanelResult(
                panel.Name, panel.Script, panel.EasyOcrLanguages, panel.PpOcrLanguages, easy, pp,
                easy is null ? null : ScorePanel(panel.Reference, easy, rtl), ScorePanel(panel.Reference, pp, rtl)));
        }

        var md = new StringBuilder();
        AppendHeader(md, "Scripts (20 real sign panels from ocr_test4.png)", ocr);
        md.AppendLine("charF1 = order-free character F1 (letters, digits, combining marks; primary metric). " +
                      "CER is relaxed and, for Arabic, the better of logical and display order.");
        md.AppendLine();
        md.AppendLine("| panel | script | languages | charF1 EasyOCR | charF1 PP-OCRv5 | CER EasyOCR | CER PP-OCRv5 | ms EasyOCR | ms PP-OCRv5 | EasyOCR text | PP-OCRv5 text |");
        md.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---|---|");
        foreach (var r in results)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.Name} | {r.Script} | {string.Join("+", r.PpOcrLanguages)} | " +
                $"{Fmt(r.EasyOcrScore?.CharF1)} | {Fmt(r.PpOcrScore.CharF1)} | {Fmt(r.EasyOcrScore?.Cer)} | {Fmt(r.PpOcrScore.Cer)} | " +
                $"{(r.EasyOcr is null ? "n/a" : r.EasyOcr.Milliseconds.ToString("0", CultureInfo.InvariantCulture))} | {r.PpOcr.Milliseconds:0} | " +
                $"{Cell(r.EasyOcr)} | {Cell(r.PpOcr)} |");
        }

        md.AppendLine();
        md.AppendLine("Per script (mean charF1; panels where EasyOCR has no recognizer are excluded from its mean):");
        md.AppendLine();
        md.AppendLine("| script | panels | EasyOCR | PP-OCRv5 | delta |");
        md.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var g in results.GroupBy(r => ScriptFamily(r.Script)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var easyScores = g.Where(r => r.EasyOcrScore?.CharF1 is not null).Select(r => r.EasyOcrScore!.CharF1!.Value).ToList();
            double? easyMean = easyScores.Count > 0 ? easyScores.Average() : null;
            double ppMean = g.Average(r => r.PpOcrScore.CharF1 ?? 0);
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {g.Key} | {g.Count()} | {Fmt(easyMean)} | {Fmt(ppMean)} | {(easyMean is null ? "n/a" : (ppMean - easyMean.Value).ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture))} |");
        }

        AppendFailures(md, results.Select(r => (r.Name, r.EasyOcr ?? new EngineRun("", 0, 0, 0, null), r.PpOcr)));
        Publish("scripts", md, results);

        Assert.Equal(EngineBenchmarkData.SignPanels.Count, results.Count);
        Assert.DoesNotContain(results, r => r.PpOcr.Failed);
    }

    // ---- running ----------------------------------------------------------------------------------------------

    /// <summary>Loads both engines' models for <paramref name="languages"/> outside any timed window.</summary>
    private static async Task WarmUpAsync(EasyOcrService ocr, string path, string[] languages)
    {
        await RunAsync(() => ocr.ExtractTextFromImage(path, languages, EasyOcrOptions));
        await RunAsync(() => ocr.ExtractTextFromImage(path, languages, PpOcrOptions));
    }

    /// <summary>
    /// Runs one input through both engines, alternating which goes first by <paramref name="index"/> so
    /// background load drifts across both evenly.
    /// </summary>
    private static async Task<(EngineRun EasyOcr, EngineRun PpOcr)> RunBothAsync(int index, Func<RecognitionOptions, Task<OcrResult>> run)
    {
        if (index % 2 == 0)
        {
            var easy = await RunAsync(() => run(EasyOcrOptions));
            var pp = await RunAsync(() => run(PpOcrOptions));
            return (easy, pp);
        }
        else
        {
            var pp = await RunAsync(() => run(PpOcrOptions));
            var easy = await RunAsync(() => run(EasyOcrOptions));
            return (easy, pp);
        }
    }

    /// <summary>Times one call; a failure (e.g. a model that cannot be downloaded) is recorded, not thrown.</summary>
    private static async Task<EngineRun> RunAsync(Func<Task<OcrResult>> call)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await call();
            sw.Stop();
            return new EngineRun(
                result.FullText,
                result.Lines.Count,
                result.Lines.Count == 0 ? 0 : result.Lines.Average(l => l.Confidence),
                sw.Elapsed.TotalMilliseconds,
                null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new EngineRun(string.Empty, 0, 0, sw.Elapsed.TotalMilliseconds, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- scoring ----------------------------------------------------------------------------------------------

    private static Score ScoreFullText(string reference, EngineRun run)
    {
        if (run.Failed) return new Score(null, null, null, null);
        var report = EngineBenchmarkData.Compare(reference, run.Text);
        return new Score(report.CharacterErrorRate, report.WordErrorRate,
            EngineBenchmarkData.CharBagF1(reference, run.Text), EngineBenchmarkData.WordRecall(reference, run.Text));
    }

    private static Score ScoreWordBag(string words, EngineRun run)
        => run.Failed
            ? new Score(null, null, null, null)
            : new Score(null, null, EngineBenchmarkData.CharBagF1(words, run.Text), EngineBenchmarkData.WordRecall(words, run.Text));

    private static Score ScorePanel(string reference, EngineRun run, bool rightToLeft)
    {
        if (run.Failed) return new Score(null, null, null, null);
        var report = EngineBenchmarkData.Compare(reference, run.Text, rightToLeft);
        return new Score(report.CharacterErrorRate, report.WordErrorRate, EngineBenchmarkData.CharBagF1(reference, run.Text), null);
    }

    /// <summary>
    /// The known strings a corpus page of <paramref name="category"/> carries, and whether
    /// <paramref name="text"/> recovers each (whitespace-insensitive, case-insensitive) — the same probes
    /// <see cref="OcrDatasetBenchmarkTests"/> asserts for the default engine.
    /// </summary>
    private static Dictionary<string, bool> Probes(string category, string text)
    {
        var probes = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (category != "low_quality") probes["OCR TEST banner"] = Has(text, "OCR TEST");
        if (category == "plain_text")
        {
            probes["pangram"] = Has(text, "quick brown fox");
            probes["e-mail"] = Has(text, "test.user@example.com");
            probes["product name"] = Has(text, "PaddleOCRNet");
        }
        if (category == "low_quality")
        {
            probes["alphabet probe"] = Has(text, "abcdefghijklmnopqrstuvwxyz");
            probes["digit probe"] = Has(text, "0123456789");
        }
        return probes;
    }

    private static bool Has(string text, string needle)
        => StripSpaces(text).Contains(StripSpaces(needle), StringComparison.OrdinalIgnoreCase);

    private static string StripSpaces(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    private static string ProbeRate<T>(IEnumerable<T> items, Func<T, IReadOnlyDictionary<string, bool>> probes)
    {
        var all = items.SelectMany(i => probes(i).Values).ToList();
        return all.Count == 0 ? "—" : $"{all.Count(v => v)}/{all.Count}";
    }

    private static string ScriptFamily(string script)
        => script.StartsWith("Latin", StringComparison.Ordinal) ? "Latin" : script;

    // ---- reporting --------------------------------------------------------------------------------------------

    private static void AppendHeader(StringBuilder md, string title, EasyOcrService ocr)
    {
        md.AppendLine($"## Engine comparison — {title}");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz} · {RuntimeInformation.OSDescription} · {Environment.ProcessorCount} logical CPUs · " +
            $"provider {ocr.ActiveExecutionProvider} · EasyOCR = CRAFT + CRNN (default options) · PP-OCRv5 = DB mobile det + text-line cls + SVTR mobile rec " +
            $"(RecognitionOptions.Engine = PpOcrV5, otherwise default options) · warm, interleaved, median ms incl. decode.");
        md.AppendLine();
    }

    private static void AppendScoreHeader(StringBuilder md)
    {
        md.AppendLine("| input | CER EasyOCR | CER PP-OCRv5 | WER EasyOCR | WER PP-OCRv5 | charF1 EasyOCR | charF1 PP-OCRv5 | word recall EasyOCR | word recall PP-OCRv5 | ms EasyOCR | ms PP-OCRv5 |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
    }

    private static void AppendScoreRow(StringBuilder md, string name, Score easy, Score pp, EngineRun easyRun, EngineRun ppRun)
        => md.AppendLine(CultureInfo.InvariantCulture,
            $"| {name} | {Fmt(easy.Cer)} | {Fmt(pp.Cer)} | {Fmt(easy.Wer)} | {Fmt(pp.Wer)} | {Fmt(easy.CharF1)} | {Fmt(pp.CharF1)} | " +
            $"{Fmt(easy.WordRecall)} | {Fmt(pp.WordRecall)} | {easyRun.Milliseconds:0} | {ppRun.Milliseconds:0} |");

    private static void AppendFailures(StringBuilder md, IEnumerable<(string Name, EngineRun EasyOcr, EngineRun PpOcr)> runs)
    {
        var failures = runs
            .SelectMany(r => new[] { (r.Name, Engine: "EasyOCR", Run: r.EasyOcr), (r.Name, Engine: "PP-OCRv5", Run: r.PpOcr) })
            .Where(f => f.Run.Failed)
            .ToList();
        if (failures.Count == 0) return;

        md.AppendLine();
        md.AppendLine("Failures:");
        md.AppendLine();
        foreach (var f in failures) md.AppendLine($"- {f.Name} / {f.Engine}: {f.Run.Error}");
    }

    private static string Fmt(double? value)
        => value is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) : "n/a";

    /// <summary>A run's text squeezed into one Markdown table cell.</summary>
    private static string Cell(EngineRun? run)
    {
        if (run is null) return "n/a";
        if (run.Failed) return "error";
        var text = string.Join(" / ", run.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (text.Length > 80) text = text[..80] + "…";
        return text.Replace("|", "\\|", StringComparison.Ordinal);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return 0;
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Echoes the Markdown to the test output and writes it plus the raw per-input data as JSON to
    /// <c>EASYOCRSHARP_BENCHMARK_REPORT_DIR</c> (or the test output directory).
    /// </summary>
    private void Publish<T>(string section, StringBuilder md, T data)
    {
        _out.WriteLine(md.ToString());

        var dir = Environment.GetEnvironmentVariable("EASYOCRSHARP_BENCHMARK_REPORT_DIR");
        if (string.IsNullOrWhiteSpace(dir)) dir = AppContext.BaseDirectory;
        Directory.CreateDirectory(dir);
        var stem = Path.Combine(dir, $"engine-comparison-{section}");
        File.WriteAllText(stem + ".md", md.ToString());
        File.WriteAllText(stem + ".json", JsonSerializer.Serialize(data, ReportJson));
        _out.WriteLine($"Report written to {stem}.md / .json");
    }
}
