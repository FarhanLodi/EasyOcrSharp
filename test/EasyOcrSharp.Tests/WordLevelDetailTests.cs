using EasyOcrSharp.Export;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Unit tests (no models, no network) for word/character geometry: the CTC alignment decode, the
/// timestep → pixel mapping (including padded inputs and rotated patches), the split of characters into
/// words, and the exporters' use of — and fallback away from — real word boxes.
/// </summary>
public class WordLevelDetailTests
{
    // Recognizer vocabulary: class 0 is the CTC blank, class k ≥ 1 maps to Vocab[k-1].
    private const string Vocab = "ABC ";
    private const int Classes = 5;

    /// <summary>Softmax probability of the winning class when it holds <paramref name="strength"/> and every other class holds 0.</summary>
    private static double WinnerProbability(double strength)
        => Math.Exp(strength) / (Math.Exp(strength) + (Classes - 1));

    private static float[,] BuildLogits(params (int Class, float Strength)[] steps)
    {
        var logits = new float[steps.Length, Classes];
        for (int t = 0; t < steps.Length; t++)
        {
            logits[t, steps[t].Class] = steps[t].Strength;
        }
        return logits;
    }

    /// <summary>"AB C": A spans two timesteps (the second one weakly), B, a space and C one each.</summary>
    private static CtcDecodeResult DecodeAbSpaceC()
    {
        var logits = BuildLogits(
            (1, 20f),   // A
            (1, 1f),    // A again (weak) — same class, so the glyph's span widens
            (0, 20f),   // blank
            (2, 20f),   // B
            (0, 20f),   // blank
            (4, 20f),   // space
            (3, 20f));  // C
        return CtcDecoder.GreedyDecodeWithAlignment(logits, 7, Classes, Vocab, allowed: null);
    }

    private static OcrPoint[] Rectangle(double x, double y, double width, double height) =>
    [
        new OcrPoint(x, y), new OcrPoint(x + width, y),
        new OcrPoint(x + width, y + height), new OcrPoint(x, y + height),
    ];

    private static PatchAlignment Upright(CtcDecodeResult decoded, int cropWidth, int cropHeight, int contentWidth, int paddedWidth)
        => new(decoded.Alignment, decoded.Steps, contentWidth, paddedWidth, PatchTransform.Upright(cropWidth, cropHeight));

    // ---- alignment ----

    [Fact]
    public void GreedyAlignmentReportsOneEntryPerCharacterWithItsTimestepSpan()
    {
        var decoded = DecodeAbSpaceC();

        Assert.Equal("AB C", decoded.Text);
        Assert.Equal(7, decoded.Steps);
        Assert.Equal(4, decoded.Alignment.Count);

        Assert.Equal(('A', 0, 1), (decoded.Alignment[0].Value, decoded.Alignment[0].StartStep, decoded.Alignment[0].EndStep));
        Assert.Equal(('B', 3, 3), (decoded.Alignment[1].Value, decoded.Alignment[1].StartStep, decoded.Alignment[1].EndStep));
        Assert.Equal((' ', 5, 5), (decoded.Alignment[2].Value, decoded.Alignment[2].StartStep, decoded.Alignment[2].EndStep));
        Assert.Equal(('C', 6, 6), (decoded.Alignment[3].Value, decoded.Alignment[3].StartStep, decoded.Alignment[3].EndStep));
    }

    [Fact]
    public void RepeatedTimestepsAverageIntoTheCharacterConfidence()
    {
        var decoded = DecodeAbSpaceC();

        // 'A' was emitted over two timesteps: a near-certain one and a weak one.
        double expected = (WinnerProbability(20) + WinnerProbability(1)) / 2.0;
        Assert.Equal(expected, decoded.Alignment[0].Confidence, 9);

        // Single-timestep glyphs keep that timestep's probability verbatim.
        Assert.Equal(WinnerProbability(20), decoded.Alignment[1].Confidence, 9);
    }

    [Fact]
    public void AlignmentDoesNotChangeTextOrConfidence()
    {
        var logits = BuildLogits((1, 20f), (1, 1f), (0, 20f), (2, 20f), (0, 20f), (4, 20f), (3, 20f));

        var plain = CtcDecoder.GreedyDecode(logits, 7, Classes, Vocab, allowed: null);
        var aligned = CtcDecoder.GreedyDecodeWithAlignment(logits, 7, Classes, Vocab, allowed: null);

        Assert.Equal(plain.Text, aligned.Text);
        Assert.Equal(plain.Confidence, aligned.Confidence);
    }

