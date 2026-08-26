namespace EasyOcrSharp.Models;

/// <summary>
/// Enables and configures handwriting recognition with a locally exported <b>TrOCR</b> model — a ViT
/// encoder plus an autoregressive text decoder, the architecture that reads cursive and printed
/// handwriting that EasyOCR's CRNN recognizers cannot. Assign an instance to
/// <see cref="Services.EasyOcrServiceOptions.Handwriting"/> (null, the default, leaves the feature off
/// and changes nothing) and then call
/// <see cref="Services.EasyOcrService.RecognizeHandwritingAsync(EasyImageSharp.Image{EasyImageSharp.PixelFormats.Rgb24}, RecognitionOptions?, System.Threading.CancellationToken)"/>.
/// <para>
/// The TrOCR weights are <b>not</b> hosted alongside EasyOcrSharp's own models, so every path below is
/// supplied by you and loaded straight from disk — nothing is downloaded, exactly like
/// <see cref="Services.CustomRecognizer"/>. Produce the three files with
/// <c>tools/export_trocr_onnx.py</c>, which exports <c>encoder_model.onnx</c>,
/// <c>decoder_model.onnx</c> and the tokenizer sidecar from any HuggingFace TrOCR checkpoint
/// (e.g. <c>microsoft/trocr-base-handwritten</c>), then point <see cref="FromDirectory"/> at its output.
/// </para>
/// </summary>
/// <remarks>
/// Every numeric default matches the published <c>microsoft/trocr-*-handwritten</c> configuration:
/// 384×384 RGB input normalized with mean/std 0.5, a RoBERTa byte-level BPE vocabulary whose
/// <c>&lt;/s&gt;</c> token (id 2) is both the decoder's start token and its end-of-sequence token, and
/// <c>&lt;pad&gt;</c> at id 1. Override them only when your checkpoint genuinely differs.
/// </remarks>
public sealed record HandwritingOptions
{
    /// <summary>Conventional file name of the exported ViT encoder inside a model directory.</summary>
    public const string DefaultEncoderFileName = "encoder_model.onnx";

    /// <summary>Conventional file name of the exported text decoder inside a model directory.</summary>
    public const string DefaultDecoderFileName = "decoder_model.onnx";

    /// <summary>Conventional file name of the tokenizer vocabulary sidecar inside a model directory.</summary>
    public const string DefaultTokenizerFileName = "vocab.json";

    /// <summary>
    /// Path to the exported TrOCR <b>encoder</b> ONNX file. Its single image input is a
    /// <c>[1, 3, <see cref="ImageSize"/>, <see cref="ImageSize"/>]</c> float tensor and its output is the
    /// ViT patch-embedding sequence handed to the decoder.
    /// <para>
    /// <b>Null (the default) downloads the hosted model</b> into the shared model cache on first use,
    /// exactly like the printed-text recognizers — see <see cref="Quantize"/>. Set it to use your own
    /// export instead, in which case nothing is downloaded.
    /// </para>
    /// </summary>
    public string? EncoderModelPath { get; init; }

    /// <summary>
    /// Path to the exported TrOCR <b>decoder</b> ONNX file. Both flavours optimum emits are supported and
    /// detected from the graph's input metadata: a plain decoder (the whole prefix is re-fed each step)
    /// and a decoder with <c>past_key_values</c> caching (only the newest token is fed, which is markedly
    /// faster on long lines). Null (the default) downloads the hosted model.
    /// </summary>
    public string? DecoderModelPath { get; init; }

    /// <summary>
    /// Path to the tokenizer sidecar used to turn generated token ids back into text. Accepts a
    /// HuggingFace <c>vocab.json</c> (a token → id object), a full <c>tokenizer.json</c> (the
    /// <c>model.vocab</c> object is read), or a plain JSON array of tokens in id order. Only decoding is
    /// needed, so <c>merges.txt</c> is not required. Null (the default) downloads the hosted vocabulary.
    /// </summary>
    public string? TokenizerPath { get; init; }

