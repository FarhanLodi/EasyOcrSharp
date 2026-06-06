using EasyOcrSharp;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Unit tests for the EasyOCR-parity features added on top of the base pipeline: beam-search
/// decoding, exposed grouping/contrast tunables, word-beam dictionary constraint, custom recognizers,
/// and the recognize-from-boxes API. None require downloaded models.
/// </summary>
public class DecoderTests
{
    // logits[t, c]; class 0 is the CTC blank, class k≥1 maps to characters[k-1].
    private static float[,] Logits(int classes, params int[] argmaxPerStep)
    {
        var l = new float[argmaxPerStep.Length, classes];
        for (int t = 0; t < argmaxPerStep.Length; t++)
            for (int c = 0; c < classes; c++)
                l[t, c] = c == argmaxPerStep[t] ? 8f : 0f;
        return l;
    }

    [Fact]
    public void Greedy_CollapsesRepeats_AndDropsBlank()
    {
        // a, a, blank, b  ->  "ab"
        var logits = Logits(4, 1, 1, 0, 2);
        var (text, conf) = CtcDecoder.GreedyDecode(logits, 4, 4, "abc", allowed: null);

        Assert.Equal("ab", text);
        Assert.True(conf > 0.9, $"confidence was {conf}");
    }

    [Fact]
    public void BeamSearch_MatchesGreedy_OnConfidentSequence()
    {
        var logits = Logits(4, 1, 1, 0, 2);
        var (text, conf) = CtcDecoder.BeamSearchDecode(logits, 4, 4, "abc", allowed: null, beamWidth: 5, trie: null);

        Assert.Equal("ab", text);
        Assert.True(conf > 0.0 && conf <= 1.0);
    }

    [Fact]
    public void Allowlist_SuppressesDisallowedCharacters()
    {
        var allowed = CtcDecoder.BuildAllowedMask("abc", allowlist: "b", blocklist: null);
        Assert.NotNull(allowed);
        Assert.False(allowed![0]); // 'a'
        Assert.True(allowed[1]);   // 'b'
        Assert.False(allowed[2]);  // 'c'

        // 'a' is disallowed, so the first two steps fall back to blank; only the final 'b' survives.
        var logits = Logits(4, 1, 1, 0, 2);
        var (text, _) = CtcDecoder.GreedyDecode(logits, 4, 4, "abc", allowed);
        Assert.Equal("b", text);
    }

    [Fact]
    public void Blocklist_IsIgnoredWhenAllowlistSet_AndPrecedenceHolds()
    {
        // Allowlist wins: only 'a' allowed even though blocklist also names 'a'.
        var allowed = CtcDecoder.BuildAllowedMask("abc", allowlist: "a", blocklist: "a");
        Assert.True(allowed![0]);
        Assert.False(allowed[1]);
        Assert.False(allowed[2]);
    }

    [Fact]
    public void WordBeamSearch_DecodesDictionaryWord()
    {
        var trie = WordTrie.Build(new[] { "cat" });
        Assert.NotNull(trie);
        var logits = Logits(4, 1, 2, 3); // c, a, t
        var (text, _) = CtcDecoder.BeamSearchDecode(logits, 3, 4, "cat", allowed: null, beamWidth: 5, trie);
        Assert.Equal("cat", text);
    }
}

public class WordTrieTests
{
    [Fact]
    public void CanExtend_ConstrainsToLexiconPrefixes()
    {
        var trie = WordTrie.Build(new[] { "cat", "car" })!;

        Assert.True(trie.CanExtend("", 'c'));
        Assert.True(trie.CanExtend("c", 'a'));
        Assert.True(trie.CanExtend("ca", 't'));
        Assert.True(trie.CanExtend("ca", 'r'));
        Assert.False(trie.CanExtend("ca", 'x')); // not a prefix of cat/car
        Assert.True(trie.CanExtend("cat", ' ')); // whitespace always ends a word
    }

    [Fact]
    public void CanExtend_IsPermissiveOnceOffLexicon()
    {
        var trie = WordTrie.Build(new[] { "cat" })!;
        // "zz" is not a lexicon path, so further characters are no longer constrained.
        Assert.True(trie.CanExtend("zz", 'q'));
    }

    [Fact]
    public void Build_ReturnsNull_ForEmptyDictionary()
    {
        Assert.Null(WordTrie.Build(null));
        Assert.Null(WordTrie.Build(Array.Empty<string>()));
    }
}