    [Fact]
    public void AllowlistMaskingIsReflectedInTheAlignment()
    {
        var logits = BuildLogits((1, 20f), (0, 20f), (2, 20f));
        var allowed = CtcDecoder.BuildAllowedMask(Vocab, allowlist: "A", blocklist: null);

        var decoded = CtcDecoder.GreedyDecodeWithAlignment(logits, 3, Classes, Vocab, allowed);

        Assert.Equal("A", decoded.Text);
        Assert.Single(decoded.Alignment);
        Assert.Equal('A', decoded.Alignment[0].Value);
    }

    // ---- timestep → pixel geometry ----

    [Fact]
    public void CharacterSpansAreOrderedAndDoNotOverlap()
    {
        var decoded = DecodeAbSpaceC();
        var polygon = Rectangle(0, 0, 400, 50);

        var (words, characters) = WordGeometry.Build(
            polygon, Upright(decoded, 400, 50, contentWidth: 512, paddedWidth: 512), WordLevelDetail.Characters);

        Assert.Equal(new[] { "A", "B", " ", "C" }, characters.Select(c => c.Value));
        for (int i = 1; i < characters.Count; i++)
        {
            Assert.True(characters[i].BoundingBox.MinX > characters[i - 1].BoundingBox.MinX,
                "character spans must advance along the text direction");
            Assert.True(characters[i].BoundingBox.MinX >= characters[i - 1].BoundingBox.MaxX - 1e-9,
                "character spans must not overlap");
        }

        // 7 timesteps over a 400px-wide box: 'A' owns steps 0-1, so it covers the first 2/7.
        Assert.Equal(0.0, characters[0].BoundingBox.MinX, 6);
        Assert.Equal(400.0 * 2 / 7, characters[0].BoundingBox.MaxX, 6);
        Assert.Equal(2, words.Count);
    }

    [Fact]
    public void WordsSplitOnWhitespaceAndAverageTheirCharacterConfidences()
    {
        var decoded = DecodeAbSpaceC();

        var (words, characters) = WordGeometry.Build(
            Rectangle(0, 0, 400, 50), Upright(decoded, 400, 50, 512, 512), WordLevelDetail.Characters);

        Assert.Equal(new[] { "AB", "C" }, words.Select(w => w.Text));

        double expectedAb = (decoded.Alignment[0].Confidence + decoded.Alignment[1].Confidence) / 2.0;
        Assert.Equal(expectedAb, words[0].Confidence, 9);
        Assert.Equal(decoded.Alignment[3].Confidence, words[1].Confidence, 9);

        // The word hull spans its first character's start to its last character's end, and no further.
        Assert.Equal(characters[0].BoundingBox.MinX, words[0].BoundingBox.MinX, 6);
        Assert.Equal(characters[1].BoundingBox.MaxX, words[0].BoundingBox.MaxX, 6);
    }

    [Fact]
    public void WordQuadsStayInsideTheParentLinePolygon()
    {
        var decoded = DecodeAbSpaceC();
        var polygon = Rectangle(120, 40, 400, 50);

        var (words, characters) = WordGeometry.Build(
            polygon, Upright(decoded, 400, 50, 512, 512), WordLevelDetail.Characters);

        var line = OcrBoundingBox.FromPoints(polygon);
        foreach (var point in words.SelectMany(w => w.BoundingPolygon).Concat(characters.SelectMany(c => c.BoundingPolygon)))
        {
            Assert.InRange(point.X, line.MinX - 1e-9, line.MaxX + 1e-9);
            Assert.InRange(point.Y, line.MinY - 1e-9, line.MaxY + 1e-9);
        }
    }

    [Fact]
    public void PaddingTimestepsDoNotStretchTheMapping()
    {
        var decoded = DecodeAbSpaceC();

        var unpadded = WordGeometry.Build(
            Rectangle(0, 0, 400, 50), Upright(decoded, 400, 50, contentWidth: 256, paddedWidth: 256), WordLevelDetail.Characters);
        // Same content, fed at 320px because the batch's widest member forced extra right padding.
        var padded = WordGeometry.Build(
            Rectangle(0, 0, 400, 50), Upright(decoded, 400, 50, contentWidth: 256, paddedWidth: 320), WordLevelDetail.Characters);

        double plainCharWidth = unpadded.Characters[0].BoundingBox.Width;
        double paddedCharWidth = padded.Characters[0].BoundingBox.Width;

        // The padding is 25% of the content width, so each timestep covers 25% more of the content.
        Assert.Equal(plainCharWidth * 320.0 / 256.0, paddedCharWidth, 6);
        Assert.True(paddedCharWidth > plainCharWidth);
    }