    /// <summary>
    /// Use the int8-quantized hosted weights (the default) rather than full precision. The quantized
    /// pair is roughly a quarter of the size — a much friendlier first-use download — and reads ordinary
    /// text just as accurately. Mirrors <see cref="Services.EasyOcrServiceOptions.Quantize"/> for the
    /// printed-text packs.
    /// <para>
    /// Ignored when <see cref="EncoderModelPath"/> and <see cref="DecoderModelPath"/> are set: your own
    /// files are used exactly as given.
    /// </para>
    /// </summary>
    public bool Quantize { get; init; } = true;

    /// <summary>
    /// Whether every model file was supplied by the caller, so no download is required. False when any
    /// path is null, which routes that file through the shared model cache.
    /// </summary>
    public bool IsFullyLocal =>
        !string.IsNullOrWhiteSpace(EncoderModelPath)
        && !string.IsNullOrWhiteSpace(DecoderModelPath)
        && !string.IsNullOrWhiteSpace(TokenizerPath);

    /// <summary>
    /// Handwriting using the hosted TrOCR weights: nothing to download by hand, nothing to configure.
    /// The models are fetched into the model cache on the first handwriting call.
    /// </summary>
    public static HandwritingOptions Default { get; } = new();

    /// <summary>
    /// Side length (px) of the square RGB image fed to the encoder. TrOCR's processor uses 384; change
    /// this only for a checkpoint exported at another resolution. Crops are stretched (not letterboxed)
    /// to this size, matching the reference <c>ViTImageProcessor</c>.
    /// </summary>
    public int ImageSize { get; init; } = 384;

    /// <summary>
    /// Per-channel mean subtracted after scaling pixels to the 0–1 range. TrOCR uses 0.5 on all three
    /// channels, so the normalized input lands in [-1, 1].
    /// </summary>
    public float NormalizationMean { get; init; } = 0.5f;

    /// <summary>
    /// Per-channel standard deviation the mean-centred pixels are divided by. TrOCR uses 0.5. Must be
    /// non-zero.
    /// </summary>
    public float NormalizationStd { get; init; } = 0.5f;

    /// <summary>
    /// Hard cap on the number of tokens generated for one region. A degenerate or mismatched decoder
    /// that never predicts end-of-sequence stops here instead of looping forever, so this is a safety
    /// bound as much as a quality knob. Default 64, comfortably more than a handwritten line needs.
    /// </summary>
    public int MaxTokens { get; init; } = 64;

    /// <summary>
    /// Number of hypotheses kept by the decoder search. 1 (the default) is plain greedy decoding — one
    /// decoder run per token. Larger values run beam search, costing roughly <c>BeamWidth</c>× the
    /// decoder runs for a modest accuracy gain on messy handwriting.
    /// </summary>
    public int BeamWidth { get; init; } = 1;

    /// <summary>
    /// Exponent applied to the hypothesis length when beam search normalizes a sequence's log-probability
    /// (<c>score = Σ log p / lengthᵖ</c>). 1.0 (default) is plain per-token averaging; values above 1
    /// favour longer readings, below 1 shorter ones. Ignored when <see cref="BeamWidth"/> is 1.
    /// </summary>
    public double LengthPenalty { get; init; } = 1.0;

    /// <summary>
    /// Token id the decoder is primed with before it predicts anything. TrOCR uses <c>&lt;/s&gt;</c>
    /// (id 2) — the model then emits <c>&lt;s&gt;</c>, the text, and <c>&lt;/s&gt;</c> again.
    /// </summary>
    public int DecoderStartTokenId { get; init; } = 2;

    /// <summary>
    /// Token id that terminates generation (<c>&lt;/s&gt;</c>, id 2, for TrOCR's RoBERTa vocabulary).
    /// </summary>
    public int EndOfSequenceTokenId { get; init; } = 2;

    /// <summary>Padding token id (<c>&lt;pad&gt;</c>, id 1, for TrOCR). Never emitted into the text.</summary>
    public int PadTokenId { get; init; } = 1;

