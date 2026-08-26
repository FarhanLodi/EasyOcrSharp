using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Runs a locally exported <b>TrOCR</b> encoder-decoder over rectified text regions — the handwriting
/// recognizer EasyOCR has no equivalent of. The ViT encoder turns a 384×384 crop into a patch-embedding
/// sequence; the text decoder then generates token ids one at a time, conditioned on that sequence and
/// on everything it has already written, until it predicts end-of-sequence or hits
/// <see cref="HandwritingOptions.MaxTokens"/>. Ids become text through <see cref="TrOcrTokenizer"/>.
/// <para>
/// Two decoder exports are supported and told apart from the session's input metadata rather than from
/// the file name: a plain decoder (no <c>past_key_values</c> inputs), where the whole prefix is re-fed
/// every step, and a cached decoder (<c>past_key_values.*</c> present, as in optimum's merged export),
/// where only the newest token is fed and the attention keys/values are carried forward. The cached
/// path is O(n) in the generated length instead of O(n²).
/// </para>
/// <para>
/// Sessions are created once and reused; <see cref="Recognize(Image{Rgb24}, IReadOnlyList{OcrPoint[]}, CancellationToken)"/>
/// is safe to call concurrently because all mutable decode state lives in per-call objects.
/// </para>
/// </summary>
internal sealed class TrOcrRecognizer : IDisposable
{
    /// <summary>Prefix optimum gives every key/value cache <b>input</b> of an exported decoder.</summary>
    private const string PastPrefix = "past_key_values";

    /// <summary>Prefix optimum gives every key/value cache <b>output</b> of an exported decoder.</summary>
    private const string PresentPrefix = "present";

    /// <summary>Boolean switch a <i>merged</i> decoder uses to pick its no-cache / with-cache branch.</summary>
    private const string UseCacheBranchName = "use_cache_branch";

    private readonly HandwritingOptions _options;
    private readonly TrOcrTokenizer _tokenizer;
    private readonly TrOcrSearchOptions _search;
    private readonly SessionOptions _sessionOptions;
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;

    private readonly string _pixelValuesName;
    private readonly string _encoderOutputName;
    private readonly string _inputIdsName;
    private readonly string _encoderStatesName;
    private readonly string? _encoderMaskName;
    private readonly string _logitsName;
    private readonly bool _hasUseCacheBranch;
    private readonly PastKeyValueSlot[] _pastSlots;

    /// <summary>
    /// Loads the encoder and decoder from the paths in <paramref name="options"/> and resolves the
    /// graphs' input/output names. Sessions run on the engine's resolved execution provider, falling
    /// back to CPU once if the accelerated session cannot initialize — the same policy as the OCR
    /// engine's first model load.
    /// </summary>
    /// <exception cref="FileNotFoundException">An encoder, decoder or tokenizer file is missing.</exception>
    /// <exception cref="EasyOcrSharpException">The exported graphs are not a usable TrOCR pair.</exception>
    public TrOcrRecognizer(HandwritingOptions options, EngineOptions engineOptions, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(engineOptions);
        options.Validate();

        _options = options;
        _tokenizer = TrOcrTokenizer.FromFile(options.ResolvedTokenizerPath);
        (_encoder, _decoder, _sessionOptions) = OpenSessions(options, engineOptions, logger);

        try
        {
            _pixelValuesName = ResolveEncoderImageInput(_encoder, options.ResolvedEncoderPath);
            _encoderOutputName = ResolveEncoderOutput(_encoder);
            _inputIdsName = ResolveInputIds(_decoder, options.ResolvedDecoderPath);
            _encoderStatesName = ResolveEncoderStatesInput(_decoder, options.ResolvedDecoderPath);
            _encoderMaskName = _decoder.InputMetadata.ContainsKey("encoder_attention_mask") ? "encoder_attention_mask" : null;
            _hasUseCacheBranch = _decoder.InputMetadata.ContainsKey(UseCacheBranchName);
            _logitsName = ResolveLogitsOutput(_decoder);
            _pastSlots = BuildPastSlots(_decoder, options.ResolvedDecoderPath, _hasUseCacheBranch);
        }
        catch
        {
            Dispose();
            throw;
        }

        _search = new TrOcrSearchOptions
        {
            StartTokenId = options.DecoderStartTokenId,
            EndOfSequenceTokenId = options.EndOfSequenceTokenId,
            MaxTokens = options.MaxTokens,
            BeamWidth = options.BeamWidth,
            LengthPenalty = options.LengthPenalty,
            // <s>, </s>, <pad> and friends are generated but carry no text, so they are excluded from
            // the confidence average (see TrOcrSearchOptions.UnscoredTokenIds).
            UnscoredTokenIds = BuildUnscoredIds(_tokenizer, options),
        };

        logger?.LogInformation(
            "TrOCR handwriting recognizer loaded (encoder '{Encoder}', decoder '{Decoder}', {Vocab} tokens, {Mode} decoding).",
            options.ResolvedEncoderPath, options.ResolvedDecoderPath, _tokenizer.Count,
            _pastSlots.Length > 0 ? "key/value-cached" : "full-prefix");
    }