    [Fact]
    public void SpansNeverRunPastTheContentWidth()
    {
        var decoded = DecodeAbSpaceC();

        var (words, characters) = WordGeometry.Build(
            Rectangle(0, 0, 400, 50),
            Upright(decoded, 400, 50, contentWidth: 128, paddedWidth: 512), // extreme padding
            WordLevelDetail.Characters);

        Assert.All(characters, c => Assert.InRange(c.BoundingBox.MaxX, 0.0, 400.0));
        Assert.All(words, w => Assert.InRange(w.BoundingBox.MaxX, 0.0, 400.0));
    }

    [Fact]
    public void RotatedDetectionBoxProducesRotatedCharacterQuads()
    {
        var decoded = DecodeAbSpaceC();

        // A 400×50 box turned 30° clockwise: corners follow the text direction, not the axes.
        const double angle = Math.PI / 6;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        var polygon = new[]
        {
            new OcrPoint(0, 0),
            new OcrPoint(400 * cos, 400 * sin),
            new OcrPoint((400 * cos) - (50 * sin), (400 * sin) + (50 * cos)),
            new OcrPoint(-50 * sin, 50 * cos),
        };

        var (words, _) = WordGeometry.Build(
            polygon, Upright(decoded, 400, 50, 512, 512), WordLevelDetail.Words);

        var quad = words[0].BoundingPolygon;
        Assert.Equal(4, quad.Count);

        // Top edge follows the rotated baseline: dy/dx ≈ tan(30°), definitely not axis-aligned.
        double dx = quad[1].X - quad[0].X;
        double dy = quad[1].Y - quad[0].Y;
        Assert.True(dx > 0);
        Assert.Equal(Math.Tan(angle), dy / dx, 6);

        // The left edge leans the same way instead of dropping straight down.
        Assert.NotEqual(quad[0].X, quad[3].X, 3);
    }

    [Fact]
    public void RotatedPatchIsMappedBackOntoTheUprightCrop()
    {
        // A 100×20 crop rotated 90° clockwise becomes a 20×100 patch: the patch's text direction runs
        // down the crop, and its top-left corner is the crop's bottom-left.
        var transform = PatchTransform.Rotated(cropWidth: 100, cropHeight: 20, patchWidth: 20, patchHeight: 100, angleDegrees: 90);

        var start = transform.ToCrop(0, 0);
        var end = transform.ToCrop(1, 1);

        Assert.Equal(0.0, start.U, 6);
        Assert.Equal(1.0, start.V, 6);
        Assert.Equal(1.0, end.U, 6);
        Assert.Equal(0.0, end.V, 6);
    }

    [Fact]
    public void UprightPatchIsTheIdentity()
    {
        var transform = PatchTransform.Upright(100, 20);
        var mapped = transform.ToCrop(0.25, 0.75);

        Assert.Equal(0.25, mapped.U, 9);
        Assert.Equal(0.75, mapped.V, 9);
    }

    // ---- opt-in ----

    [Fact]
    public void WordLevelDetailNoneProducesNoGeometry()
    {
        var decoded = DecodeAbSpaceC();

        var (words, characters) = WordGeometry.Build(
            Rectangle(0, 0, 400, 50), Upright(decoded, 400, 50, 512, 512), WordLevelDetail.None);

        Assert.Empty(words);
        Assert.Empty(characters);
    }

    [Fact]
    public void WordLevelDetailWordsSkipsCharacters()
    {
        var decoded = DecodeAbSpaceC();

        var (words, characters) = WordGeometry.Build(
            Rectangle(0, 0, 400, 50), Upright(decoded, 400, 50, 512, 512), WordLevelDetail.Words);

        Assert.Equal(2, words.Count);
        Assert.Empty(characters);
    }

    [Fact]
    public void DefaultOptionsLeaveTheFeatureOff()
    {
        Assert.Equal(WordLevelDetail.None, RecognitionOptions.Default.WordLevelDetail);
        Assert.Equal(WordLevelDetail.None, CrnnRunOptions.FromRecognition(RecognitionOptions.Default).WordLevelDetail);
        Assert.Equal(WordLevelDetail.None, CrnnRunOptions.Defaults.WordLevelDetail);

        var line = new OcrLine { Text = "AB C" };
        Assert.Empty(line.Words);
        Assert.Empty(line.Characters);
    }

    [Fact]
    public void RecognitionOptionsFlowsThroughToTheRecognizer()
    {
        var run = CrnnRunOptions.FromRecognition(new RecognitionOptions { WordLevelDetail = WordLevelDetail.Characters });
        Assert.Equal(WordLevelDetail.Characters, run.WordLevelDetail);
    }

    // ---- exporters ----

    private static OcrResult ResultWith(OcrLine line) => new()
    {
        FullText = line.Text,
        Lines = new[] { line },
        Languages = new[] { "en" },
    };

    private static OcrLine PlainLine() => new()
    {
        Text = "AB CD",
        Confidence = 0.5,
        BoundingPolygon = Rectangle(0, 0, 100, 20),
        BoundingBox = new OcrBoundingBox(0, 0, 100, 20),
    };