    /// <summary>
    /// How many detected regions are decoded concurrently. Default 1: TrOCR is a large transformer whose
    /// single run already saturates a CPU, so overlapping regions usually costs more than it gains. Raise
    /// it on a GPU or a many-core server.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = 1;

    /// <summary>
    /// Language code reported in <see cref="OcrResult.Languages"/> for handwriting results. Purely
    /// descriptive metadata — the script a TrOCR checkpoint can read is fixed by its weights, not chosen
    /// per call. Defaults to <c>"en"</c>, matching the published English handwriting checkpoints.
    /// </summary>
    public string Language { get; init; } = "en";

    /// <summary>
    /// Builds options for a model directory laid out the way <c>tools/export_trocr_onnx.py</c> writes it:
    /// <see cref="DefaultEncoderFileName"/>, <see cref="DefaultDecoderFileName"/> and
    /// <see cref="DefaultTokenizerFileName"/> (falling back to <c>tokenizer.json</c> when no
    /// <c>vocab.json</c> is present). Every other setting keeps its default; use a <c>with</c> expression
    /// to adjust one.
    /// </summary>
    /// <param name="directory">Directory holding the exported encoder, decoder and tokenizer sidecar.</param>
    /// <returns>Options pointing at the three files inside <paramref name="directory"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is null or blank.</exception>
    public static HandwritingOptions FromDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A model directory must be provided.", nameof(directory));

        var root = Path.GetFullPath(directory);
        var vocab = Path.Combine(root, DefaultTokenizerFileName);
        if (!File.Exists(vocab))
        {
            var tokenizerJson = Path.Combine(root, "tokenizer.json");
            if (File.Exists(tokenizerJson)) vocab = tokenizerJson;
        }

        return new HandwritingOptions
        {
            EncoderModelPath = Path.Combine(root, DefaultEncoderFileName),
            DecoderModelPath = Path.Combine(root, DefaultDecoderFileName),
            TokenizerPath = vocab,
        };
    }

    /// <summary>
    /// Validates the settings that would otherwise fail deep inside ONNX Runtime with an opaque message,
    /// and confirms the three files exist. Called once when the recognizer is first loaded.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A numeric setting is outside its usable range.</exception>
    /// <exception cref="FileNotFoundException">An encoder, decoder or tokenizer file is missing.</exception>
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ImageSize, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTokens, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(BeamWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDegreeOfParallelism, 1);
        if (NormalizationStd == 0f)
            throw new ArgumentOutOfRangeException(nameof(NormalizationStd), "Normalization standard deviation must be non-zero.");

        RequireFile(EncoderModelPath, "encoder");
        RequireFile(DecoderModelPath, "decoder");
        RequireFile(TokenizerPath, "tokenizer");

        static void RequireFile(string? path, string role)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Handwriting {role} file '{path}' was not found. Leave the path null to download the " +
                    "hosted TrOCR model, or point it at your own export (see tools/export_trocr_onnx.py).",
                    path);
            }
        }
    }

    /// <summary>
    /// The encoder path once resolution has run. Every path is non-null by the time the recognizer is
    /// built — <c>EasyOcrService</c> fills in whatever the caller left null from the model cache — so
    /// this asserts that invariant instead of scattering null-forgiving operators through the runtime.
    /// </summary>
    internal string ResolvedEncoderPath => Resolved(EncoderModelPath, nameof(EncoderModelPath));

    /// <summary>The decoder path once resolution has run. See <see cref="ResolvedEncoderPath"/>.</summary>
    internal string ResolvedDecoderPath => Resolved(DecoderModelPath, nameof(DecoderModelPath));

    /// <summary>The tokenizer path once resolution has run. See <see cref="ResolvedEncoderPath"/>.</summary>
    internal string ResolvedTokenizerPath => Resolved(TokenizerPath, nameof(TokenizerPath));

    private static string Resolved(string? path, string name)
        => path ?? throw new InvalidOperationException(
            $"HandwritingOptions.{name} was still null when the recognizer was built. The hosted model " +
            "should have been resolved before this point; this is a bug in EasyOcrSharp.");
}