    /// <summary>True when the decoder graph carries a key/value cache and only the newest token is fed.</summary>
    public bool UsesKeyValueCache => _pastSlots.Length > 0;

    /// <summary>
    /// Recognizes every supplied region: rectifies the polygon out of the source image, runs TrOCR over
    /// the crop, and returns one <see cref="OcrLine"/> per region in the input order. A region that
    /// cannot be rectified (a degenerate polygon) yields an empty line rather than failing the page.
    /// </summary>
    public IReadOnlyList<OcrLine> Recognize(
        Image<Rgb24> source, IReadOnlyList<OcrPoint[]> polygons, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(polygons);

        var results = new OcrLine[polygons.Count];
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxDegreeOfParallelism),
            CancellationToken = cancellationToken,
        };

        Parallel.For(0, polygons.Count, parallelOptions, i =>
        {
            var polygon = polygons[i];
            var box = OcrBoundingBox.FromPoints(polygon);
            using var crop = PerspectiveWarp.Rectify(source, polygon);
            if (crop is null || crop.Width == 0 || crop.Height == 0)
            {
                results[i] = new OcrLine { Text = string.Empty, Confidence = 0, BoundingPolygon = polygon, BoundingBox = box };
                return;
            }

            var (text, confidence) = Recognize(crop);
            results[i] = new OcrLine { Text = text, Confidence = confidence, BoundingPolygon = polygon, BoundingBox = box };
        });

        return results;
    }

    /// <summary>
    /// Recognizes one already-rectified crop. The confidence is the geometric mean of the per-token
    /// probabilities of the emitted text tokens — <c>exp((1/n)·Σ ln p(tokenᵢ))</c> — which keeps it on
    /// the same 0–1 scale as the CRNN line scores and independent of how long the reading is. The
    /// terminating end-of-sequence token and the structural <c>&lt;s&gt;</c>/<c>&lt;pad&gt;</c> tokens
    /// are excluded, so the number measures the text rather than the model's willingness to stop.
    /// </summary>
    public (string Text, double Confidence) Recognize(Image<Rgb24> crop)
    {
        ArgumentNullException.ThrowIfNull(crop);
        if (crop.Width == 0 || crop.Height == 0) return (string.Empty, 0.0);

        var pixels = Preprocess(crop, _options.ImageSize, _options.NormalizationMean, _options.NormalizationStd);
        var encoderStates = RunEncoder(pixels);

        var generation = TrOcrSearch.Run(
            _search,
            createState: () => new DecoderState(),
            cloneState: static state => state.Clone(),
            nextLogits: (state, tokens) => NextTokenLogits(state, tokens, encoderStates));

        // Byte-level BPE marks a word-initial token with a leading space, so the first token of a line
        // usually decodes with one; trim it (and any trailing padding space) as EasyOCR's lines are.
        var text = _tokenizer.Decode(generation.TokenIds).Trim();
        return (text, generation.Confidence);
    }

    /// <summary>
    /// TrOCR's image preprocessing: stretch the crop to a square <paramref name="size"/>×<paramref name="size"/>
    /// RGB image (bilinear, matching the reference processor's <c>resample=2</c>), scale pixels to 0–1,
    /// then normalize per channel as <c>(p − mean) / std</c>. Returns a CHW tensor of
    /// <c>3·size·size</c> floats — channel-major, red plane first — ready to be wrapped in a
    /// <c>[1, 3, size, size]</c> ONNX tensor.
    /// </summary>
    internal static float[] Preprocess(Image<Rgb24> crop, int size, float mean, float std)
    {
        var tensor = new float[3 * size * size];
        using var resized = crop.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(size, size),
            Sampler = KnownResamplers.Triangle,
            Mode = ResizeMode.Stretch,
        }));

        int plane = size * size;
        resized.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < size; y++)
            {
                var row = rows.GetRowSpan(y);
                int rowStart = y * size;
                for (int x = 0; x < size; x++)
                {
                    var pixel = row[x];
                    int index = rowStart + x;
                    tensor[index] = (pixel.R / 255f - mean) / std;
                    tensor[plane + index] = (pixel.G / 255f - mean) / std;
                    tensor[2 * plane + index] = (pixel.B / 255f - mean) / std;
                }
            }
        });

        return tensor;
    }

    // ---- inference ----

    /// <summary>Runs the ViT encoder once and copies its hidden states out of the run's owned buffers.</summary>
    private DenseTensor<float> RunEncoder(float[] pixels)
    {
        var input = new DenseTensor<float>(pixels, new[] { 1, 3, _options.ImageSize, _options.ImageSize });
        using var run = _encoder.Run(new[] { NamedOnnxValue.CreateFromTensor(_pixelValuesName, input) });
        var output = run.First(o => o.Name == _encoderOutputName).AsTensor<float>();
        if (output.Dimensions.Length != 3)
        {
            throw new EasyOcrSharpException(
                $"Unexpected TrOCR encoder output shape [{string.Join(",", output.Dimensions.ToArray())}]. Expected [batch, sequence, hidden].");
        }
        return CopyTensor(output);
    }

    /// <summary>
    /// One decoder step: feeds the prefix (or, with a key/value cache, only the newest token plus the
    /// carried-forward cache) and returns the logits for the next position. The cache lives in
    /// <paramref name="state"/> and is replaced — never mutated in place — so a beam that clones a
    /// parent's state shares its tensors safely.
    /// </summary>
    private float[] NextTokenLogits(DecoderState state, IReadOnlyList<int> tokens, DenseTensor<float> encoderStates)
    {
        bool cached = _pastSlots.Length > 0;
        bool firstStep = state.Past is null;

        // With a warm cache the graph has already consumed everything except the newest token.
        int offset = cached && !firstStep ? tokens.Count - 1 : 0;
        int length = tokens.Count - offset;
        var ids = new DenseTensor<long>(new[] { 1, length });
        for (int i = 0; i < length; i++) ids[0, i] = tokens[offset + i];

        var feeds = new List<NamedOnnxValue>(4 + _pastSlots.Length)
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName, ids),
            NamedOnnxValue.CreateFromTensor(_encoderStatesName, encoderStates),
        };

        if (_encoderMaskName is not null)
        {
            int encoderLength = encoderStates.Dimensions[1];
            var mask = new DenseTensor<long>(new[] { 1, encoderLength });
            for (int i = 0; i < encoderLength; i++) mask[0, i] = 1;
            feeds.Add(NamedOnnxValue.CreateFromTensor(_encoderMaskName, mask));
        }

        if (cached)
        {
            foreach (var slot in _pastSlots)
            {
                var tensor = state.Past is { } past && past.TryGetValue(slot.InputName, out var cachedTensor)
                    ? cachedTensor
                    : slot.CreateEmpty();
                feeds.Add(NamedOnnxValue.CreateFromTensor(slot.InputName, tensor));
            }
            if (_hasUseCacheBranch)
            {
                var flag = new DenseTensor<bool>(new[] { !firstStep }, new[] { 1 });
                feeds.Add(NamedOnnxValue.CreateFromTensor(UseCacheBranchName, flag));
            }
        }

        using var run = _decoder.Run(feeds);
        var logits = LastPositionLogits(run.First(o => o.Name == _logitsName).AsTensor<float>());

        if (cached)
        {
            var updated = state.Past is null
                ? new Dictionary<string, DenseTensor<float>>(_pastSlots.Length, StringComparer.Ordinal)
                : new Dictionary<string, DenseTensor<float>>(state.Past, StringComparer.Ordinal);
            foreach (var slot in _pastSlots)
            {
                // A with-cache branch does not recompute the cross-attention keys/values, so its
                // `present.*.encoder.*` outputs may be absent; the first step's values stay valid.
                var present = run.FirstOrDefault(o => o.Name == slot.PresentName);
                if (present is not null) updated[slot.InputName] = CopyTensor(present.AsTensor<float>());
            }
            state.Past = updated;
        }

        return logits;
    }

    /// <summary>Extracts the final position's row from a <c>[1, length, vocab]</c> (or <c>[length, vocab]</c>) logits tensor.</summary>
    private static float[] LastPositionLogits(Tensor<float> logits)
    {
        var dims = logits.Dimensions;
        int length, vocab;
        if (dims.Length == 3 && dims[0] == 1) { length = dims[1]; vocab = dims[2]; }
        else if (dims.Length == 2) { length = dims[0]; vocab = dims[1]; }
        else
        {
            throw new EasyOcrSharpException(
                $"Unexpected TrOCR decoder output shape [{string.Join(",", dims.ToArray())}]. Expected [1, length, vocab].");
        }
        if (length < 1 || vocab < 1) return Array.Empty<float>();

        ReadOnlySpan<float> data = logits is DenseTensor<float> dense ? dense.Buffer.Span : logits.ToArray();
        var row = new float[vocab];
        data.Slice((length - 1) * vocab, vocab).CopyTo(row);
        return row;
    }

    /// <summary>Copies an ONNX Runtime-owned tensor into a buffer that outlives the run that produced it.</summary>
    private static DenseTensor<float> CopyTensor(Tensor<float> tensor)
    {
        var buffer = tensor is DenseTensor<float> dense ? dense.Buffer.ToArray() : tensor.ToArray();
        return new DenseTensor<float>(buffer, tensor.Dimensions.ToArray());
    }

    // ---- graph introspection ----

    private static (InferenceSession Encoder, InferenceSession Decoder, SessionOptions Options) OpenSessions(
        HandwritingOptions options, EngineOptions engineOptions, ILogger? logger)
    {
        var provider = ExecutionProviderResolver.Resolve(engineOptions.ExecutionProvider, logger);
        var sessionOptions = ExecutionProviderResolver.BuildSessionOptions(provider, engineOptions, logger, perBoxParallel: false);
        try
        {
            var (encoder, decoder) = OpenPair(options, sessionOptions);
            return (encoder, decoder, sessionOptions);
        }
        catch (Exception ex) when (provider != OcrExecutionProvider.Cpu)
        {
            sessionOptions.Dispose();
            logger?.LogWarning(ex, "Accelerated session initialization failed for TrOCR handwriting; falling back to CPU.");
            var cpuOptions = ExecutionProviderResolver.BuildSessionOptions(OcrExecutionProvider.Cpu, engineOptions, logger, perBoxParallel: false);
            try
            {
                var (encoder, decoder) = OpenPair(options, cpuOptions);
                return (encoder, decoder, cpuOptions);
            }
            catch
            {
                cpuOptions.Dispose();
                throw;
            }
        }
        catch
        {
            sessionOptions.Dispose();
            throw;
        }

        static (InferenceSession Encoder, InferenceSession Decoder) OpenPair(HandwritingOptions options, SessionOptions sessionOptions)
        {
            var encoder = new InferenceSession(options.ResolvedEncoderPath, sessionOptions);
            try
            {
                return (encoder, new InferenceSession(options.ResolvedDecoderPath, sessionOptions));
            }
            catch
            {
                encoder.Dispose();
                throw;
            }
        }
    }

    private static string ResolveEncoderImageInput(InferenceSession encoder, string modelPath)
    {
        if (encoder.InputMetadata.ContainsKey("pixel_values")) return "pixel_values";
        foreach (var (name, meta) in encoder.InputMetadata)
        {
            if (meta.ElementType == typeof(float) && meta.Dimensions.Length == 4) return name;
        }
        throw new EasyOcrSharpException(
            $"TrOCR encoder '{modelPath}' has no 4-D float image input (expected 'pixel_values'). " +
            "Re-export it with tools/export_trocr_onnx.py.");
    }

    private static string ResolveEncoderOutput(InferenceSession encoder)
        => encoder.OutputMetadata.ContainsKey("last_hidden_state") ? "last_hidden_state" : encoder.OutputMetadata.Keys.First();

    private static string ResolveInputIds(InferenceSession decoder, string modelPath)
    {
        if (decoder.InputMetadata.ContainsKey("input_ids")) return "input_ids";
        foreach (var (name, meta) in decoder.InputMetadata)
        {
            if (meta.ElementType == typeof(long) && meta.Dimensions.Length == 2 && !name.StartsWith(PastPrefix, StringComparison.Ordinal))
                return name;
        }
        throw new EasyOcrSharpException(
            $"TrOCR decoder '{modelPath}' has no token-id input (expected 'input_ids'). " +
            "Re-export it with tools/export_trocr_onnx.py.");
    }

    private static string ResolveEncoderStatesInput(InferenceSession decoder, string modelPath)
    {
        if (decoder.InputMetadata.ContainsKey("encoder_hidden_states")) return "encoder_hidden_states";
        foreach (var (name, meta) in decoder.InputMetadata)
        {
            if (meta.ElementType == typeof(float) && meta.Dimensions.Length == 3 && !name.StartsWith(PastPrefix, StringComparison.Ordinal))
                return name;
        }
        throw new EasyOcrSharpException(
            $"TrOCR decoder '{modelPath}' has no encoder-state input (expected 'encoder_hidden_states'). " +
            "It must be the decoder half of a vision-encoder-decoder export.");
    }

    private static string ResolveLogitsOutput(InferenceSession decoder)
    {
        if (decoder.OutputMetadata.ContainsKey("logits")) return "logits";
        foreach (var name in decoder.OutputMetadata.Keys)
        {
            if (!name.StartsWith(PresentPrefix, StringComparison.Ordinal)) return name;
        }
        return decoder.OutputMetadata.Keys.First();
    }

    /// <summary>
    /// Maps every <c>past_key_values.*</c> input to the <c>present.*</c> output that refreshes it, and
    /// reads the head count / head width out of the graph so a zero-length cache can be synthesized for
    /// the first step. Returns an empty array when the decoder has no cache at all (the plain export),
    /// which selects full-prefix decoding.
    /// </summary>
    private static PastKeyValueSlot[] BuildPastSlots(InferenceSession decoder, string modelPath, bool hasUseCacheBranch)
    {
        var slots = new List<PastKeyValueSlot>();
        foreach (var (name, meta) in decoder.InputMetadata)
        {
            if (!name.StartsWith(PastPrefix, StringComparison.Ordinal)) continue;

            var present = PresentPrefix + name[PastPrefix.Length..];
            var dims = meta.Dimensions;
            if (dims.Length != 4 || dims[1] <= 0 || dims[3] <= 0)
            {
                throw new EasyOcrSharpException(
                    $"TrOCR decoder '{modelPath}' declares cache input '{name}' with shape " +
                    $"[{string.Join(",", dims)}]; the head count and head width must be static so an empty " +
                    "cache can be built for the first step. Export the plain 'decoder_model.onnx' instead.");
            }
            slots.Add(new PastKeyValueSlot(name, present, dims[1], dims[3]));
        }

        if (slots.Count == 0) return Array.Empty<PastKeyValueSlot>();

        // optimum's stand-alone `decoder_with_past_model.onnx` takes the cross-attention keys/values as
        // inputs and never recomputes them, so it cannot run the very first step on its own — it is only
        // ever the second half of a two-file pipeline. Detect that shape and say so plainly.
        bool needsCrossAttentionCache = slots.Any(s => s.InputName.Contains(".encoder.", StringComparison.Ordinal));
        bool producesCrossAttentionCache = slots
            .Where(s => s.InputName.Contains(".encoder.", StringComparison.Ordinal))
            .All(s => decoder.OutputMetadata.ContainsKey(s.PresentName));
        if (needsCrossAttentionCache && !producesCrossAttentionCache && !hasUseCacheBranch)
        {
            throw new EasyOcrSharpException(
                $"TrOCR decoder '{modelPath}' looks like optimum's 'decoder_with_past_model.onnx': it consumes " +
                "cross-attention key/value caches without producing them, so it cannot generate the first token. " +
                "Point HandwritingOptions.DecoderModelPath at 'decoder_model.onnx' or 'decoder_model_merged.onnx'.");
        }

        return slots.ToArray();
    }

    /// <summary>
    /// The token ids kept out of the confidence average: the tokenizer's structural tokens plus the
    /// configured start / end / pad ids, which a vocabulary without the usual <c>&lt;s&gt;</c> spellings
    /// would otherwise leave unaccounted for.
    /// </summary>
    private static IReadOnlySet<int> BuildUnscoredIds(TrOcrTokenizer tokenizer, HandwritingOptions options)
    {
        var ids = new HashSet<int>(tokenizer.SpecialTokenIds)
        {
            options.DecoderStartTokenId,
            options.EndOfSequenceTokenId,
            options.PadTokenId,
        };
        return ids;
    }

    /// <summary>
    /// Releases both ONNX sessions and the session options they were created with. Safe to call twice —
    /// the constructor itself calls it if graph introspection rejects the exported pair.
    /// </summary>
    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
        _sessionOptions.Dispose();
    }

    /// <summary>
    /// One key/value cache entry of the decoder: the graph input that receives it, the graph output that
    /// refreshes it, and the static head geometry needed to synthesize the zero-length cache the first
    /// step is fed.
    /// </summary>
    private readonly record struct PastKeyValueSlot(string InputName, string PresentName, int Heads, int HeadWidth)
    {
        /// <summary>An empty <c>[1, heads, 0, width]</c> cache — "nothing has been attended to yet".</summary>
        public DenseTensor<float> CreateEmpty() => new(new[] { 1, Heads, 0, HeadWidth });
    }

    /// <summary>
    /// Per-hypothesis decoder state. <see cref="Past"/> is null until the first step has run (which
    /// selects the no-cache branch and the full prefix), then holds one tensor per
    /// <see cref="PastKeyValueSlot"/>. Cloning copies the dictionary but shares the tensors, which is
    /// safe because a step replaces entries rather than writing into them.
    /// </summary>
    private sealed class DecoderState
    {
        public Dictionary<string, DenseTensor<float>>? Past { get; set; }

        public DecoderState Clone() => new()
        {
            Past = Past is null ? null : new Dictionary<string, DenseTensor<float>>(Past, StringComparer.Ordinal),
        };
    }
}

