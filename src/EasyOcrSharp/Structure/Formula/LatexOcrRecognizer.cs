using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Structure.Formula;

/// <summary>
/// LaTeX-OCR (RapidLaTeXOCR) formula recognizer: an image-to-sequence (ViT encoder + autoregressive
/// transformer decoder) ONNX model. Optionally normalizes the crop's size with an <see cref="_imageResizer"/>
/// model, encodes the crop with <see cref="_encoder"/>, then greedily decodes tokens with
/// <see cref="_decoder"/>, detokenizing through <see cref="_vocab"/> into a LaTeX string. Owns and disposes
/// all sessions.
/// <para>
/// Ported from RapidLaTeXOCR (MIT, <c>github.com/RapidAI/RapidLaTeXOCR</c>: <c>main.py</c>, <c>models.py</c>,
/// <c>utils.py</c>). This is a <b>split</b> encoder/decoder transformer, <b>not</b> PP-FormulaNet (which has
/// no ONNX export path).
/// </para>
/// <para>
/// Pre-processing (matching RapidLaTeXOCR's <c>PreProcess</c>): the crop is converted to grayscale,
/// thresholded at 128, auto-inverted so text is dark on a light background, cropped to its content bounding
/// box, and padded to the next multiple of 32 on a white (255) canvas. A side exceeding the maximum
/// (672×192) is scaled down with bilinear sampling; a degenerate crop is grown to at least 32×32. Pixels are
/// z-score normalized with mean=0.7931 / std=0.1738 over <c>x/255</c> — i.e.
/// <c>(x − mean·255)·(1/(std·255))</c> — into a single-channel <c>[1, 1, H, W]</c> float tensor. When an
/// <see cref="_imageResizer"/> session is supplied, an argmax-bucketing loop refines the padded width
/// (<c>width = (argmax + 1)·32</c>, up to 10 iterations); otherwise the crop is simply padded to its nearest
/// 32-px bucket.
/// </para>
/// <para>
/// Decoding has <b>no KV-cache</b>: the full token sequence is re-fed to the decoder every step. Starting
/// from <c>[BOS=1]</c>, each step runs the decoder with its three declared inputs in declared order:
/// <c>x</c> (the int64 token sequence <c>[1,len]</c>), <c>mask</c> (an all-true boolean attention mask
/// <c>[1,len]</c>), and <c>context</c> (the cached encoder output <c>[1, Nvis, 256]</c>); it yields logits
/// <c>[1, len, 8000]</c>, takes the last position's logits, greedily picks the argmax token over the real
/// vocabulary (the 8000-wide logits exceed the ~1175-token vocab), and appends it. The loop stops at
/// <c>EOS=2</c> or after 512 tokens. The token ids are then detokenized via <see cref="_vocab"/> (a decode-only
/// id → string map): BOS is skipped, BPE word-boundary markers (<c>"Ġ"</c>) become spaces, the special
/// <c>[BOS]</c>/<c>[EOS]</c>/<c>[PAD]</c> tokens are stripped, and surrounding whitespace is collapsed.
/// </para>
/// </summary>
internal sealed class LatexOcrRecognizer : IFormulaRecognizer
{
    // --- Pre-processing constants (RapidLaTeXOCR PreProcess) ---

    /// <summary>
    /// Per-pixel z-score mean applied over <c>x/255</c>.
    /// </summary>
    private const float NormMean = 0.7931f;

    /// <summary>
    /// Per-pixel z-score standard deviation applied over <c>x/255</c>.
    /// </summary>
    private const float NormStd = 0.1738f;

    /// <summary>
    /// Binarization threshold (8-bit) used to find the content bounding box and to auto-invert.
    /// </summary>
    private const int BinaryThreshold = 128;

    /// <summary>
    /// All padded image dimensions are rounded up to a multiple of this (the ViT patch grid stride).
    /// </summary>
    private const int SizeMultiple = 32;

