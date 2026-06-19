using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Services;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Unit tests (no models, CI-safe) for the June-2026 review fixes that are pure functions: column- and
/// font-aware reading order, IoU NMS box dedup, the greedy-confidence formula pin, region→image
/// coordinate translation, the source-dimension result fields, and PDF DoS-guard option math.
/// </summary>
public class ReviewReadingOrderTests
{
    private static OcrLine Line(string text, double x, double y, double w, double h)
    {
        var box = new OcrBoundingBox(x, y, x + w, y + h);
        var poly = new[] { new OcrPoint(x, y), new OcrPoint(x + w, y), new OcrPoint(x + w, y + h), new OcrPoint(x, y + h) };
        return new OcrLine { Text = text, Confidence = 1.0, BoundingBox = box, BoundingPolygon = poly };
    }

    [Fact]
    public void ReadsTwoColumnsTopToBottomThenLeftToRight()
    {
        // Column A at x∈[0,200], column B at x∈[400,600] separated by a clear 200px gutter.
        var lines = new[]
        {
            Line("B2", 400, 100, 200, 30),
            Line("A1", 0, 0, 200, 30),
            Line("B1", 400, 0, 200, 30),
            Line("A2", 0, 100, 200, 30),
        };

        var ordered = EasyOcrService.SortLinesByReadingOrder(lines);

        Assert.Equal(new[] { "A1", "A2", "B1", "B2" }, ordered.ConvertAll(l => l.Text));
    }

    [Fact]
    public void AdaptiveToleranceKeepsLargeFontWordsOnTheSameLine()
    {
        // Two words on one large (h=80) visual line with a 12px vertical jitter and reversed x order.
        // A fixed 10px band would split them and emit "World Hello"; the height-relative band keeps the
        // line together and emits left-to-right "Hello World".
        var lines = new[]
        {
            Line("World", 200, 0, 150, 80),
            Line("Hello", 0, 12, 150, 80),
        };

        var ordered = EasyOcrService.SortLinesByReadingOrder(lines);

        Assert.Equal(new[] { "Hello", "World" }, ordered.ConvertAll(l => l.Text));
    }

    [Fact]
    public void SingleLineIsReturnedUnchanged()
    {
        var lines = new[] { Line("only", 5, 5, 10, 10) };
        Assert.Single(EasyOcrService.SortLinesByReadingOrder(lines));
    }
}

public class ReviewNmsTests
{
    private static OcrPoint[] Rect(double x0, double y0, double x1, double y1)
        => new[] { new OcrPoint(x0, y0), new OcrPoint(x1, y0), new OcrPoint(x1, y1), new OcrPoint(x0, y1) };

    [Fact]
    public void DropsHeavilyOverlappingDuplicate()
    {
        var polys = new[] { Rect(0, 0, 100, 100), Rect(5, 5, 105, 105) }; // IoU ≈ 0.82
        var reduced = BoxNms.Reduce(polys, 0.6);
        Assert.Single(reduced);
    }

    [Fact]
    public void KeepsDistinctNonOverlappingBoxes()
    {
        var polys = new[] { Rect(0, 0, 100, 100), Rect(200, 0, 300, 100) }; // IoU 0
        var reduced = BoxNms.Reduce(polys, 0.6);
        Assert.Equal(2, reduced.Count);
    }

    [Fact]
    public void ThresholdZeroDisablesSuppression()
    {
        var polys = new[] { Rect(0, 0, 100, 100), Rect(1, 1, 101, 101) };
        var reduced = BoxNms.Reduce(polys, 0.0);
        Assert.Equal(2, reduced.Count);
        Assert.Same(polys, reduced); // unchanged reference when disabled
    }
}

public class ReviewConfidenceFormulaTests
{
    private static float[,] Logits(int classes, params int[] argmaxPerStep)
    {
        var l = new float[argmaxPerStep.Length, classes];
        for (int t = 0; t < argmaxPerStep.Length; t++)
            for (int c = 0; c < classes; c++)
                l[t, c] = c == argmaxPerStep[t] ? 8f : 0f;
        return l;
    }

