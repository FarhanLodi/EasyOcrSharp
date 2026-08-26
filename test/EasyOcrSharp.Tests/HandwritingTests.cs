using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Unit tests for handwriting recognition (feature #11, TrOCR). Everything here is a pure function —
/// the byte-level BPE decoder, the greedy/beam search over a synthetic logits sequence, the image
/// preprocessing, and the wiring that keeps the feature invisible when it is not configured. No model
/// is loaded and no network is touched, so these run in the default CI gate.
/// </summary>
public class TrOcrTokenizerTests
{
    // A miniature RoBERTa-style byte-level vocabulary, written with JSON \u escapes so the exact code
    // points are visible (and immune to any source-encoding accident):
    //   Ġ        = 'Ġ'   = byte 0x20, the space that starts a word
    //   Ã©  = 'Ã©'  = bytes C3 A9       -> "é" (2-byte UTF-8)
    //   âĤ¬ = 'âĤ¬' = bytes E2 82 AC -> "€" (3-byte UTF-8; 0x82 is a shifted byte)
    private const string Vocab = """
        {"<s>":0,"<pad>":1,"</s>":2,"Ġhello":3,"Ã©":4,"âĤ¬":5,"World":6}
        """;

    [Fact]
    public void Decodes_word_boundary_marker_as_a_space()
    {
        var tokenizer = TrOcrTokenizer.FromJson(Vocab);

        // <s> Ġhello </s>  ->  " hello" once the specials are dropped.
        Assert.Equal(" hello", tokenizer.Decode(new[] { 0, 3, 2 }));
    }

    [Fact]
    public void Decodes_multi_byte_utf8_split_across_byte_level_characters()
    {
        var tokenizer = TrOcrTokenizer.FromJson(Vocab);

        // Byte-level BPE stores "é" as the two characters standing for C3 and A9; only re-joining the
        // bytes before UTF-8 decoding recovers the character.
        Assert.Equal("é", tokenizer.Decode(new[] { 4 }));
        // "€" additionally exercises a byte (0x82) that has no printable form and was shifted to U+0124.
        Assert.Equal("€", tokenizer.Decode(new[] { 5 }));
        Assert.Equal(" hello€", tokenizer.Decode(new[] { 0, 3, 5, 2 }));
    }

    [Fact]
    public void Special_tokens_are_skipped_by_default_and_kept_on_request()
    {
        var tokenizer = TrOcrTokenizer.FromJson(Vocab);

        Assert.Equal(" helloWorld", tokenizer.Decode(new[] { 0, 3, 6, 2 }));
        Assert.Equal("<s> helloWorld</s>", tokenizer.Decode(new[] { 0, 3, 6, 2 }, skipSpecialTokens: false));
        Assert.Equal(new[] { 0, 1, 2 }, tokenizer.SpecialTokenIds.OrderBy(id => id));
    }

    [Fact]
    public void Unknown_ids_are_skipped_rather_than_throwing()
    {
        var tokenizer = TrOcrTokenizer.FromJson(Vocab);

        Assert.Equal(" hello", tokenizer.Decode(new[] { 3, 99, -1 }));
        Assert.Null(tokenizer.TokenAt(99));
    }

    [Fact]
    public void Accepts_a_full_tokenizer_json_and_an_ordered_array()
    {
        var nested = TrOcrTokenizer.FromJson("""
            {"version":"1.0","model":{"type":"BPE","vocab":{"<s>":0,"</s>":2,"Ġok":1}}}
            """);
        Assert.Equal(" ok", nested.Decode(new[] { 0, 1, 2 }));

        var ordered = TrOcrTokenizer.FromJson("""["<s>","Ġok","</s>"]""");
        Assert.Equal(" ok", ordered.Decode(new[] { 0, 1, 2 }));
    }

    [Fact]
    public void Rejects_a_document_that_holds_no_vocabulary()
    {
        Assert.Throws<EasyOcrSharpException>(() => TrOcrTokenizer.FromJson("""{"note":"not a vocab"}"""));
        Assert.Throws<EasyOcrSharpException>(() => TrOcrTokenizer.FromJson("42"));
    }
}

/// <summary>
/// Greedy and beam decoding driven by a synthetic logits sequence — no ONNX session involved, which is
/// exactly what the search abstraction exists for.
/// </summary>
public class TrOcrSearchTests
{
    private const int Classes = 5;
    private const int Eos = 2;

    private static TrOcrSearchOptions Options(int beamWidth = 1, int maxTokens = 16) => new()
    {
        StartTokenId = Eos,               // TrOCR primes the decoder with </s>
        EndOfSequenceTokenId = Eos,
        BeamWidth = beamWidth,
        MaxTokens = maxTokens,
        UnscoredTokenIds = new HashSet<int> { 0, 1, 2 },
    };

    /// <summary>Logits with a single peak, i.e. an unambiguous argmax at <paramref name="index"/>.</summary>
    private static float[] Peak(int index, float value = 8f)
    {
        var logits = new float[Classes];
        logits[index] = value;
        return logits;
    }

    private static double SoftmaxAt(float[] logits, int index)
    {
        double max = logits.Max();
        double sum = logits.Sum(l => Math.Exp(l - max));
        return Math.Exp(logits[index] - max) / sum;
    }

    private static TrOcrGeneration Decode(TrOcrSearchOptions options, Func<IReadOnlyList<int>, float[]> steps)
        => TrOcrSearch.Run<object>(options, () => new object(), state => state, (_, tokens) => steps(tokens));

    [Fact]
    public void Greedy_emits_the_argmax_of_every_step_and_stops_at_end_of_sequence()
    {
        var steps = new[] { Peak(3), Peak(4), Peak(Eos) };

        var generation = Decode(Options(), tokens => steps[tokens.Count - 1]);

        Assert.Equal(new[] { 3, 4 }, generation.TokenIds);
        Assert.False(generation.HitTokenLimit);

        // Confidence is the geometric mean of the emitted tokens' probabilities; both steps share the
        // same shape, so it collapses to that single probability.
        double p = SoftmaxAt(Peak(3), 3);
        Assert.Equal(p, generation.Confidence, 6);
    }

    [Fact]
    public void Greedy_stops_at_the_token_limit_when_the_model_never_predicts_end_of_sequence()
    {
        // A degenerate decoder that always wants the same token: MaxTokens is the only thing that stops it.
        var generation = Decode(Options(maxTokens: 3), _ => Peak(3));

        Assert.Equal(new[] { 3, 3, 3 }, generation.TokenIds);
        Assert.True(generation.HitTokenLimit);
    }

    [Fact]
    public void Structural_tokens_are_generated_but_left_out_of_the_confidence()
    {
        // TrOCR emits <s> (id 0) before the first real token; a low-probability <s> must not drag the
        // reading's confidence down, so only token 3 is averaged.
        var steps = new[] { Peak(0, 1f), Peak(3), Peak(Eos) };

        var generation = Decode(Options(), tokens => steps[tokens.Count - 1]);

        Assert.Equal(new[] { 0, 3 }, generation.TokenIds);
        Assert.Equal(SoftmaxAt(Peak(3), 3), generation.Confidence, 6);
    }

    [Fact]
    public void Beam_search_prefers_a_sequence_greedy_throws_away()
    {
        // Step 0 slightly prefers token 3 over token 4, but only the token-4 branch can then finish
        // confidently. Greedy commits to 3; a width-2 beam keeps both alive and picks 4.
        var first = new float[Classes];
        first[3] = 1.0f;
        first[4] = 0.9f;
        var afterThree = new float[Classes];
        afterThree[Eos] = 0.5f;
        var afterFour = new float[Classes];
        afterFour[Eos] = 5.0f;

        float[] Steps(IReadOnlyList<int> tokens)
            => tokens.Count == 1 ? first : tokens[^1] == 3 ? afterThree : afterFour;

        Assert.Equal(new[] { 3 }, Decode(Options(), Steps).TokenIds);
        Assert.Equal(new[] { 4 }, Decode(Options(beamWidth: 2), Steps).TokenIds);
    }

    [Fact]
    public void Beam_search_also_honours_the_token_limit()
    {
        var generation = Decode(Options(beamWidth: 3, maxTokens: 2), _ => Peak(3));

        Assert.Equal(new[] { 3, 3 }, generation.TokenIds);
        Assert.True(generation.HitTokenLimit);
    }
}

/// <summary>TrOCR's fixed image preprocessing: square resize, 0–1 scaling, mean/std normalization.</summary>
public class TrOcrPreprocessTests
{
    private static float[] Run(Rgb24 color, int size = 32)
    {
        using var image = new Image<Rgb24>(11, 7, color);
        return TrOcrRecognizer.Preprocess(image, size, mean: 0.5f, std: 0.5f);
    }

    [Fact]
    public void Produces_a_chw_tensor_of_the_requested_square_size()
    {
        var tensor = Run(new Rgb24(255, 255, 255), size: 32);
        Assert.Equal(3 * 32 * 32, tensor.Length);
    }

    [Fact]
    public void Normalizes_white_to_one_and_black_to_minus_one()
    {
        Assert.All(Run(new Rgb24(255, 255, 255)), v => Assert.Equal(1.0, v, 4));
        Assert.All(Run(new Rgb24(0, 0, 0)), v => Assert.Equal(-1.0, v, 4));
    }

    [Fact]
    public void Applies_the_documented_scale_then_normalize_formula()
    {
        // (128/255 - 0.5) / 0.5
        double expected = (128 / 255.0 - 0.5) / 0.5;
        Assert.All(Run(new Rgb24(128, 128, 128)), v => Assert.Equal(expected, v, 3));
    }

    [Fact]
    public void Channels_are_planar_red_then_green_then_blue()
    {
        const int size = 8;
        var tensor = Run(new Rgb24(255, 0, 0), size);
        int plane = size * size;

        Assert.Equal(1.0, tensor[0], 4);                 // red plane
        Assert.Equal(-1.0, tensor[plane], 4);            // green plane
        Assert.Equal(-1.0, tensor[2 * plane], 4);        // blue plane
    }
}

/// <summary>
/// The contract that matters most for backwards compatibility: with
/// <see cref="EasyOcrServiceOptions.Handwriting"/> left null, nothing about the service changes.
/// </summary>
public class HandwritingConfigurationTests
{
    [Fact]
    public void Handwriting_is_off_by_default()
    {
        var options = new EasyOcrServiceOptions();

        Assert.Null(options.Handwriting);
        Assert.Null(HandwritingRegistry.Resolve(options.ToEngineOptions()));
    }

    [Fact]
    public void Enabling_handwriting_does_not_alter_any_other_engine_setting()
    {
        var without = new EasyOcrServiceOptions { ModelCachePath = null, Quantize = true, IntraOpNumThreads = 2 };
        var baseline = without.ToEngineOptions();

        without.Handwriting = new HandwritingOptions
        {
            EncoderModelPath = "encoder.onnx",
            DecoderModelPath = "decoder.onnx",
            TokenizerPath = "vocab.json",
        };
        var enabled = without.ToEngineOptions();

        Assert.Equal(baseline.ExecutionProvider, enabled.ExecutionProvider);
        Assert.Equal(baseline.ModelCachePath, enabled.ModelCachePath);
        Assert.Equal(baseline.Quantize, enabled.Quantize);
        Assert.Equal(baseline.IntraOpNumThreads, enabled.IntraOpNumThreads);
        Assert.Equal(baseline.InterOpNumThreads, enabled.InterOpNumThreads);
        Assert.Equal(baseline.LogGpuHint, enabled.LogGpuHint);

        // ...and the handwriting settings are reachable from the instance the service keeps.
        Assert.Null(HandwritingRegistry.Resolve(baseline));
        Assert.Same(without.Handwriting, HandwritingRegistry.Resolve(enabled));
    }

    [Fact]
    public async Task Unconfigured_service_reports_no_handwriting_support_and_refuses_the_call()
    {
        await using var ocr = new EasyOcrService();
        using var image = new Image<Rgb24>(16, 16);

        Assert.False(ocr.SupportsHandwriting);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.RecognizeHandwritingAsync(image));

        // Unloading a recognizer that was never loaded is a no-op, not a crash.
        ocr.UnloadHandwritingModels();
    }

    [Fact]
    public void Configured_service_reports_support_without_loading_anything()
    {
        // Paths are never touched until the first recognition call, so a service can be constructed and
        // inspected without the (large) model files being present.
        var options = new EasyOcrServiceOptions
        {
            Handwriting = new HandwritingOptions
            {
                EncoderModelPath = "missing-encoder.onnx",
                DecoderModelPath = "missing-decoder.onnx",
                TokenizerPath = "missing-vocab.json",
            },
        };

        using var ocr = new EasyOcrService(options);
        Assert.True(ocr.SupportsHandwriting);
    }

    [Fact]
    public void Defaults_match_the_published_trocr_handwriting_checkpoints()
    {
        var options = new HandwritingOptions
        {
            EncoderModelPath = "e.onnx",
            DecoderModelPath = "d.onnx",
            TokenizerPath = "v.json",
        };

        Assert.Equal(384, options.ImageSize);
        Assert.Equal(0.5f, options.NormalizationMean);
        Assert.Equal(0.5f, options.NormalizationStd);
        Assert.Equal(2, options.DecoderStartTokenId);
        Assert.Equal(2, options.EndOfSequenceTokenId);
        Assert.Equal(1, options.PadTokenId);
        Assert.Equal(1, options.BeamWidth);   // greedy
        Assert.Equal("en", options.Language);
    }

    [Fact]
    public void Validation_reports_a_missing_model_file_instead_of_failing_inside_onnx_runtime()
    {
        var options = new HandwritingOptions
        {
            EncoderModelPath = Path.Combine(Path.GetTempPath(), "easyocrsharp-no-such-encoder.onnx"),
            DecoderModelPath = Path.Combine(Path.GetTempPath(), "easyocrsharp-no-such-decoder.onnx"),
            TokenizerPath = Path.Combine(Path.GetTempPath(), "easyocrsharp-no-such-vocab.json"),
        };

        var ex = Assert.Throws<FileNotFoundException>(() => options.Validate());
        Assert.Contains("encoder", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromDirectory_uses_the_conventional_export_layout()
    {
        var directory = Path.Combine(Path.GetTempPath(), "easyocrsharp-trocr-layout");
        var options = HandwritingOptions.FromDirectory(directory);

        Assert.EndsWith(HandwritingOptions.DefaultEncoderFileName, options.EncoderModelPath);
        Assert.EndsWith(HandwritingOptions.DefaultDecoderFileName, options.DecoderModelPath);
        Assert.EndsWith(HandwritingOptions.DefaultTokenizerFileName, options.TokenizerPath);
    }
}

/// <summary>
/// End-to-end handwriting recognition against a real TrOCR export. Skipped unless the exported model
/// directory (and a sample image) are pointed to by environment variables, because the weights are
/// user-supplied — nothing is downloaded for this feature.
/// </summary>
[Trait("Category", "Integration")]
[Collection(OcrIntegrationCollection.Name)]
public class HandwritingIntegrationTests
{
    [SkippableFact]
    public async Task Reads_a_handwritten_image_with_a_local_trocr_export()
    {
        var modelDirectory = Environment.GetEnvironmentVariable("EASYOCRSHARP_TROCR_DIR");
        Skip.If(string.IsNullOrWhiteSpace(modelDirectory) || !Directory.Exists(modelDirectory),
            "Set EASYOCRSHARP_TROCR_DIR to a directory produced by tools/export_trocr_onnx.py.");

        var imagePath = Environment.GetEnvironmentVariable("EASYOCRSHARP_TROCR_IMAGE");
        Skip.If(string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath),
            "Set EASYOCRSHARP_TROCR_IMAGE to a handwritten sample image.");

        await using var ocr = new EasyOcrService(new EasyOcrServiceOptions
        {
            Handwriting = HandwritingOptions.FromDirectory(modelDirectory!),
        });

        Assert.True(ocr.SupportsHandwriting);
        var result = await ocr.RecognizeHandwritingAsync(imagePath!);

        Assert.NotEmpty(result.Lines);
        Assert.False(string.IsNullOrWhiteSpace(result.FullText));
        Assert.All(result.Lines, line => Assert.InRange(line.Confidence, 0.0, 1.0));

        ocr.UnloadHandwritingModels();
    }
}