    /// <summary>
    /// Minimum padded width/height; degenerate crops are grown to at least this.
    /// </summary>
    private const int MinSide = 32;

    /// <summary>
    /// Maximum padded width before the crop is scaled down with bilinear sampling.
    /// </summary>
    private const int MaxWidth = 672;

    /// <summary>
    /// Maximum padded height before the crop is scaled down with bilinear sampling.
    /// </summary>
    private const int MaxHeight = 192;

    // --- Decoder special token ids (RapidLaTeXOCR) ---

    /// <summary>
    /// Begin-of-sequence token id that seeds the decode loop.
    /// </summary>
    private const int BosToken = 1;

    /// <summary>
    /// End-of-sequence token id that terminates the decode loop.
    /// </summary>
    private const int EosToken = 2;

    /// <summary>
    /// Hard cap on the number of decoded tokens.
    /// </summary>
    private const int MaxSeqLen = 512;

    /// <summary>
    /// Maximum number of image-resizer bucketing iterations.
    /// </summary>
    private const int MaxResizerIterations = 10;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly InferenceSession? _imageResizer;
    private readonly IReadOnlyDictionary<int, string> _vocab;

    /// <summary>
    /// Encoder input tensor name (resolved once from the model's metadata).
    /// </summary>
    private readonly string _encoderInputName;

    /// <summary>
    /// Decoder input tensor names in the model's declared order. For this re-hosted ONNX they are
    /// <c>["x", "mask", "context"]</c> — i.e. [int64 tokens, bool mask, float encoder context].
    /// </summary>
    private readonly string[] _decoderInputNames;

    /// <summary>
    /// Image-resizer input tensor name (null when no resizer session was supplied).
    /// </summary>
    private readonly string? _resizerInputName;

    /// <summary>
    /// Creates the recognizer over the LaTeX-OCR encoder/decoder sessions, an optional image-resizer
    /// session, and the token-id → token vocabulary (from <c>tokenizer.json</c>).
    /// </summary>
    /// <param name="encoder">The ViT image-encoder session (this instance takes ownership).</param>
    /// <param name="decoder">The autoregressive token-decoder session (this instance takes ownership).</param>
    /// <param name="imageResizer">Optional pre-encode image-resizer session (this instance takes ownership when non-null).</param>
    /// <param name="vocab">Token-id → token string map used to detokenize the decoded sequence.</param>
    public LatexOcrRecognizer(
        InferenceSession encoder,
        InferenceSession decoder,
        InferenceSession? imageResizer,
        IReadOnlyDictionary<int, string> vocab)
    {
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _imageResizer = imageResizer;
        _vocab = vocab ?? throw new ArgumentNullException(nameof(vocab));

        // Resolve input names once. The decoder's three inputs are passed in the model's declared order,
        // which this re-hosted ONNX exports as ["x" (tokens), "mask", "context"]; we honour whatever names
        // and order the graph actually reports rather than hard-coding them.
        _encoderInputName = _encoder.InputMetadata.Keys.First();
        _decoderInputNames = _decoder.InputMetadata.Keys.ToArray();
        _resizerInputName = _imageResizer?.InputMetadata.Keys.First();
    }

