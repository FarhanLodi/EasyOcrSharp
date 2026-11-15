using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EasyOcrSharp.Structure.Engine.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Structure.Engine.Recognition;

/// <summary>
/// PaddleOCR text recognizer (SVTR_LCNet / CRNN family). Each crop is resized to a fixed height
/// (typically 48px) keeping aspect ratio, padded to the batch tensor width, normalized, run through the
/// ONNX network to per-timestep logits, then CTC-greedy-decoded against the character dictionary via
/// <see cref="CtcDecoder"/>.
/// <para>
/// Preprocessing matches PaddleOCR's <c>resize_norm_img</c> (PaddleX
/// <c>text_recognition/processors.py</c>): per batch the tensor width is
/// <c>imgW = int(H · max_wh_ratio)</c> where <c>max_wh_ratio = max(320/H, widest crop's w/h)</c>,
/// capped at 3200 px (crops beyond the cap are aspect-squeezed). Each crop is scaled to height
/// <see cref="_imageHeight"/> with bilinear resampling to width <c>min(ceil(H · w/h), imgW)</c> — the
/// widest crop lands on the floored <c>imgW</c> — normalized as <c>(x/255 − 0.5) / 0.5</c> into [−1,1],
/// laid out as a CHW <b>BGR</b> float tensor (the ONNX rec export consumes BGR, per its
/// <c>inference.yml</c> <c>DecodeImage img_mode: BGR</c>), and right-padded with zeros to <c>imgW</c>.
/// The batch overload sorts crops by aspect ratio (width/height) so similarly-shaped lines share a
/// tensor with minimal padding, runs them in chunks of <c>rec_batch_num</c> (default 6), and reorders
/// the results back to the caller's input order.
/// </para>
/// <para>
/// The network output is <c>[N, T, C]</c> (N rows, T timesteps, C = vocab classes). Each row is handed to
/// <see cref="CtcDecoder.GreedyDecode(ReadOnlySpan{float}, int, int, IReadOnlyList{string}, bool[], List{CtcCharacter}, int, float)"/>
/// with the Paddle vocab (blank at index 0), reading the output tensor's memory in place. The recognizer
/// returns every result regardless of confidence; the engine applies <c>drop_score</c>.
/// </para>
/// <para>
/// Thread safety: the character filter travels down each call (never stored on the instance) and
/// <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/> is thread-safe, so one cached
/// recognizer can serve concurrent calls, and one call can run several batches concurrently. Batch
/// composition is fixed by the aspect sort, so concurrency never changes a result.
/// </para>
/// </summary>
internal sealed class SvtrRecognizer : ITextRecognizer
{
    /// <summary>
    /// PaddleOCR's default recognition batch size (<c>rec_batch_num</c>).
    /// </summary>
    internal const int DefaultBatchSize = 6;

    /// <summary>
    /// Minimum batch tensor width in pixels. PaddleOCR's <c>rec_image_shape</c> is [3, 48, 320]:
    /// <c>max_wh_ratio</c> starts at 320/H, so every batch tensor is at least 320 px wide.
    /// </summary>
    internal const int MinTensorWidth = 320;

    /// <summary>
    /// Maximum batch tensor width in pixels (PaddleOCR's <c>max_imgW</c>). Wider crops are
    /// aspect-squeezed down to this width rather than growing the tensor.
    /// </summary>
    internal const int MaxTensorWidth = 3200;

    /// <summary>
    /// PaddleOCR's <c>rec_image_shape</c> height, which every recognizer pack is exported for.
    /// </summary>
    internal const int DefaultImageHeight = 48;

    /// <summary>
    /// Upper bound on a crop's resized width, matching <c>CrnnRecognizer.MaxWidth</c>. Without it the
    /// tensor and the model's per-timestep output grow without limit in the source aspect ratio.
    /// </summary>
    private const int MaxRecWidth = 4096;

    /// <summary>
    /// <c>(x/255 − 0.5)/0.5</c> for every byte value, computed with exactly the per-pixel expression
    /// <c>x / 127.5f − 1f</c> so the table lookup is bit-identical to evaluating it inline.
    /// </summary>
    private static readonly float[] NormalizeTable = BuildNormalizeTable();