/// <summary>
/// Settings for the autoregressive search that drives an encoder-decoder recognizer. Kept separate from
/// <see cref="HandwritingOptions"/> (and free of any ONNX types) so the search itself is a pure,
/// unit-testable function of a logits sequence.
/// </summary>
internal sealed record TrOcrSearchOptions
{
    /// <summary>Token the decoder is primed with; it is never part of the output.</summary>
    public required int StartTokenId { get; init; }

    /// <summary>Token that terminates a hypothesis. It is scored for ranking but never emitted.</summary>
    public required int EndOfSequenceTokenId { get; init; }

    /// <summary>Hard cap on generated tokens — the guarantee that a degenerate model still terminates.</summary>
    public int MaxTokens { get; init; } = 64;

    /// <summary>Hypotheses kept alive. 1 = greedy decoding.</summary>
    public int BeamWidth { get; init; } = 1;

    /// <summary>Exponent applied to the length when normalizing a beam's log-probability.</summary>
    public double LengthPenalty { get; init; } = 1.0;

    /// <summary>
    /// Tokens that are generated but excluded from the confidence average because they carry no text
    /// (<c>&lt;s&gt;</c>, <c>&lt;pad&gt;</c>, …). They still influence beam ranking, since predicting
    /// them confidently is genuine evidence the model is on track.
    /// </summary>
    public IReadOnlySet<int> UnscoredTokenIds { get; init; } = new HashSet<int>();
}