    /// <inheritdoc />
    /// <remarks>
    /// (Optional) resize-bucket → encode crop once → autoregressive greedy decode (no KV-cache) →
    /// detokenize to a LaTeX string. See the type-level docs for the full pipeline.
    /// </remarks>
    public string RecognizeLatex(Image<Rgb24> crop)
    {
        ArgumentNullException.ThrowIfNull(crop);

        // PREPROCESS → single-channel [1,1,H,W] float tensor.
        var imageTensor = Preprocess(crop);

        // ENCODE ONCE → context [1, Nvis, dim]. Cached and re-used for every decode step.
        var encoderInputs = new[] { NamedOnnxValue.CreateFromTensor(_encoderInputName, imageTensor) };
        using var encoderOutputs = _encoder.Run(encoderInputs);
        var context = encoderOutputs.First().AsTensor<float>();
        // Materialize the context once into an owning DenseTensor so it survives past the encoder's
        // disposable results and can be re-fed to the decoder each step.
        var contextTensor = new DenseTensor<float>(context.ToArray(), context.Dimensions.ToArray());

        // DECODE LOOP — re-feed the full token sequence every step (the exported decoder is stateless).
        var tokens = new List<long>(MaxSeqLen) { BosToken };
        for (int step = 0; step < MaxSeqLen; step++)
        {
            int len = tokens.Count;

            // tokens: int64[1, len]
            var tokenTensor = new DenseTensor<long>(new[] { 1, len });
            for (int i = 0; i < len; i++) tokenTensor[0, i] = tokens[i];

            // mask: bool[1, len], all true (no padding within a single-sequence batch).
            var maskTensor = new DenseTensor<bool>(new[] { 1, len });
            maskTensor.Fill(true);

            // Bind the three inputs by the decoder's declared names so we match its [tokens, mask, context]
            // order regardless of the concrete names the exporter chose.
            var decoderInputs = new NamedOnnxValue[]
            {
                NamedOnnxValue.CreateFromTensor(_decoderInputNames[0], tokenTensor),
                NamedOnnxValue.CreateFromTensor(_decoderInputNames[1], maskTensor),
                NamedOnnxValue.CreateFromTensor(_decoderInputNames[2], contextTensor),
            };

            using var decoderOutputs = _decoder.Run(decoderInputs);
            var logits = decoderOutputs.First().AsTensor<float>();

            // logits: [1, len, 8000]; greedily pick the argmax over the LAST position's distribution.
            // The exported decoder's logit width (8000) is wider than the tokenizer's vocabulary
            // (_vocab.Count, ~1175); the trailing columns are unused padding whose logits are
            // unconstrained. Restrict the argmax to real vocabulary ids so a spurious high value in the
            // padding region can never produce an id that has no token (which detokenizing would drop).
            var dims = logits.Dimensions;
            int logitWidth = dims[dims.Length - 1];
            int vocabSize = Math.Min(logitWidth, _vocab.Count);
            ReadOnlySpan<float> flat = logits.ToArray();
            int lastRowBase = (len - 1) * logitWidth;

            int best = 0;
            float bestScore = float.NegativeInfinity;
            for (int c = 0; c < vocabSize; c++)
            {
                float v = flat[lastRowBase + c];
                if (v > bestScore)
                {
                    bestScore = v;
                    best = c;
                }
            }

            if (best == EosToken) break;
            tokens.Add(best);
        }

        // DETOKENIZE the decoded ids (skipping the seeded BOS) into a LaTeX string.
        return Detokenize(tokens);
    }