    [Fact]
    public void GreedyConfidenceCountsEveryNonBlankTimestep_MatchingEasyOcr()
    {
        // Sequence a,a,blank,b -> "ab" with three NON-BLANK timesteps (the repeated 'a' counts twice).
        // EasyOCR computes custom_mean = (∏ p)^(2/√n) over max-probs at timesteps where index != blank
        // (recognition.py: `max_probs = v[i!=0]`), i.e. n = number of non-blank timesteps = 3, NOT the
        // 2 emitted/collapsed characters. This test pins that behaviour so it can't silently regress.
        var (text, conf) = CtcDecoder.GreedyDecode(Logits(4, 1, 1, 0, 2), 4, 4, "abc", allowed: null);

        Assert.Equal("ab", text);

        double p = Math.Exp(8) / (Math.Exp(8) + 3); // softmax of the argmax class among 4 classes
        double expectedN3 = Math.Pow(Math.Pow(p, 3), 2.0 / Math.Sqrt(3)); // n = 3 non-blank timesteps
        double wrongN2 = Math.Pow(Math.Pow(p, 2), 2.0 / Math.Sqrt(2));    // n = 2 (emitted chars) — must NOT match
        Assert.Equal(expectedN3, conf, 6);
        Assert.NotEqual(wrongN2, conf, 6);
    }
}

public class ReviewCoordinateTranslationTests
{
    [Fact]
    public void TranslateLinesOffsetsBoxAndPolygon()
    {
        var line = new OcrLine
        {
            Text = "x",
            BoundingBox = new OcrBoundingBox(10, 20, 30, 40),
            BoundingPolygon = new[] { new OcrPoint(10, 20), new OcrPoint(30, 40) },
        };

        var t = EasyOcrService.TranslateLines(new[] { line }, 5, 7)[0];

        Assert.Equal(15, t.BoundingBox.MinX);
        Assert.Equal(27, t.BoundingBox.MinY);
        Assert.Equal(35, t.BoundingBox.MaxX);
        Assert.Equal(47, t.BoundingBox.MaxY);
        Assert.Equal(15, t.BoundingPolygon[0].X);
        Assert.Equal(27, t.BoundingPolygon[0].Y);
    }

    [Fact]
    public void TranslateRegionsZeroOffsetReturnsSameReference()
    {
        var regions = new[]
        {
            new DetectedRegion
            {
                BoundingPolygon = new[] { new OcrPoint(1, 2) },
                BoundingBox = new OcrBoundingBox(1, 2, 3, 4),
            },
        };
        Assert.Same(regions, EasyOcrService.TranslateRegions(regions, 0, 0));
    }
}

public class ReviewResultAndOptionTests
{
    [Fact]
    public void OcrResultCarriesSourceDimensions()
    {
        var r = new OcrResult
        {
            FullText = string.Empty,
            Lines = Array.Empty<OcrLine>(),
            Languages = Array.Empty<string>(),
            SourceWidth = 640,
            SourceHeight = 480,
        };
        Assert.Equal(640, r.SourceWidth);
        Assert.Equal(480, r.SourceHeight);
        Assert.Equal(0, OcrResult.Empty.SourceWidth);
        Assert.Equal(0, OcrResult.Empty.SourceHeight);
    }

    [Theory]
    [InlineData(200, 200_000_000L)]
    [InlineData(0, 0L)]
    public void PdfMaxPagePixelsDerivedFromMegapixels(int megapixels, long expected)
        => Assert.Equal(expected, new PdfOcrOptions { MaxPageMegapixels = megapixels }.MaxPagePixels);

    [Fact]
    public void PdfOptionsRejectNegativeGuards()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfOcrOptions { MaxPages = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfOcrOptions { MaxPageMegapixels = -1 }.Validate());
    }
}