public class GroupingThresholdTests
{
    private static OcrPoint[] Quad(double x0, double y0, double x1, double y1)
        => new OcrPoint[] { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    [Fact]
    public void WidthThreshold_ControlsHorizontalMerge()
    {
        // Two same-line boxes with a 20px gap (heights 10). Default width_ths (1.0 -> 10px) keeps them
        // apart; a larger width_ths merges them into a single region.
        var boxes = new[] { Quad(0, 0, 20, 10), Quad(40, 0, 60, 10) };

        var apart = TextBoxGrouper.Group(boxes, 200, 200, GroupingOptions.Default);
        Assert.Equal(2, apart.Count);

        var merged = TextBoxGrouper.Group(boxes, 200, 200, new GroupingOptions { WidthThreshold = 3.0 });
        Assert.Single(merged);
    }

    [Fact]
    public void VerticallySeparatedBoxes_BecomeSeparateLines()
    {
        var boxes = new[] { Quad(0, 0, 20, 10), Quad(0, 100, 20, 110) };
        var grouped = TextBoxGrouper.Group(boxes, 200, 200, GroupingOptions.Default);
        Assert.Equal(2, grouped.Count);
    }
}

public class ParagraphThresholdTests
{
    private static OcrLine Line(string text, double minX, double minY, double maxX, double maxY)
    {
        var bb = new OcrBoundingBox(minX, minY, maxX, maxY);
        var poly = new OcrPoint[] { new(minX, minY), new(maxX, minY), new(maxX, maxY), new(minX, maxY) };
        return new OcrLine { Text = text, Confidence = 0.9, BoundingBox = bb, BoundingPolygon = poly };
    }

    [Fact]
    public void CloseLines_MergeIntoOneParagraph()
    {
        var lines = new[]
        {
            Line("Hello", 0, 0, 100, 20),
            Line("World", 0, 25, 100, 45), // 5px gap, overlapping horizontally
        };
        var merged = ParagraphGrouper.Merge(lines, GroupingOptions.Default);
        Assert.Single(merged);
        Assert.Equal("Hello\nWorld", merged[0].Text);
    }

    [Fact]
    public void YThreshold_GatesVerticalMerge()
    {
        // 25px gap between 20px-tall lines: above the default y_ths (1.0 -> 20px) so they stay apart,
        // but a larger y_ths joins them.
        var lines = new[]
        {
            Line("Hello", 0, 0, 100, 20),
            Line("World", 0, 45, 100, 65),
        };

        var apart = ParagraphGrouper.Merge(lines, GroupingOptions.Default);
        Assert.Equal(2, apart.Count);

        var joined = ParagraphGrouper.Merge(lines, new GroupingOptions { ParagraphYThreshold = 2.0 });
        Assert.Single(joined);
    }
}

public class ParityOptionsTests
{
    [Fact]
    public void RecognitionOptions_ParityDefaults_AreSafe()
    {
        var o = RecognitionOptions.Default;
        Assert.Equal(DecoderType.Greedy, o.Decoder);
        Assert.Equal(5, o.BeamWidth);
        Assert.Equal(1, o.BatchSize);
        Assert.Equal(0.1, o.ContrastThreshold, 3);
        Assert.Equal(0.5, o.AdjustContrastTarget, 3);
        Assert.Null(o.RotationInfo);
        Assert.Null(o.Dictionary);
        Assert.NotNull(o.GroupingOptions);
        Assert.Equal(0.5, o.GroupingOptions.YCenterThreshold, 3);
    }

    [Fact]
    public void CustomRecognizer_WithoutCharactersOrVocab_Throws()
    {
        var opts = new EasyOcrServiceOptions();
        opts.CustomRecognizers.Add(new CustomRecognizer
        {
            Name = "custom",
            ModelPath = "model.onnx",
            Languages = new[] { "xx" },
        });

        // The engine validates custom recognizers at construction time.
        Assert.Throws<EasyOcrSharpException>(() => new EasyOcrService(opts));
    }

    [Fact]
    public void CustomRecognizer_WithCharacters_IsAccepted()
    {
        var opts = new EasyOcrServiceOptions();
        opts.CustomRecognizers.Add(new CustomRecognizer
        {
            Name = "custom",
            ModelPath = "model.onnx",
            Characters = "abc",
            Languages = new[] { "xx" },
        });

        // Construction succeeds; the model is only loaded on first use.
        using var service = new EasyOcrService(opts);
        Assert.NotNull(service);
    }

    [Fact]
    public void QuantizedModel_ResolvesInt8VariantWithChecksum()
    {
        var def = EasyOcrSharp.Internal.ModelRegistry.Latin;
        var int8 = EasyOcrSharp.Internal.ModelRegistry.QuantizedModel(def);

        Assert.Equal("latin_g2.int8.onnx", int8.FileName);
        Assert.EndsWith("latin_g2.int8.onnx", int8.Url);
        Assert.False(string.IsNullOrEmpty(int8.Sha256)); // verified download (checksum recorded)
    }

    [Fact]
    public async Task RecognizeRegionsAsync_WithNoRegions_ReturnsEmptyResult()
    {
        await using var service = new EasyOcrService();
        using var image = new Image<Rgb24>(16, 16, new Rgb24(255, 255, 255));

        var result = await service.RecognizeRegionsAsync(
            image, Array.Empty<IReadOnlyList<OcrPoint>>(), new[] { "en" });

        Assert.Empty(result.Lines);
        Assert.Equal(string.Empty, result.FullText);
        Assert.Equal(new[] { "en" }, result.Languages);
    }
}
