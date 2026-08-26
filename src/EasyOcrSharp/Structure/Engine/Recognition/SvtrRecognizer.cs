using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EasyOcrSharp.Structure.Engine.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Structure.Engine.Recognition;

/// <summary>
/// PaddleOCR text recognizer (SVTR_LCNet / CRNN family). Each crop is resized to a fixed height
/// (typically 48px) keeping aspect ratio, padded to the batch's max width, normalized, run through the
/// ONNX network to per-timestep logits, then CTC-greedy-decoded against the character dictionary via
/// <see cref="CtcDecoder"/>.
/// <para>
/// Preprocessing matches PaddleOCR's <c>resize_norm_img</c>: each crop is scaled to height
/// <see cref="_imageHeight"/> keeping its aspect ratio (so width = round(H · w/h)), the pixels are
/// normalized as <c>(x/255 − 0.5) / 0.5</c> into [−1,1], laid out as a CHW (RGB) float tensor, and
/// right-padded with zeros to the batch's maximum width. The batch overload sorts crops by aspect ratio
/// (width/height) so similarly-shaped lines share a tensor with minimal padding, runs them in chunks of
/// <c>rec_batch_num</c> (default 6), and reorders the results back to the caller's input order.
/// </para>
/// <para>
/// The network output is <c>[N, T, C]</c> (N rows, T timesteps, C = vocab classes). Each row is handed to
/// <see cref="CtcDecoder.GreedyDecode"/> with the Paddle vocab (blank at index 0). The recognizer returns
/// every result regardless of confidence; the engine applies <c>drop_score</c>.
/// </para>
/// </summary>
internal sealed class SvtrRecognizer : ITextRecognizer
{
    /// <summary>
    /// PaddleOCR's default recognition batch size (<c>rec_batch_num</c>).
    /// </summary>
    private const int DefaultBatchSize = 6;

    private readonly InferenceSession _session;
    private readonly IReadOnlyList<string> _dictLines;
    // Built lazily on the first decode, once the model's actual output class count is known, so the vocab
    // length matches the network exactly (community dicts disagree on whether the blank/space are included).
    private IReadOnlyList<string>? _vocab;
    private readonly int _imageHeight;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly int _batchSize;

    // The most recently requested character filter. The recognizer is shared/cached across calls, while
    // Allowlist/Blocklist are per-call options, so the engine sets this immediately before invoking
    // Recognize, or passes options to the options-aware overload. Null = no filtering.
    private IReadOnlyCollection<string>? _allowlist;
    private IReadOnlyCollection<string>? _blocklist;

    /// <summary>
    /// Creates the recognizer over an already-built ONNX <see cref="InferenceSession"/> and a loaded
    /// character dictionary in the Paddle vocab convention (blank at index 0).
    /// </summary>
    /// <param name="session">The loaded recognition model session (this instance takes ownership and disposes it).</param>
    /// <param name="dictLines">
    /// The raw dictionary lines in CTC index order (see <see cref="CharacterDictionary.LoadLines(string)"/>).
    /// The final vocabulary is built on first inference via <see cref="CharacterDictionary.BuildVocab"/> so its
    /// length matches the model's output class count exactly.
    /// </param>
    /// <param name="imageHeight">Fixed recognition input height in pixels (PaddleOCR's <c>rec_image_shape</c> H). Default 48.</param>
    /// <param name="batchSize">Crops per ONNX run (PaddleOCR's <c>rec_batch_num</c>). Default 6.</param>
    public SvtrRecognizer(InferenceSession session, IReadOnlyList<string> dictLines, int imageHeight = 48, int batchSize = DefaultBatchSize)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dictLines = dictLines ?? throw new ArgumentNullException(nameof(dictLines));
        _imageHeight = imageHeight > 0 ? imageHeight : 48;
        _batchSize = batchSize > 0 ? batchSize : DefaultBatchSize;