    /// <summary>
    /// Converts <paramref name="crop"/> into the model's single-channel <c>[1, 1, H, W]</c> input tensor:
    /// grayscale → threshold@128 → auto-invert to dark-on-light → crop to content bbox → bilinear down-scale
    /// if a side exceeds the 672×192 maximum → pad to the next multiple of 32 on a white (255) canvas
    /// (refined by the image-resizer bucketing loop when available) → z-score normalize.
    /// </summary>
    private DenseTensor<float> Preprocess(Image<Rgb24> crop)
    {
        // 1) Grayscale luminance + binary mask (text vs. background) at the 128 threshold.
        int srcW = crop.Width;
        int srcH = crop.Height;
        var gray = new byte[srcH * srcW];
        long darkCount = 0; // pixels below the threshold (candidate "ink").

        crop.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < srcH; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);
                int rowBase = y * srcW;
                for (int x = 0; x < srcW; x++)
                {
                    Rgb24 px = row[x];
                    // ITU-R BT.601 luma (matches OpenCV/PIL "L" conversion used by RapidLaTeXOCR).
                    int lum = (px.R * 299 + px.G * 587 + px.B * 114) / 1000;
                    if (lum > 255) lum = 255;
                    byte g = (byte)lum;
                    gray[rowBase + x] = g;
                    if (g < BinaryThreshold) darkCount++;
                }
            }
        });

        // 2) Auto-invert so the foreground (text) is DARK on a LIGHT background. If most pixels are dark,
        //    the image is light-on-dark and is inverted.
        long total = (long)srcW * srcH;
        bool invert = darkCount * 2 > total;
        if (invert)
        {
            for (int i = 0; i < gray.Length; i++) gray[i] = (byte)(255 - gray[i]);
        }

        // 3) Crop to the content bounding box: the extent of all "ink" (sub-threshold) pixels after
        //    normalization to dark-on-light.
        int minX = srcW, minY = srcH, maxX = -1, maxY = -1;
        for (int y = 0; y < srcH; y++)
        {
            int rowBase = y * srcW;
            for (int x = 0; x < srcW; x++)
            {
                if (gray[rowBase + x] < BinaryThreshold)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        // No ink found (blank crop) — fall back to the whole frame so we still emit a valid tensor.
        if (maxX < minX || maxY < minY)
        {
            minX = 0;
            minY = 0;
            maxX = srcW - 1;
            maxY = srcH - 1;
        }

        int boxW = maxX - minX + 1;
        int boxH = maxY - minY + 1;

        // Build a grayscale image of just the content box on a white background for resampling/padding.
        using var content = new Image<L8>(boxW, boxH);
        content.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < boxH; y++)
            {
                Span<L8> row = accessor.GetRowSpan(y);
                int srcRowBase = (minY + y) * srcW + minX;
                for (int x = 0; x < boxW; x++)
                {
                    row[x] = new L8(gray[srcRowBase + x]);
                }
            }
        });

        // 4) If a side exceeds the maximum, scale DOWN (bilinear) preserving aspect ratio.
        int curW = boxW;
        int curH = boxH;
        if (curW > MaxWidth || curH > MaxHeight)
        {
            double scale = Math.Min(MaxWidth / (double)curW, MaxHeight / (double)curH);
            curW = Math.Max(1, (int)Math.Round(curW * scale));
            curH = Math.Max(1, (int)Math.Round(curH * scale));
            content.Mutate(c => c.Resize(new ResizeOptions
            {
                Size = new Size(curW, curH),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Triangle, // bilinear
            }));
        }

        // 5) Choose the padded canvas size. Default: pad each side up to the next multiple of 32 (min 32).
        int padW = PadTo(curW, SizeMultiple);
        int padH = PadTo(curH, SizeMultiple);

        // Optional image-resizer bucketing: iteratively pick the target width from the resizer's argmax.
        if (_imageResizer is not null && _resizerInputName is not null)
        {
            padW = ResolveResizerWidth(content, curW, curH, padH);
        }

        if (padW < MinSide) padW = MinSide;
        if (padH < MinSide) padH = MinSide;

        // 6) Compose the (down-scaled) content onto a white canvas of the padded size (top-left anchored).
        using var canvas = new Image<L8>(padW, padH, new L8(255));
        canvas.Mutate(c => c.DrawImage(content, new Point(0, 0), 1f));

        // 7) Z-score normalize over x/255: img = (x - mean*255) * (1/(std*255)). Single-channel [1,1,H,W].
        const float invStdScaled = 1f / (NormStd * 255f);
        const float meanScaled = NormMean * 255f;
        var buffer = new float[padH * padW];
        canvas.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < padH; y++)
            {
                Span<L8> row = accessor.GetRowSpan(y);
                int rowBase = y * padW;
                for (int x = 0; x < padW; x++)
                {
                    buffer[rowBase + x] = (row[x].PackedValue - meanScaled) * invStdScaled;
                }
            }
        });

        return new DenseTensor<float>(buffer, new[] { 1, 1, padH, padW });
    }

    /// <summary>
    /// Runs RapidLaTeXOCR's image-resizer bucketing loop: pads the content to the candidate width, asks the
    /// resizer for the best width bucket (<c>width = (argmax + 1)·32</c>), and repeats (up to
    /// <see cref="MaxResizerIterations"/>) until the width stabilizes. Returns the resolved padded width.
    /// </summary>
    private int ResolveResizerWidth(Image<L8> content, int contentW, int contentH, int padH)
    {
        int width = PadTo(contentW, SizeMultiple);
        for (int iter = 0; iter < MaxResizerIterations; iter++)
        {
            int candidateW = Math.Max(MinSide, width);

            // Pad the content onto a white canvas of the candidate size and z-score normalize into the
            // resizer's [1,1,padH,candidateW] input.
            const float invStdScaled = 1f / (NormStd * 255f);
            const float meanScaled = NormMean * 255f;
            var buf = new float[padH * candidateW];
            using (var canvas = new Image<L8>(candidateW, padH, new L8(255)))
            {
                canvas.Mutate(c => c.DrawImage(content, new Point(0, 0), 1f));
                canvas.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < padH; y++)
                    {
                        Span<L8> row = accessor.GetRowSpan(y);
                        int rowBase = y * candidateW;
                        for (int x = 0; x < candidateW; x++)
                            buf[rowBase + x] = (row[x].PackedValue - meanScaled) * invStdScaled;
                    }
                });
            }
            var probe = new DenseTensor<float>(buf, new[] { 1, 1, padH, candidateW });

            int newWidth;
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_resizerInputName!, probe) };
            using (var outputs = _imageResizer!.Run(inputs))
            {
                ReadOnlySpan<float> res = outputs.First().AsTensor<float>().ToArray();
                int argmax = 0;
                float bestScore = float.NegativeInfinity;
                for (int i = 0; i < res.Length; i++)
                {
                    if (res[i] > bestScore)
                    {
                        bestScore = res[i];
                        argmax = i;
                    }
                }
                newWidth = (argmax + 1) * SizeMultiple;
            }

            if (newWidth == candidateW) return candidateW;
            width = newWidth;
        }

        return Math.Max(MinSide, width);
    }

    /// <summary>
    /// Rounds <paramref name="value"/> up to the nearest positive multiple of <paramref name="multiple"/>.
    /// </summary>
    private static int PadTo(int value, int multiple)
    {
        if (value < 1) value = 1;
        int rounded = ((value + multiple - 1) / multiple) * multiple;
        return rounded < multiple ? multiple : rounded;
    }

    /// <summary>
    /// Joins the decoded token ids into a LaTeX string: BOS is skipped, each id is mapped through
    /// <see cref="_vocab"/>, BPE word-boundary markers (<c>"Ġ"</c>) become spaces, leftover special tokens
    /// (<c>[BOS]</c>/<c>[EOS]</c>/<c>[PAD]</c>) are stripped, and runs of whitespace are collapsed to one.
    /// </summary>
    private string Detokenize(IReadOnlyList<long> tokens)
    {
        // Concatenate raw token strings, skipping the seeded BOS at index 0.
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i < tokens.Count; i++)
        {
            if (_vocab.TryGetValue((int)tokens[i], out var piece) && piece is not null)
            {
                sb.Append(piece);
            }
        }

        // BPE convention: "Ġ" marks a word boundary (a leading space); the leading "▁" sentinel maps the
        // same way. Replace both with a literal space.
        string text = sb.ToString()
            .Replace("Ġ", " ")
            .Replace("▁", " ");

        // Strip any special tokens that surfaced as literal text.
        text = text
            .Replace("[BOS]", string.Empty)
            .Replace("[EOS]", string.Empty)
            .Replace("[PAD]", string.Empty);

        // Collapse all whitespace runs to single spaces and trim the ends.
        var outSb = new System.Text.StringBuilder(text.Length);
        bool prevSpace = false;
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace) outSb.Append(' ');
                prevSpace = true;
            }
            else
            {
                outSb.Append(ch);
                prevSpace = false;
            }
        }

        return outSb.ToString().Trim();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
        _imageResizer?.Dispose();
    }
}