    private static OcrLine LineWithWords()
    {
        var line = PlainLine();
        return line with
        {
            Words = new[]
            {
                new OcrWord { Text = "AB", Confidence = 0.9, BoundingBox = new OcrBoundingBox(5, 1, 45, 19) },
                new OcrWord { Text = "CD", Confidence = 0.8, BoundingBox = new OcrBoundingBox(55, 1, 95, 19) },
            },
        };
    }

    [Fact]
    public void ExportersFallBackToTheProportionalSplitWhenThereAreNoWords()
    {
        var result = ResultWith(PlainLine());

        var hocr = result.ToHocr(100, 20);
        Assert.Contains("<span class='ocrx_word' id='word_1_1' title='bbox 0 0 50 20; x_wconf 50'>AB</span>", hocr, StringComparison.Ordinal);
        Assert.Contains("<span class='ocrx_word' id='word_1_2' title='bbox 50 0 100 20; x_wconf 50'>CD</span>", hocr, StringComparison.Ordinal);
        Assert.DoesNotContain("x_bboxes", hocr, StringComparison.Ordinal);

        var alto = result.ToAlto(100, 20);
        Assert.Contains("<String ID=\"string_1_1\" HPOS=\"0\" VPOS=\"0\" WIDTH=\"50\" HEIGHT=\"20\" WC=\"0.5\" CONTENT=\"AB\"/>", alto, StringComparison.Ordinal);
        Assert.DoesNotContain("<Glyph", alto, StringComparison.Ordinal);

        var tsv = result.ToTsv();
        Assert.Contains("5\t1\t1\t1\t1\t1\t0\t0\t50\t20\t50\tAB\n", tsv, StringComparison.Ordinal);
        Assert.Contains("5\t1\t1\t1\t1\t2\t50\t0\t50\t20\t50\tCD\n", tsv, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportersUseRealWordGeometryAndConfidenceWhenAvailable()
    {
        var result = ResultWith(LineWithWords());

        var hocr = result.ToHocr(100, 20);
        Assert.Contains("<span class='ocrx_word' id='word_1_1' title='bbox 5 1 45 19; x_wconf 90'>AB</span>", hocr, StringComparison.Ordinal);
        Assert.Contains("<span class='ocrx_word' id='word_1_2' title='bbox 55 1 95 19; x_wconf 80'>CD</span>", hocr, StringComparison.Ordinal);

        var alto = result.ToAlto(100, 20);
        Assert.Contains("<String ID=\"string_1_1\" HPOS=\"5\" VPOS=\"1\" WIDTH=\"40\" HEIGHT=\"18\" WC=\"0.9\" CONTENT=\"AB\"/>", alto, StringComparison.Ordinal);

        var tsv = result.ToTsv();
        Assert.Contains("5\t1\t1\t1\t1\t1\t5\t1\t40\t18\t90\tAB\n", tsv, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportersEmitCharacterDetailOnlyWhenCharactersArePresent()
    {
        var line = LineWithWords();
        OcrChar Glyph(string value, double x0, double x1) => new()
        {
            Value = value,
            Confidence = 0.9,
            BoundingBox = new OcrBoundingBox(x0, 1, x1, 19),
        };
        var withChars = line with
        {
            Characters = new[]
            {
                Glyph("A", 5, 25), Glyph("B", 25, 45), Glyph(" ", 45, 55), Glyph("C", 55, 75), Glyph("D", 75, 95),
            },
        };

        var hocr = ResultWith(withChars).ToHocr(100, 20);
        Assert.Contains("x_bboxes 5 1 25 19 25 1 45 19; x_confs 90 90", hocr, StringComparison.Ordinal);

        var alto = ResultWith(withChars).ToAlto(100, 20);
        Assert.Contains("<Glyph ID=\"glyph_1_1_1\" HPOS=\"5\" VPOS=\"1\" WIDTH=\"20\" HEIGHT=\"18\" GC=\"0.9\" CONTENT=\"A\"/>", alto, StringComparison.Ordinal);
        Assert.Contains("</String>", alto, StringComparison.Ordinal);
    }

    [Fact]
    public void CharacterDetailIsDroppedWhenItDoesNotMatchTheWords()
    {
        // Characters that spell something else must never be attached to the words.
        var line = LineWithWords() with
        {
            Characters = new[]
            {
                new OcrChar { Value = "X", Confidence = 0.9, BoundingBox = new OcrBoundingBox(5, 1, 25, 19) },
            },
        };

        var hocr = ResultWith(line).ToHocr(100, 20);
        Assert.DoesNotContain("x_bboxes", hocr, StringComparison.Ordinal);
    }
}