        // The recognition graph has a single input and a single output; resolve their names once.
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    /// <summary>
    /// Applies the per-call character filter (<see cref="RecognitionOptions.Allowlist"/> /
    /// <see cref="RecognitionOptions.Blocklist"/>) used by subsequent <see cref="Recognize(IReadOnlyList{Image{Rgb24}})"/>
    /// calls. The recognizer is shared and cached across recognition calls while these options are per-call,
    /// so the engine sets the filter immediately before each <c>Recognize</c> (or uses the
    /// <see cref="Recognize(IReadOnlyList{Image{Rgb24}}, RecognitionOptions)"/> overload, which scopes it
    /// automatically). Passing <c>null</c>, or options with empty lists, clears the filter.
    /// </summary>
    /// <param name="options">The recognition options whose allow/block lists to honor, or <c>null</c> to clear.</param>
    public void SetCharacterFilter(RecognitionOptions? options)
    {
        _allowlist = options?.Allowlist;
        _blocklist = options?.Blocklist;
    }

    /// <summary>
    /// Recognizes a batch of crops while honoring the character filter (allow/block lists) carried by
    /// <paramref name="options"/>. The filter is scoped to this call: it is applied for the duration of the
    /// recognition and the previously active filter is restored afterwards, so a shared recognizer stays
    /// safe to reuse across calls with different options.
    /// </summary>
    /// <param name="crops">The upright text-line crops (caller retains ownership of each).</param>
    /// <param name="options">The recognition options whose allow/block lists to honor.</param>
    /// <returns>One (text, confidence) tuple per input crop, in the same order.</returns>
    public IReadOnlyList<(string Text, float Confidence)> Recognize(
        IReadOnlyList<Image<Rgb24>> crops, RecognitionOptions options)
    {
        ArgumentNullException.ThrowIfNull(crops);
        ArgumentNullException.ThrowIfNull(options);

        var prevAllow = _allowlist;
        var prevBlock = _blocklist;
        _allowlist = options.Allowlist;
        _blocklist = options.Blocklist;
        try
        {
            return Recognize(crops);
        }
        finally
        {
            _allowlist = prevAllow;
            _blocklist = prevBlock;
        }
    }