    private readonly InferenceSession _session;
    private readonly IReadOnlyList<string> _dictLines;
    // Built lazily on the first decode, once the model's actual output class count is known, so the vocab
    // length matches the network exactly (community dicts disagree on whether the blank/space are included).
    private IReadOnlyList<string>? _vocab;
    private readonly int _imageHeight;
    private readonly string _inputName;
    private readonly int _batchSize;

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
    public SvtrRecognizer(InferenceSession session, IReadOnlyList<string> dictLines, int imageHeight = DefaultImageHeight, int batchSize = DefaultBatchSize)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dictLines = dictLines ?? throw new ArgumentNullException(nameof(dictLines));
        _imageHeight = imageHeight > 0 ? imageHeight : DefaultImageHeight;
        _batchSize = batchSize > 0 ? batchSize : DefaultBatchSize;

        // The recognition graph has a single input; resolve its name once.
        _inputName = _session.InputMetadata.Keys.First();
    }

    /// <summary>
    /// Recognizes a batch of crops while honoring the character filter (allow/block lists),
    /// <see cref="RecognitionOptions.BatchSize"/> and <see cref="RecognitionOptions.MaxDegreeOfParallelism"/>
    /// carried by <paramref name="options"/>. The filter is passed down this call only, so a shared recognizer
    /// stays safe to use concurrently with different options. A non-positive
    /// <see cref="RecognitionOptions.BatchSize"/> falls back to the constructor's batch size.
    /// </summary>
    /// <param name="crops">The upright text-line crops (caller retains ownership of each).</param>
    /// <param name="options">The recognition options whose allow/block lists and batch size to honor.</param>
    /// <returns>One (text, confidence) tuple per input crop, in the same order.</returns>
    public IReadOnlyList<(string Text, float Confidence)> Recognize(
        IReadOnlyList<Image<Rgb24>> crops, RecognitionOptions options)
        => ToTuples(RecognizeDetailed(crops, options, maxConcurrentBatches: 1, includeCharacters: false));

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
        return ToTuples(RecognizeCore(crops, _batchSize, null, null, 0, 1, 1, false, CancellationToken.None));
    }

    /// <inheritdoc />
    public IReadOnlyList<RecognizedText> RecognizeDetailed(
        IReadOnlyList<Image<Rgb24>> crops, RecognitionOptions options, int maxConcurrentBatches, bool includeCharacters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(crops);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        return RecognizeCore(
            crops,
            options.BatchSize > 0 ? options.BatchSize : _batchSize,
            options.Allowlist,
            options.Blocklist,
            options.SpaceRecoveryThreshold,
            maxConcurrentBatches,
            BoundedParallel.ResolveDegree(options.MaxDegreeOfParallelism),
            includeCharacters,
            cancellationToken);
    }

    /// <summary>
    /// The shared recognition body. The allow/block lists, batch size and space-recovery threshold arrive
    /// as parameters rather than instance state, which is what makes a cached recognizer safe to share
    /// across concurrent calls.
    /// </summary>
    private RecognizedText[] RecognizeCore(
        IReadOnlyList<Image<Rgb24>> crops,
        int batchSize,
        IReadOnlyCollection<string>? allowlist,
        IReadOnlyCollection<string>? blocklist,
        double spaceRecoveryThreshold,
        int maxConcurrentBatches,
        int maxDegreeOfParallelism,
        bool includeCharacters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int count = crops.Count;
        var results = new RecognizedText[count];
        if (count == 0) return results;

        // Sort by aspect ratio (width / height) so adjacent crops have similar normalized widths and a
        // shared batch tensor needs the least zero-padding. We sort indices, not the crops, so we can scatter
        // each result back to its original position.
        var order = new int[count];
        var ratios = new double[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
            ratios[i] = crops[i].Width / (double)Math.Max(1, crops[i].Height);
        }
        Array.Sort(ratios, order);

        int batchCount = (count + batchSize - 1) / batchSize;
        int concurrentBatches = Math.Clamp(maxConcurrentBatches, 1, batchCount);
        // Batches running concurrently already spread the crop preprocessing across threads; only a
        // sequential run parallelizes the per-crop resize inside each batch.
        int cropParallelism = concurrentBatches > 1 ? 1 : maxDegreeOfParallelism;

        // The allow/block mask needs the vocab, which needs the model's class count from the first output.
        // The first batch therefore runs alone and fixes both for the rest of the call.
        bool[]? selectable = null;
        int spaceClass = -1;
        float spaceThreshold = spaceRecoveryThreshold > 0 ? (float)spaceRecoveryThreshold : 0f;
        RunBatch(0);
        if (batchCount > 1)
        {
            BoundedParallel.For(batchCount - 1, concurrentBatches, cancellationToken, b => RunBatch(b + 1));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return results;

        void RunBatch(int batchIndex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var batchStage = StructureDiagnostics.MeasureStage("recognition_batch");
            int start = batchIndex * batchSize;
            int end = Math.Min(start + batchSize, count);
            int batchCountInTensor = end - start;

            // ratios is sorted ascending, so the batch maximum is its last element.
            int imgW = ComputeBatchTensorWidth(_imageHeight, ratios[end - 1]);

            // Build the [N, 3, H, imgW] CHW tensor in place: each crop is resized to
            // min(ceil(H * w/h), imgW) — the widest crop lands on the *floored* imgW, matching Python's
            // int() truncation — and its normalized pixels are written straight into its row of the batch
            // buffer. The remaining (imgW - w) columns stay zero (DenseTensor is zero-initialized).
            var tensor = new DenseTensor<float>(new[] { batchCountInTensor, 3, _imageHeight, imgW });
            Memory<float> buffer = tensor.Buffer;
            var widths = new int[batchCountInTensor];
            int imageStride = 3 * _imageHeight * imgW;
            StructureDiagnostics.RecognitionTensorBytes.Record((long)batchCountInTensor * imageStride * sizeof(float));
            using (StructureDiagnostics.MeasureStage("recognition_pack"))
            {
                BoundedParallel.For(batchCountInTensor, cropParallelism, cancellationToken, b =>
                {
                    int w = Math.Max(1, Math.Min((int)Math.Ceiling(_imageHeight * ratios[start + b]), imgW));
                    widths[b] = w;
                    WriteNormalized(crops[order[start + b]], w, imgW, buffer, b * imageStride);
                });
            }
            if (StructureDiagnostics.RecognitionPadding.Enabled)
                StructureDiagnostics.RecognitionPadding.Record(1d - widths.Sum(w => (long)w) / (double)((long)batchCountInTensor * imgW));

            // Run inference and decode each output row, scattering back to the caller's order.
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> inferenceOutputs;
            cancellationToken.ThrowIfCancellationRequested();
            using (StructureDiagnostics.MeasureStage("recognition_inference"))
                inferenceOutputs = _session.Run(inputs);
            using var outputs = inferenceOutputs;
            cancellationToken.ThrowIfCancellationRequested();
            using var decodeStage = StructureDiagnostics.MeasureStage("ctc_decode");
            var output = outputs.First().AsTensor<float>();

            // Output shape is [N, T, C]; T and C come from the model (T depends on imgW).
            var dims = output.Dimensions;
            int timeSteps = dims.Length >= 3 ? dims[1] : 0;
            int numClasses = dims.Length >= 3 ? dims[2] : _dictLines.Count;
            // Build the vocab to match the model's class count on first use (then reuse it). Published with
            // Interlocked so a concurrent caller either sees null or a fully-constructed list — a plain
            // `??=` store can be observed half-built on weak memory models (ARM64).
            var vocab = Volatile.Read(ref _vocab);
            if (vocab is null)
            {
                var built = CharacterDictionary.BuildVocab(_dictLines, numClasses);
                vocab = Interlocked.CompareExchange(ref _vocab, built, null) ?? built;
            }

            // Build the allow/block selectable mask once for the whole call (first batch), now that the vocab
            // (and so the class→token mapping) is known. Null when no filter is requested — the decoder then
            // runs byte-identically to the unfiltered path.
            if (batchIndex == 0)
            {
                selectable = CharacterDictionary.BuildSelectableMask(vocab, allowlist, blocklist);
                spaceClass = CharacterDictionary.FindSpaceClass(vocab);
            }

            // Decode straight from the output tensor's memory while the results are still alive (no copy):
            // the output is [N, T, C] against an ~18k-class vocabulary, so ToArray() doubles the peak for no
            // benefit. Fall back to a copy only if some future export returns another tensor type.
            ReadOnlySpan<float> flat = output is DenseTensor<float> dense ? dense.Buffer.Span : output.ToArray();
            int rowStride = timeSteps * numClasses;

            for (int b = 0; b < batchCountInTensor; b++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<float> rowLogits = flat.Slice(b * rowStride, rowStride);
                int source = order[start + b];
                if (!includeCharacters)
                {
                    var (text, confidence) = CtcDecoder.GreedyDecode(
                        rowLogits, timeSteps, numClasses, vocab, selectable, null, spaceClass, spaceThreshold);
                    results[source] = new RecognizedText(text, confidence);
                    continue;
                }

                var characters = new List<CtcCharacter>();
                var (detailedText, detailedConfidence) = CtcDecoder.GreedyDecode(
                    rowLogits, timeSteps, numClasses, vocab, selectable, characters, spaceClass, spaceThreshold);
                results[source] = new RecognizedText(detailedText, detailedConfidence)
                {
                    Characters = characters,
                    // One timestep covers imgW/T tensor pixels; the crop occupies only its resized width w of
                    // the tensor, so scale back by cropWidth/w.
                    StepWidth = timeSteps > 0
                        ? imgW / (double)timeSteps * (crops[source].Width / (double)widths[b])
                        : 0,
                };
            }
        }
    }

    /// <summary>
    /// Python parity (PaddleX <c>text_recognition/processors.py:50-97</c>): the batch tensor width is
    /// <c>imgW = int(H · max_wh_ratio)</c> with <c>max_wh_ratio = max(320/H, widest crop's w/h)</c>, so
    /// every batch is at least <see cref="MinTensorWidth"/> (320) px wide, capped at
    /// <see cref="MaxTensorWidth"/> (3200) — crops beyond the cap get aspect-squeezed by the
    /// per-crop resized-width clamp.
    /// </summary>
    /// <param name="imageHeight">Recognition input height (<c>rec_image_shape</c> H, typically 48).</param>
    /// <param name="widestRatio">The widest crop's width/height ratio in the batch.</param>
    /// <returns>The batch tensor width in pixels, in [320, 3200].</returns>
    internal static int ComputeBatchTensorWidth(int imageHeight, double widestRatio)
    {
        double maxWhRatio = Math.Max(MinTensorWidth / (double)imageHeight, widestRatio);
        return Math.Min((int)(imageHeight * maxWhRatio), MaxTensorWidth);
    }

    /// <summary>
    /// Resizes <paramref name="crop"/> to <see cref="_imageHeight"/> × <paramref name="targetWidth"/>
    /// (the caller computes the width from the batch policy), then writes the pixels normalized to
    /// <c>(x/255 − 0.5) / 0.5</c> (i.e. [−1,1]) as planar CHW <b>BGR</b> into
    /// <paramref name="buffer"/> at <paramref name="imageOffset"/>, each plane <paramref name="tensorWidth"/> wide.
    /// </summary>
    private void WriteNormalized(Image<Rgb24> crop, int targetWidth, int tensorWidth, Memory<float> buffer, int imageOffset)
    {
        int h = _imageHeight;
        int w = targetWidth;

        using var resized = crop.Clone(c => c.Resize(new ResizeOptions
        {
            Size = new Size(w, h),
            Mode = ResizeMode.Stretch,
            // Python uses cv2.resize's default INTER_LINEAR.
            Sampler = KnownResamplers.Triangle,
        }));

        int planeStride = h * tensorWidth;
        float[] table = NormalizeTable;
        resized.ProcessPixelRows(accessor =>
        {
            // Re-acquire the span inside the delegate: a ref-struct Span<T> can't be captured by a lambda.
            Span<float> data = buffer.Span;
            for (int y = 0; y < h; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);
                int rowBase = imageOffset + y * tensorWidth;
                // The ONNX rec export consumes BGR crops (inference.yml DecodeImage img_mode: BGR).
                Span<float> blue = data.Slice(rowBase, w);                     // channel 0 (B)
                Span<float> green = data.Slice(rowBase + planeStride, w);      // channel 1 (G)
                Span<float> red = data.Slice(rowBase + 2 * planeStride, w);    // channel 2 (R)
                for (int x = 0; x < w; x++)
                {
                    Rgb24 px = row[x];
                    blue[x] = table[px.B];
                    green[x] = table[px.G];
                    red[x] = table[px.R];
                }
            }
        });
    }

    private static float[] BuildNormalizeTable()
    {
        var table = new float[256];
        for (int i = 0; i < table.Length; i++)
        {
            byte value = (byte)i;
            // (x/255 - 0.5)/0.5 == x/127.5 - 1 — the exact expression the per-pixel loop used.
            table[i] = value / 127.5f - 1f;
        }
        return table;
    }

    private static (string Text, float Confidence)[] ToTuples(IReadOnlyList<RecognizedText> readings)
    {
        var tuples = new (string Text, float Confidence)[readings.Count];
        for (int i = 0; i < tuples.Length; i++) tuples[i] = (readings[i].Text, readings[i].Confidence);
        return tuples;
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}
