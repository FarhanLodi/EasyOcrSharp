using System.Diagnostics;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyOcrSharp.Internal;
using Xunit;
using Xunit.Abstractions;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Interleaved A/B micro-benchmark of each model-input packer against the implementation it replaced (the
/// <c>…Reference</c> methods; bit-for-bit parity is <see cref="TensorPackingParityTests"/>'s job). A fused
/// packer is only worth keeping if it is not slower, so each scenario fails when the fused median exceeds the
/// reference median by more than the tolerance — and names the packer to roll back.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in: skipped unless <c>EASYOCRSHARP_RUN_BENCHMARKS=1</c>, because wall-clock assertions do not belong in
/// the default unit run. <c>EASYOCRSHARP_BENCH_TOLERANCE</c> overrides the allowed ratio (default 1.10).
/// Run with <c>dotnet test -c Release --filter Category=Benchmark</c>.
/// </para>
/// <para>
/// The machine may be shared with other CPU-heavy work, so the two implementations alternate inside every
/// round (A B, then B A on the next round) and the comparison uses medians over many rounds; allocation per
/// call is reported alongside, since the fused paths exist to avoid the intermediate resized image.
/// </para>
/// </remarks>
[Trait("Category", "Benchmark")]
public sealed class TensorPackingBenchmarkTests
{
    private const string EnableVariable = "EASYOCRSHARP_RUN_BENCHMARKS";
    private const string ToleranceVariable = "EASYOCRSHARP_BENCH_TOLERANCE";
    private const int Rounds = 25;
    private static readonly TimeSpan TargetSample = TimeSpan.FromMilliseconds(40);

    private readonly ITestOutputHelper _out;

    public TensorPackingBenchmarkTests(ITestOutputHelper output) => _out = output;

    [SkippableTheory]
    [InlineData("craft-identity")]     // 1700x2200 page, default canvas: no resize, the common case
    [InlineData("craft-downscale")]    // 3024x4032 phone photo resized to the 2560 canvas
    [InlineData("craft-upscale")]      // 800x600 at MagRatio 1.5
    [InlineData("crnn-batch")]         // 40 line crops of mixed size, the batch recognizer path
    [InlineData("trocr")]              // handwriting crops stretched to 384x384
    [InlineData("orientation")]        // page -> 224x224 doc-orientation input
    [InlineData("unwarp-capped")]      // 1700x2200 page: memory cap applies, resize branch
    [InlineData("unwarp-full")]        // 1200x1600 page: full-size write branch
    public void Fused_packer_is_not_slower_than_the_reference(string scenario)
    {
        Skip.IfNot(Environment.GetEnvironmentVariable(EnableVariable) == "1",
            $"Benchmarks are opt-in: set {EnableVariable}=1 (preferably with -c Release).");

        double tolerance = double.TryParse(Environment.GetEnvironmentVariable(ToleranceVariable),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) && t > 0 ? t : 1.10;

        var (reference, fused, dispose) = Build(scenario);
        try
        {
            // Warm-up (JIT, pools), then size each sample to roughly TargetSample.
            for (int i = 0; i < 3; i++) { reference(); fused(); }
            int reps = Math.Max(1, (int)(TargetSample.TotalMilliseconds / Math.Max(0.01, TimeOnce(reference).TotalMilliseconds)));

            var referenceTimes = new double[Rounds];
            var fusedTimes = new double[Rounds];
            for (int round = 0; round < Rounds; round++)
            {
                if ((round & 1) == 0)
                {
                    referenceTimes[round] = Sample(reference, reps);
                    fusedTimes[round] = Sample(fused, reps);
                }
                else
                {
                    fusedTimes[round] = Sample(fused, reps);
                    referenceTimes[round] = Sample(reference, reps);
                }
            }

            double referenceMedian = Median(referenceTimes), fusedMedian = Median(fusedTimes);
            long referenceBytes = AllocatedPerCall(reference), fusedBytes = AllocatedPerCall(fused);
            double ratio = fusedMedian / referenceMedian;
            _out.WriteLine(
                $"{scenario}: reference {referenceMedian:F3} ms (min {referenceTimes.Min():F3}), fused {fusedMedian:F3} ms " +
                $"(min {fusedTimes.Min():F3}), ratio {ratio:F3}; allocated/call reference {referenceBytes / 1024.0:F0} KiB, " +
                $"fused {fusedBytes / 1024.0:F0} KiB; {Rounds} interleaved rounds x {reps} calls; " +
                $"optimized={IsOptimized()}");

            Assert.True(ratio <= tolerance,
                $"{scenario}: the fused packer's median {fusedMedian:F3} ms is {ratio:F2}x the reference's {referenceMedian:F3} ms " +
                $"(tolerance {tolerance:F2}x). Roll this packer back to its …Reference implementation.");
        }
        finally
        {
            dispose();
        }
    }

    private static (Action Reference, Action Fused, Action Dispose) Build(string scenario)
    {
        switch (scenario)
        {
            case "craft-identity": return Craft(1700, 2200, 2560, 1.0);
            case "craft-downscale": return Craft(3024, 4032, 2560, 1.0);
            case "craft-upscale": return Craft(800, 600, 2560, 1.5);
            case "crnn-batch":
            {
                var rng = new Random(11);
                var crops = Enumerable.Range(0, 40).Select(i =>
                {
                    int h = rng.Next(18, 70);
                    int w = i % 10 == 0 ? rng.Next(4, 20) : rng.Next(60, 1400);
                    return TensorPackingParityTests.MakeImage(w, h, seed: i);
                }).ToArray();
                return (
                    () => { foreach (var c in crops) ImageProcessing.PreprocessForCrnn(c, 64, 4096, false, out _); },
                    () => { foreach (var c in crops) ImageProcessing.PreprocessForCrnnFused(c, 64, 4096, false, out _); },
                    () => { foreach (var c in crops) c.Dispose(); });
            }
            case "trocr":
            {
                var crops = new[] { (600, 80), (900, 120), (300, 60), (1400, 90), (450, 70) }
                    .Select((s, i) => TensorPackingParityTests.MakeImage(s.Item1, s.Item2, seed: i)).ToArray();
                return (
                    () => { foreach (var c in crops) TrOcrRecognizer.PreprocessReference(c, 384, 0.5f, 0.5f); },
                    () => { foreach (var c in crops) TrOcrRecognizer.Preprocess(c, 384, 0.5f, 0.5f); },
                    () => { foreach (var c in crops) c.Dispose(); });
            }
            case "orientation":
            {
                var page = TensorPackingParityTests.MakeImage(1700, 2200, seed: 3);
                return (
                    () => DocPreprocessor.BuildOrientationTensorReference(page),
                    () => DocPreprocessor.BuildOrientationTensor(page),
                    page.Dispose);
            }
            case "unwarp-capped": return Unwarp(1700, 2200);
            case "unwarp-full": return Unwarp(1200, 1600);
            default: throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        static (Action, Action, Action) Craft(int w, int h, int canvas, double mag)
        {
            var page = TensorPackingParityTests.MakeImage(w, h, seed: w + h);
            return (
                () => ImageProcessing.PreprocessForCraft(page, canvas, mag),
                () => ImageProcessing.PreprocessForCraftFused(page, canvas, mag),
                page.Dispose);
        }

        static (Action, Action, Action) Unwarp(int w, int h)
        {
            var page = TensorPackingParityTests.MakeImage(w, h, seed: w * h);
            var g = DocPreprocessor.ComputeUnwarpGeometry(w, h);
            return (
                () => DocPreprocessor.BuildUnwarpTensorReference(page, g),
                () => DocPreprocessor.BuildUnwarpTensor(page, g),
                page.Dispose);
        }
    }

    private static TimeSpan TimeOnce(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        return sw.Elapsed;
    }

    /// <summary>Mean milliseconds per call over <paramref name="reps"/> consecutive calls.</summary>
    private static double Sample(Action action, int reps)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < reps; i++) action();
        return sw.Elapsed.TotalMilliseconds / reps;
    }

    private static long AllocatedPerCall(Action action)
    {
        // All threads: the reference resize runs on the thread pool.
        long before = GC.GetTotalAllocatedBytes(precise: true);
        action();
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    private static double Median(double[] values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>Whether the library under test was compiled with optimizations (a Debug run understates both sides).</summary>
    private static bool IsOptimized()
        => typeof(ImageProcessing).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            .OfType<DebuggableAttribute>().All(a => !a.IsJITOptimizerDisabled);
}
