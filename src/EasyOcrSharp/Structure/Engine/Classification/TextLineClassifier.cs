using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
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
/// </summary>
internal sealed class TextLineClassifier : IAngleClassifier
{
    // The cls graph (PP-LCNet_x1_0_textline_ori) has a fixed spatial input of 80×160; only the batch
    // dimension is dynamic. Feeding any other H×W fails ONNX Runtime's shape check.
    private const int TargetHeight = 80;
    private const int TargetWidth = 160;
    private const int RotatedLabel = 1; // output index for the 180° class

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

        var input = BuildInputTensor(crop);

        using var results = _session.Run(
            new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });

        // Output "fetch_name_0" is [1, 2]: scores for {0°, 180°}. Take argmax and its value.
        var scores = results[0].AsEnumerable<float>().ToArray();
        int idx = ArgMax(scores, out float score);

        // The model's unfiltered verdict: the caller applies the confidence gate.
        return (idx == RotatedLabel, score);
    }

    /// <summary>
    /// Preprocesses <paramref name="crop"/> into the model's <c>[1, 3, 80, 160]</c> input:
    /// stretch-resize to exactly 160×80 with bilinear sampling (PaddleX's <c>ResizeImage</c> ignores
    /// aspect ratio and never pads), then ImageNet-normalize each RGB channel as
    /// <c>(x/255 − mean)/std</c> in CHW layout.
    /// </summary>
    internal static DenseTensor<float> BuildInputTensor(Image<Rgb24> crop)
    {
        using var resized = crop.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(TargetWidth, TargetHeight),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Triangle, // bilinear (cv2.resize default INTER_LINEAR)
        }));

        var tensor = new DenseTensor<float>(new[] { 1, 3, TargetHeight, TargetWidth });
        const int channelStride = TargetHeight * TargetWidth;
        var bufferMemory = tensor.Buffer;

        resized.ProcessPixelRows(accessor =>
        {
            // Re-acquire the span inside the delegate: a ref-struct Span<T> can't be captured by a lambda.
            var buffer = bufferMemory.Span;
            for (int y = 0; y < TargetHeight; y++)
            {
                var row = accessor.GetRowSpan(y);
                int rowOffset = y * TargetWidth;
                for (int x = 0; x < TargetWidth; x++)
                {
                    Rgb24 p = row[x];
                    int pixelOffset = rowOffset + x;
                    buffer[pixelOffset] = (p.R / 255f - Mean[0]) / Std[0];                       // R
                    buffer[channelStride + pixelOffset] = (p.G / 255f - Mean[1]) / Std[1];       // G
                    buffer[2 * channelStride + pixelOffset] = (p.B / 255f - Mean[2]) / Std[2];   // B
                }
            }
        });

        return tensor;
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