/// <summary>
/// The outcome of one decode: the emitted token ids (start token and terminating end-of-sequence token
/// removed), the sequence confidence, and whether generation stopped because it ran into
/// <see cref="TrOcrSearchOptions.MaxTokens"/> rather than because the model chose to stop.
/// </summary>
internal readonly record struct TrOcrGeneration(IReadOnlyList<int> TokenIds, double Confidence, bool HitTokenLimit);

/// <summary>
/// Greedy and beam-search decoding for an autoregressive decoder, expressed over an opaque state object
/// so it works with and without key/value caching — and so it can be tested against a synthetic logits
/// sequence with no model at all.
/// <para>
/// The confidence both searches report is <c>exp((1/n)·Σ ln p(tokenᵢ))</c> over the emitted, text-bearing
/// tokens: the geometric mean of their probabilities. It stays in 0–1, does not shrink with length the
/// way a raw sequence probability does, and is directly comparable between two readings of the same
/// crop — which is what beam search needs in order to pick one.
/// </para>
/// </summary>
internal static class TrOcrSearch
{
    /// <summary>
    /// Decodes with the strategy <paramref name="options"/> selects: greedy when
    /// <see cref="TrOcrSearchOptions.BeamWidth"/> is 1 or less, beam search otherwise.
    /// </summary>
    /// <typeparam name="TState">Per-hypothesis decoder state (the key/value cache, or nothing at all).</typeparam>
    /// <param name="options">Start/stop tokens, limits and the ranking policy.</param>
    /// <param name="createState">Creates the state a fresh hypothesis starts from.</param>
    /// <param name="cloneState">Forks a state when a hypothesis branches. Never called for greedy.</param>
    /// <param name="nextLogits">
    /// Advances one hypothesis: given its state and the tokens generated so far (starting with
    /// <see cref="TrOcrSearchOptions.StartTokenId"/>), returns the raw logits for the next position.
    /// </param>
    public static TrOcrGeneration Run<TState>(
        TrOcrSearchOptions options,
        Func<TState> createState,
        Func<TState, TState> cloneState,
        Func<TState, IReadOnlyList<int>, float[]> nextLogits)
        => options.BeamWidth <= 1
            ? Greedy(options, createState, nextLogits)
            : Beam(options, createState, cloneState, nextLogits);