    /// <inheritdoc />
    public (string Text, float Confidence) Recognize(Image<Rgb24> crop)
    {
        ArgumentNullException.ThrowIfNull(crop);
        return Recognize(new[] { crop })[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<(string Text, float Confidence)> Recognize(IReadOnlyList<Image<Rgb24>> crops)
    {
        ArgumentNullException.ThrowIfNull(crops);
        int count = crops.Count;
        var results = new (string Text, float Confidence)[count];
        if (count == 0) return results;

        // Sort by aspect ratio (width / height) so adjacent crops have similar normalized widths and a
        // shared batch tensor needs the least zero-padding. We sort indices, not the crops, so we can scatter
        // each result back to its original position.
        var order = new int[count];
        var ratios = new float[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
            ratios[i] = crops[i].Width / (float)Math.Max(1, crops[i].Height);
        }
        Array.Sort(ratios, order);

        // Snapshot the active character filter for the whole call so concurrent mutation can't change it
        // mid-batch. The actual mask is built lazily below, once the vocab (and thus class indices) is known.
        var allowlist = _allowlist;
        var blocklist = _blocklist;
        bool[]? selectable = null;
        bool selectableBuilt = false;

        // Process in fixed-size batches; each batch is padded to its own maximum width.
        for (int start = 0; start < count; start += _batchSize)
        {
            int end = Math.Min(start + _batchSize, count);
            int batchCount = end - start;

            // Resize-and-normalize each crop in this batch, tracking the widest so we can pad the rest.
            var normalized = new float[batchCount][];
            var widths = new int[batchCount];
            int maxWidth = 1;
            for (int b = 0; b < batchCount; b++)
            {
                int srcIndex = order[start + b];
                normalized[b] = ResizeNormalize(crops[srcIndex], out int w);
                widths[b] = w;
                if (w > maxWidth) maxWidth = w;
            }

            // Build the [N, 3, H, maxWidth] CHW tensor, right-padding each row's width with zeros.
            var tensor = new DenseTensor<float>(new[] { batchCount, 3, _imageHeight, maxWidth });
            Span<float> buffer = tensor.Buffer.Span;
            int planeStride = _imageHeight * maxWidth;        // one channel plane per image
            int imageStride = 3 * planeStride;                // one full image
            for (int b = 0; b < batchCount; b++)
            {
                float[] src = normalized[b];
                int w = widths[b];
                int dstBase = b * imageStride;
                // src is laid out as [3, H, w]; copy each row run into the padded destination.
                for (int ch = 0; ch < 3; ch++)
                {
                    int srcChannelBase = ch * _imageHeight * w;
                    int dstChannelBase = dstBase + ch * planeStride;
                    for (int y = 0; y < _imageHeight; y++)
                    {
                        var srcRow = src.AsSpan(srcChannelBase + y * w, w);
                        srcRow.CopyTo(buffer.Slice(dstChannelBase + y * maxWidth, w));
                        // The remaining (maxWidth - w) columns stay zero — DenseTensor is zero-initialized.
                    }
                }
            }

            // Run inference and decode each output row, scattering back to the caller's order.
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
            using var outputs = _session.Run(inputs);
            var output = outputs.First().AsTensor<float>();

            // Output shape is [N, T, C]; T and C come from the model (T depends on maxWidth).
            var dims = output.Dimensions;
            int timeSteps = dims.Length >= 3 ? dims[1] : 0;
            int numClasses = dims.Length >= 3 ? dims[2] : _dictLines.Count;
            // Build the vocab to match the model's class count on first use (then reuse it).
            var vocab = _vocab ??= CharacterDictionary.BuildVocab(_dictLines, numClasses);

            // Build the allow/block selectable mask once for the whole call, now that the vocab (and so the
            // class→token mapping) is known. Null when no filter is requested — the decoder then runs
            // byte-identically to the unfiltered path.
            if (!selectableBuilt)
            {
                selectable = CharacterDictionary.BuildSelectableMask(vocab, allowlist, blocklist);
                selectableBuilt = true;
            }

            // Materialize once to a flat span so the decoder can index by (row, t, c) without per-element
            // overhead from the tensor indexer.
            ReadOnlySpan<float> flat = output.ToArray();
            int rowStride = timeSteps * numClasses;

            for (int b = 0; b < batchCount; b++)
            {
                ReadOnlySpan<float> rowLogits = flat.Slice(b * rowStride, rowStride);
                results[order[start + b]] = CtcDecoder.GreedyDecode(rowLogits, timeSteps, numClasses, vocab, selectable);
            }
        }

        return results;
    }

    /// <summary>
    /// Resizes <paramref name="crop"/> to height <see cref="_imageHeight"/> keeping aspect ratio, then
    /// normalizes pixels to <c>(x/255 − 0.5) / 0.5</c> (i.e. [−1,1]) in a planar CHW (RGB) float array of
    /// length <c>3 · H · w</c>, where <c>w</c> is the resized width returned via <paramref name="resizedWidth"/>.
    /// </summary>
    private float[] ResizeNormalize(Image<Rgb24> crop, out int resizedWidth)
    {
        int h = _imageHeight;
        // Scale width by the same factor that maps the source height to H, keeping aspect ratio. Clamp to
        // at least 1px so degenerate crops still produce a valid tensor.
        int w = Math.Max(1, (int)Math.Ceiling(crop.Width * (double)h / Math.Max(1, crop.Height)));

        using var resized = crop.Clone(c => c.Resize(new ResizeOptions
        {
            Size = new Size(w, h),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic,
        }));

        var data = new float[3 * h * w];
        int planeStride = h * w;
        resized.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);
                int rowBase = y * w;
                for (int x = 0; x < w; x++)
                {
                    Rgb24 px = row[x];
                    // (x/255 - 0.5)/0.5 == x/127.5 - 1
                    float r = px.R / 127.5f - 1f;
                    float g = px.G / 127.5f - 1f;
                    float bch = px.B / 127.5f - 1f;
                    int p = rowBase + x;
                    data[p] = r;                      // channel 0 (R)
                    data[planeStride + p] = g;        // channel 1 (G)
                    data[2 * planeStride + p] = bch;  // channel 2 (B)
                }
            }
        });

        resizedWidth = w;
        return data;
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}
