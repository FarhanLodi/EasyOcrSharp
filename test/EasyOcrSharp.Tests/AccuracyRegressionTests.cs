using EasyOcrSharp.Services;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>CI-safe unit tests for the CER/WER metrics that back the accuracy regression harness.</summary>
public class TextMetricsTests
{
    [Theory]
    [InlineData("hello", "hello", 0.0)]
    [InlineData("hello", "hallo", 0.2)]   // 1 substitution / 5 chars
    [InlineData("hello", "hell", 0.2)]    // 1 deletion / 5 chars
    [InlineData("hello", "", 1.0)]
    [InlineData("", "", 0.0)]
    public void CharacterErrorRate_Matches(string reference, string hypothesis, double expected)
        => Assert.Equal(expected, TextMetrics.CharacterErrorRate(reference, hypothesis), 6);

    [Theory]
    [InlineData("the cat sat", "the cat sat", 0.0)]
    [InlineData("the cat sat", "the dog sat", 1.0 / 3.0)]
    [InlineData("a b c d", "a b c", 0.25)]
    public void WordErrorRate_Matches(string reference, string hypothesis, double expected)
        => Assert.Equal(expected, TextMetrics.WordErrorRate(reference, hypothesis), 6);
}

/// <summary>
/// OCR accuracy regression gate (Integration: needs the ONNX models). For every <c>assets/&lt;name&gt;.gt.txt</c>
/// ground-truth file shipped next to a matching <c>assets/&lt;name&gt;</c> image, OCRs the image and asserts the
/// character error rate stays below <see cref="MaxCer"/>. This catches silent accuracy regressions that a
/// substring "Contains" assertion misses (e.g. a confidence collapse that still emits the keyword). Add
/// fixtures + ground-truth files to grow the gate; it skips cleanly when none are present.
/// </summary>
[Trait("Category", "Integration")]
public class AccuracyRegressionTests
{
    private const double MaxCer = 0.15;

    private static List<(string Image, string GroundTruth)> GroundTruthFixtures()
    {
        var found = new List<(string, string)>();
        var dir = Path.Combine(AppContext.BaseDirectory, "assets");
        if (!Directory.Exists(dir)) return found;

        foreach (var gt in Directory.EnumerateFiles(dir, "*.gt.txt"))
        {
            var stem = Path.GetFileName(gt)[..^".gt.txt".Length]; // sample.gt.txt -> sample
            var image = Directory.EnumerateFiles(dir, stem + ".*")
                .FirstOrDefault(f => !f.EndsWith(".gt.txt", StringComparison.OrdinalIgnoreCase));
            if (image is not null) found.Add((image, gt));
        }
        return found;
    }

    [SkippableFact]
    public async Task OcrStaysBelowMaxCerForEveryFixture()
    {
        var fixtures = GroundTruthFixtures();
        Skip.If(fixtures.Count == 0, "No <name>.gt.txt ground-truth fixtures present in assets/.");

        await using var ocr = new EasyOcrService();
        foreach (var (image, groundTruth) in fixtures)
        {
            var reference = (await File.ReadAllTextAsync(groundTruth)).Trim();
            var result = await ocr.ExtractTextFromImage(image, new[] { "en" });
            double cer = TextMetrics.CharacterErrorRate(reference, result.FullText.Trim());

            Assert.True(cer <= MaxCer,
                $"CER {cer:P1} for '{Path.GetFileName(image)}' exceeded {MaxCer:P0}. OCR output: <{result.FullText}>");
        }
    }
}