    /// <summary>
    /// Takes the highest-scoring token at every step and stops at the first end-of-sequence token — one
    /// decoder run per generated token, and the fastest option by a wide margin.
    /// </summary>
    public static TrOcrGeneration Greedy<TState>(
        TrOcrSearchOptions options,
        Func<TState> createState,
        Func<TState, IReadOnlyList<int>, float[]> nextLogits)
    {
        var state = createState();
        var tokens = new List<int>(options.MaxTokens + 1) { options.StartTokenId };
        var emitted = new List<int>(options.MaxTokens);
        double logProbSum = 0;
        int scored = 0;
        bool hitLimit = true;

        for (int step = 0; step < options.MaxTokens; step++)
        {
            var logits = nextLogits(state, tokens);
            if (logits.Length == 0) { hitLimit = false; break; }

            int best = ArgMax(logits);
            if (best == options.EndOfSequenceTokenId) { hitLimit = false; break; }

            if (!options.UnscoredTokenIds.Contains(best))
            {
                logProbSum += LogSoftmaxAt(logits, best);
                scored++;
            }
            tokens.Add(best);
            emitted.Add(best);
        }

        return new TrOcrGeneration(emitted, Confidence(logProbSum, scored), hitLimit);
    }

    /// <summary>
    /// Keeps <see cref="TrOcrSearchOptions.BeamWidth"/> hypotheses alive, extends each by its most likely
    /// continuations, and keeps the best few by length-normalized log-probability
    /// (<c>Σ ln p / lengthᵖ</c>). Finished hypotheses stay in the pool and compete on that same score, so
    /// a short confident reading can still beat a long uncertain one. The winner is the best finished
    /// hypothesis, or the best unfinished one if the token limit cut everything short.
    /// </summary>
    public static TrOcrGeneration Beam<TState>(
        TrOcrSearchOptions options,
        Func<TState> createState,
        Func<TState, TState> cloneState,
        Func<TState, IReadOnlyList<int>, float[]> nextLogits)
    {
        int width = Math.Max(1, options.BeamWidth);
        var beams = new List<Hypothesis<TState>> { Hypothesis<TState>.Start(createState(), options.StartTokenId) };

        for (int step = 0; step < options.MaxTokens; step++)
        {
            if (beams.All(b => b.Finished)) break;

            var candidates = new List<Candidate<TState>>(beams.Count * width);
            foreach (var beam in beams)
            {
                if (beam.Finished)
                {
                    candidates.Add(new Candidate<TState>(beam, TokenId: -1, LogProb: 0, Score: beam.Score(options.LengthPenalty)));
                    continue;
                }

                var logits = nextLogits(beam.State, beam.Tokens);
                if (logits.Length == 0)
                {
                    beam.Finished = true;
                    candidates.Add(new Candidate<TState>(beam, TokenId: -1, LogProb: 0, Score: beam.Score(options.LengthPenalty)));
                    continue;
                }

                foreach (var (tokenId, logProb) in TopK(logits, width))
                {
                    double score = Normalize(beam.LogProbTotal + logProb, beam.Length + 1, options.LengthPenalty);
                    candidates.Add(new Candidate<TState>(beam, tokenId, logProb, score));
                }
            }

            candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));
            if (candidates.Count > width) candidates.RemoveRange(width, candidates.Count - width);

