using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EasyOcrSharp.Structure.Engine.Recognition;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Structure.Engine.Classification;

/// <summary>
/// PaddleOCR text-line orientation classifier (the <c>cls</c> model, <c>PP-LCNet_x1_0_textline_ori</c>).
/// Resizes each crop to the model's fixed input (3×80×160), runs the 2-class network, and reports whether
/// the "180°" label won.
/// <para>
/// The pipeline mirrors PaddleX's textline-orientation preprocessing (<c>ResizeImage {size:[160,80]}</c>
/// + ImageNet <c>NormalizeImage</c>): stretch-resize the crop to exactly 160×80 with bilinear sampling
/// (no aspect preservation, no padding), then normalize each RGB channel as
/// <c>(x/255 − mean)/std</c> with mean [0.485, 0.456, 0.406] / std [0.229, 0.224, 0.225] into CHW order.
/// The graph's input is named <c>x</c> with the fixed shape <c>[N, 3, 80, 160]</c> (only the batch
/// dimension is dynamic) and its single output <c>fetch_name_0</c> is a <c>[N, 2]</c> tensor of scores
/// over the labels {0°, 180°}; <c>argmax</c> selects the label and its value is the confidence. This
/// class reports that raw verdict; the caller decides whether to act on it, gating with
/// <see cref="Models.RecognitionOptions.TextLineOrientationThreshold"/> — PaddleX 3.x rotates on plain
/// argmax, which measurably destroys upright text when the classifier misfires.
/// </para>
/// <para>
/// Many crops are classified <see cref="BatchSize"/> at a time. Every crop is stretched to the same fixed
/// size, so a batch needs no padding and each crop's input is identical to its single-crop tensor.
/// </para>
/// </summary>
internal sealed class TextLineClassifier : IAngleClassifier
{
    // The cls graph (PP-LCNet_x1_0_textline_ori) has a fixed spatial input of 80×160; only the batch
    // dimension is dynamic. Feeding any other H×W fails ONNX Runtime's shape check.
    internal const int TargetHeight = 80;
    internal const int TargetWidth = 160;
    private const int RotatedLabel = 1; // output index for the 180° class

    /// <summary>
    /// Crops per ONNX run when classifying many crops.
    /// </summary>
    internal const int BatchSize = 6;

    private const int ImageStride = 3 * TargetHeight * TargetWidth;

    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    private readonly InferenceSession _session;
    private readonly string _inputName;

    /// <summary>
    /// Creates the classifier over an already-built ONNX <see cref="InferenceSession"/>.
    /// </summary>
    /// <param name="session">The loaded cls model session (this instance takes ownership and disposes it).</param>
    public TextLineClassifier(InferenceSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        // The cls graph has a single input; resolve its name once rather than per-call.
        _inputName = _session.InputMetadata.Keys.First();
    }

    /// <inheritdoc />
    public (bool Rotated, float Score) Classify(Image<Rgb24> crop)
    {
        ArgumentNullException.ThrowIfNull(crop);
        return Classify(new[] { crop }, maxDegreeOfParallelism: 1)[0];
    }

    /// <inheritdoc />
    public IReadOnlyList<(bool Rotated, float Score)> Classify(IReadOnlyList<Image<Rgb24>> crops, int maxDegreeOfParallelism)
    {
        ArgumentNullException.ThrowIfNull(crops);
        var results = new (bool Rotated, float Score)[crops.Count];

        for (int start = 0; start < crops.Count; start += BatchSize)
        {
            int count = Math.Min(BatchSize, crops.Count - start);
            int batchStart = start;

            var input = new DenseTensor<float>(new[] { count, 3, TargetHeight, TargetWidth });
            Memory<float> buffer = input.Buffer;
            BoundedParallel.For(count, maxDegreeOfParallelism, CancellationToken.None,
                i => WriteInput(crops[batchStart + i], buffer, i * ImageStride));

            using var outputs = _session.Run(
                new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });

            // Output "fetch_name_0" is [N, 2]: scores for {0°, 180°}. Take each row's argmax and its value.
            var output = outputs[0].AsTensor<float>();
            ReadOnlySpan<float> scores = output is DenseTensor<float> dense ? dense.Buffer.Span : output.ToArray();
            int classes = scores.Length / count;
            for (int i = 0; i < count; i++)
            {
                int idx = ArgMax(scores.Slice(i * classes, classes), out float score);
                // The model's unfiltered verdict: the caller applies the confidence gate.
                results[batchStart + i] = (idx == RotatedLabel, score);
            }
        }

        return results;
    }

    /// <summary>
    /// Preprocesses <paramref name="crop"/> into the model's <c>[1, 3, 80, 160]</c> input:
    /// stretch-resize to exactly 160×80 with bilinear sampling (PaddleX's <c>ResizeImage</c> ignores
    /// aspect ratio and never pads), then ImageNet-normalize each RGB channel as
    /// <c>(x/255 − mean)/std</c> in CHW layout.
    /// </summary>
    internal static DenseTensor<float> BuildInputTensor(Image<Rgb24> crop)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, TargetHeight, TargetWidth });
        WriteInput(crop, tensor.Buffer, 0);
        return tensor;
    }

    /// <summary>
    /// Writes one crop's normalized <c>[3, 80, 160]</c> input into <paramref name="buffer"/> starting at
    /// <paramref name="offset"/> (see <see cref="BuildInputTensor"/> for the preprocessing).
    /// </summary>
    private static void WriteInput(Image<Rgb24> crop, Memory<float> buffer, int offset)
    {
        using var resized = crop.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(TargetWidth, TargetHeight),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Triangle, // bilinear (cv2.resize default INTER_LINEAR)
        }));

        const int channelStride = TargetHeight * TargetWidth;

        resized.ProcessPixelRows(accessor =>
        {
            // Re-acquire the span inside the delegate: a ref-struct Span<T> can't be captured by a lambda.
            var data = buffer.Span;
            for (int y = 0; y < TargetHeight; y++)
            {
                var row = accessor.GetRowSpan(y);
                int rowOffset = offset + y * TargetWidth;
                for (int x = 0; x < TargetWidth; x++)
                {
                    Rgb24 p = row[x];
                    int pixelOffset = rowOffset + x;
                    data[pixelOffset] = (p.R / 255f - Mean[0]) / Std[0];                       // R
                    data[channelStride + pixelOffset] = (p.G / 255f - Mean[1]) / Std[1];       // G
                    data[2 * channelStride + pixelOffset] = (p.B / 255f - Mean[2]) / Std[2];   // B
                }
            }
        });
    }

    /// <summary>
    /// Returns the index of the largest score and reports that score via <paramref name="max"/>.
    /// </summary>
    private static int ArgMax(ReadOnlySpan<float> scores, out float max)
    {
        int best = 0;
        // A truncated/corrupt .onnx, or output-name resolution landing on an empty auxiliary output, yields
        // an empty span; DocPreprocessor.ArgMax already guards this way.
        if (scores.Length == 0) { max = 0f; return 0; }
        max = scores[0];
        for (int i = 1; i < scores.Length; i++)
        {
            if (scores[i] > max)
            {
                max = scores[i];
                best = i;
            }
        }
        return best;
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}
