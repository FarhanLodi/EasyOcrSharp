using System.Diagnostics;
using System.Globalization;
using System.Text;
using EasyOcrSharp.Services;
using EasyOcrSharp.Structure;
using Xunit;
using Xunit.Abstractions;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Golden-output + warm-timing harness for structure-engine work (mirrors PaddleOcrNet's
/// <c>StructurePerfGoldenTests</c>). Runs <see cref="EasyOcrService.AnalyzeDocumentAsync(string, Models.DocumentAnalysisOptions?, CancellationToken)"/>
/// with the default options over a set of varied pages and writes, per page, the full structure JSON, the
/// Markdown and a compact block summary (type / bounds / reading order / text), plus warm timings (median
/// of <c>EASYOCRSHARP_GOLDEN_WARM_RUNS</c>, default 3) to <c>timings.txt</c>. A page that throws is recorded
/// (<c>&lt;page&gt;.error.txt</c> + a <c>timings.txt</c> line) and the run continues; the test fails at the
/// end. Diff two output directories to prove an engine change left the output identical. Only
/// long-standing public API is used, so the file compiles unchanged on older trees for a "before" capture. Gated: set <c>EASYOCRSHARP_GOLDEN_DIR</c> to the output directory to run it. Pages default to
/// a fixed set from <c>test/assets</c>; <c>EASYOCRSHARP_GOLDEN_EXTRA</c> adds more (semicolon-separated
/// absolute paths), <c>EASYOCRSHARP_GOLDEN_ASSETS</c> replaces the set.
/// </summary>
[Trait("Category", "Integration")]
[Collection(OcrIntegrationCollection.Name)]
public sealed class StructureGoldenTests
{
    private const string Gate = "EASYOCRSHARP_GOLDEN_DIR";

    private static readonly string[] DefaultAssets =
    {
        "structure_sample.png",
        "sample.png",
        "013_multi_column.png",
        "016_multi_column.png",
        "023_tables.png",
        "027_tables.png",
        "037_forms.png",
        "047_seals_stamps.png",
        "057_receipts_invoices.png",
        "075_rotated_perspective.png",
        "081_dense_mixed.png",
        "096_numbers_codes.png",
    };

    private readonly ITestOutputHelper _out;
    public StructureGoldenTests(ITestOutputHelper output) => _out = output;

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EasyOcrSharp.slnx")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        }
    }

    private static IReadOnlyList<string> SelectAssets()
    {
        static string[] Split(string value) => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var list = Environment.GetEnvironmentVariable("EASYOCRSHARP_GOLDEN_ASSETS") is { Length: > 0 } replace
            ? Split(replace).ToList()
            : DefaultAssets.Select(a => Path.Combine(RepoRoot, "test", "assets", a)).ToList();
        if (Environment.GetEnvironmentVariable("EASYOCRSHARP_GOLDEN_EXTRA") is { Length: > 0 } extra)
            list.AddRange(Split(extra));
        return list;
    }

    [SkippableFact]
    public async Task Structure_golden_dump_and_warm_timings()
    {
        var outDir = Environment.GetEnvironmentVariable(Gate);
        Skip.If(string.IsNullOrWhiteSpace(outDir), $"Golden harness skipped; set {Gate}=<output dir> to run.");
        Directory.CreateDirectory(outDir!);

        await using var ocr = new EasyOcrService();
        var report = new List<string>();

        var failures = new List<string>();
        int warmRuns = int.TryParse(Environment.GetEnvironmentVariable("EASYOCRSHARP_GOLDEN_WARM_RUNS"), out var w) && w > 0 ? w : 3;

        foreach (var path in SelectAssets())
        {
            Assert.True(File.Exists(path), $"Golden fixture is missing: {path}");
            string name = Path.GetFileName(path);

            try
            {
                // Cold run (model loads) produces the golden output; the warm runs are timed and re-checked.
                var first = await ocr.AnalyzeDocumentAsync(path);
                string json = first.ToJson();
                await File.WriteAllTextAsync(Path.Combine(outDir!, name + ".json"), json);
                await File.WriteAllTextAsync(Path.Combine(outDir!, name + ".md"), first.ToMarkdown());
                await File.WriteAllTextAsync(Path.Combine(outDir!, name + ".summary.txt"), Summarize(first));

                var times = new List<double>(warmRuns);
                bool stable = true;
                for (int i = 0; i < warmRuns; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var again = await ocr.AnalyzeDocumentAsync(path);
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    stable &= again.ToJson() == json;
                }

                times.Sort();
                string line = string.Create(CultureInfo.InvariantCulture,
                    $"{name}\tblocks={first.Blocks.Count}\tmedian_ms={times[times.Count / 2]:0}\truns={string.Join('/', times.Select(t => t.ToString("0", CultureInfo.InvariantCulture)))}\tstable={stable}");
                report.Add(line);
                _out.WriteLine(line);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep going: one page failing (e.g. memory pressure on a busy machine) must not cost the rest
                // of the report. The failure is recorded in the report and fails the test at the end.
                await File.WriteAllTextAsync(Path.Combine(outDir!, name + ".error.txt"), ex.ToString());
                string line = $"{name}\tERROR {ex.GetType().Name}: {ex.Message}";
                report.Add(line);
                failures.Add(line);
                _out.WriteLine(line);
            }
        }

        await File.WriteAllLinesAsync(Path.Combine(outDir!, "timings.txt"), report);
        _out.WriteLine($"Golden output written to {outDir}");
        Assert.True(failures.Count == 0, "Golden pages failed:\n" + string.Join('\n', failures));
    }

    /// <summary>One line per block: index, type, integer bounds, then the (single-line) text.</summary>
    internal static string Summarize(StructureResult result)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"size={result.SourceWidth}x{result.SourceHeight} blocks={result.Blocks.Count}\n");
        for (int i = 0; i < result.Blocks.Count; i++)
        {
            var b = result.Blocks[i];
            var box = b.Bounds;
            string text = (b.Text ?? string.Empty).Replace("\r", " ").Replace("\n", " ⏎ ");
            sb.Append(CultureInfo.InvariantCulture,
                $"{i:D3} o={b.Order,-3} {b.Type,-14} [{box.MinX:0},{box.MinY:0},{box.MaxX:0},{box.MaxY:0}] {text}\n");
        }
        return sb.ToString();
    }
}