            var next = new List<Hypothesis<TState>>(candidates.Count);
            var reusedParents = new HashSet<Hypothesis<TState>>();
            foreach (var candidate in candidates)
            {
                var parent = candidate.Parent;
                if (candidate.TokenId < 0)
                {
                    // A finished hypothesis carries over untouched; its state is never advanced again.
                    reusedParents.Add(parent);
                    next.Add(parent);
                    continue;
                }

                // The first survivor of a parent inherits its state; further survivors need a fork.
                var state = reusedParents.Add(parent) ? parent.State : cloneState(parent.State);
                next.Add(parent.Extend(state, candidate.TokenId, candidate.LogProb, options));
            }
            beams = next;
        }

        var winner = beams.Where(b => b.Finished).OrderByDescending(b => b.Score(options.LengthPenalty)).FirstOrDefault()
            ?? beams.OrderByDescending(b => b.Score(options.LengthPenalty)).First();

        return new TrOcrGeneration(winner.Emitted, Confidence(winner.ScoredLogProbSum, winner.ScoredCount), !winner.Finished);
    }

    private static double Confidence(double logProbSum, int scored)
        => scored > 0 ? Math.Clamp(Math.Exp(logProbSum / scored), 0.0, 1.0) : 0.0;

    private static double Normalize(double logProbTotal, int length, double lengthPenalty)
        => length <= 0 ? logProbTotal : logProbTotal / Math.Pow(length, lengthPenalty);

    private static int ArgMax(float[] logits)
    {
        int best = 0;
        float max = logits[0];
        for (int i = 1; i < logits.Length; i++)
        {
            if (logits[i] > max) { max = logits[i]; best = i; }
        }
        return best;
    }

    /// <summary>Log-softmax of one class, computed in the numerically stable max-subtracted form.</summary>
    private static double LogSoftmaxAt(float[] logits, int index)
    {
        float max = logits[0];
        for (int i = 1; i < logits.Length; i++) if (logits[i] > max) max = logits[i];

        double sumExp = 0;
        for (int i = 0; i < logits.Length; i++) sumExp += Math.Exp(logits[i] - max);

        return logits[index] - max - Math.Log(sumExp);
    }

    /// <summary>
    /// The <paramref name="k"/> highest-scoring classes with their log-probabilities. Selection is a
    /// single pass with an insertion into a k-sized buffer — vocabularies run to 50k entries, so sorting
    /// the whole row every step would dominate the decode.
    /// </summary>
    private static (int TokenId, double LogProb)[] TopK(float[] logits, int k)
    {
        k = Math.Min(k, logits.Length);
        var ids = new int[k];
        var values = new float[k];
        int filled = 0;

        for (int i = 0; i < logits.Length; i++)
        {
            float value = logits[i];
            if (filled == k && value <= values[filled - 1]) continue;

            int position = filled < k ? filled : k - 1;
            while (position > 0 && values[position - 1] < value)
            {
                values[position] = values[position - 1];
                ids[position] = ids[position - 1];
                position--;
            }
            values[position] = value;
            ids[position] = i;
            if (filled < k) filled++;
        }

        float max = values[0];
        double sumExp = 0;
        for (int i = 0; i < logits.Length; i++) sumExp += Math.Exp(logits[i] - max);
        double logZ = max + Math.Log(sumExp);

        var result = new (int, double)[filled];
        for (int i = 0; i < filled; i++) result[i] = (ids[i], values[i] - logZ);
        return result;
    }

    /// <summary>One hypothesis in the beam: its tokens, its two running log-probability sums, and its state.</summary>
    private sealed class Hypothesis<TState>
    {
        private Hypothesis(TState state, List<int> tokens, List<int> emitted)
        {
            State = state;
            Tokens = tokens;
            Emitted = emitted;
        }

        public TState State { get; }

        /// <summary>Everything fed to the decoder, starting with the start token.</summary>
        public List<int> Tokens { get; }

        /// <summary>The text-bearing prefix reported to the caller (no start token, no end-of-sequence).</summary>
        public List<int> Emitted { get; }

        /// <summary>Σ ln p over every generated token — the ranking score before length normalization.</summary>
        public double LogProbTotal { get; private init; }

        /// <summary>Σ ln p over the tokens that count toward confidence.</summary>
        public double ScoredLogProbSum { get; private init; }

        /// <summary>How many tokens contributed to <see cref="ScoredLogProbSum"/>.</summary>
        public int ScoredCount { get; private init; }

        /// <summary>Generated token count, including a terminating end-of-sequence token.</summary>
        public int Length { get; private init; }

        /// <summary>True once the hypothesis has predicted end-of-sequence.</summary>
        public bool Finished { get; set; }

        public static Hypothesis<TState> Start(TState state, int startTokenId)
            => new(state, new List<int> { startTokenId }, new List<int>());

        public double Score(double lengthPenalty) => Normalize(LogProbTotal, Length, lengthPenalty);

        public Hypothesis<TState> Extend(TState state, int tokenId, double logProb, TrOcrSearchOptions options)
        {
            bool terminates = tokenId == options.EndOfSequenceTokenId;
            var tokens = new List<int>(Tokens.Count + 1);
            tokens.AddRange(Tokens);
            tokens.Add(tokenId);

            var emitted = new List<int>(Emitted.Count + 1);
            emitted.AddRange(Emitted);
            if (!terminates) emitted.Add(tokenId);

            bool scores = !terminates && !options.UnscoredTokenIds.Contains(tokenId);
            return new Hypothesis<TState>(state, tokens, emitted)
            {
                LogProbTotal = LogProbTotal + logProb,
                ScoredLogProbSum = ScoredLogProbSum + (scores ? logProb : 0),
                ScoredCount = ScoredCount + (scores ? 1 : 0),
                Length = Length + 1,
                Finished = terminates,
            };
        }
    }

    /// <summary>A proposed extension of one hypothesis, ranked by its length-normalized score.</summary>
    private readonly record struct Candidate<TState>(Hypothesis<TState> Parent, int TokenId, double LogProb, double Score);
}
