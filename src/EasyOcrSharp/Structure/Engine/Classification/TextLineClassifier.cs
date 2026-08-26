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
/// The pipeline mirrors PaddleOCR's <c>ClsResizeImg</c> / RapidOcrNet's <c>TextClassifier</c>: resize the
/// crop to height 80 keeping aspect ratio (width capped at 160), right-pad with zeros to width 160,
/// normalize each channel as <c>(x/255 − 0.5)/0.5</c> into RGB CHW order, and run the ONNX graph. The
/// graph's input is named <c>x</c> with the fixed shape <c>[N, 3, 80, 160]</c> (only the batch dimension
/// is dynamic) and its single output <c>fetch_name_0</c> is a <c>[N, 2]</c> tensor of scores over the
/// labels {0°, 180°}; <c>argmax</c> selects the label and its value is the confidence. The 180° label
/// (index 1) is only accepted when its score clears the configured threshold (PaddleOCR's
/// <c>cls_thresh</c>, default 0.9). When accepted, the caller rotates the crop 180° before recognition.
/// </para>
/// </summary>
internal sealed class TextLineClassifier : IAngleClassifier
{
    // The cls graph (PP-LCNet_x1_0_textline_ori) has a fixed spatial input of 80×160; only the batch
    // dimension is dynamic. Feeding any other H×W fails ONNX Runtime's shape check.
    private const int TargetHeight = 80;
    private const int TargetWidth = 160;
    private const int RotatedLabel = 1; // output index for the 180° class

    private readonly InferenceSession _session;
    private readonly double _threshold;
    private readonly string _inputName;

    /// <summary>
    /// Creates the classifier over an already-built ONNX <see cref="InferenceSession"/>.
    /// </summary>
    /// <param name="session">The loaded cls model session (this instance takes ownership and disposes it).</param>
    /// <param name="threshold">Confidence above which the 180° label is accepted (PaddleOCR's <c>cls_thresh</c>). Default 0.9.</param>
    public TextLineClassifier(InferenceSession session, double threshold = 0.9)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _threshold = threshold;
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

        bool rotated = idx == RotatedLabel && score > _threshold;
        return (rotated, score);
    }

    /// <summary>
    /// Preprocesses <paramref name="crop"/> into the model's <c>[1, 3, 80, 160]</c> input: resize to
    /// height 80 keeping aspect ratio (width = min(160, ⌈80·w/h⌉)), right-pad with zeros to width 160,
    /// and normalize each RGB channel as <c>(x/255 − 0.5)/0.5</c> in CHW layout. Padding columns stay
    /// at the normalized value of 0 (i.e. <c>(0 − 0.5)/0.5 = −1</c>), matching PaddleOCR's zero-padding.
    /// </summary>
    private static DenseTensor<float> BuildInputTensor(Image<Rgb24> crop)
    {
        int resizeWidth = Math.Min(
            TargetWidth,
            (int)Math.Ceiling((double)TargetHeight * crop.Width / crop.Height));
        resizeWidth = Math.Max(1, resizeWidth);

        using var resized = crop.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(resizeWidth, TargetHeight),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic,
        }));

        // Tensor is zero-initialized, so padded columns [resizeWidth, 160) start at the channel mean and
        // become −1 after normalization — exactly PaddleOCR's zero-pad-then-normalize behavior.
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
                for (int x = 0; x < resizeWidth; x++)
                {
                    Rgb24 p = row[x];
                    int pixelOffset = rowOffset + x;
                    buffer[pixelOffset] = Normalize(p.R);                       // R
                    buffer[channelStride + pixelOffset] = Normalize(p.G);       // G
                    buffer[2 * channelStride + pixelOffset] = Normalize(p.B);   // B
                }
            }
        });

        return tensor;
    }

    /// <summary>
    /// Normalizes an 8-bit channel value to <c>(v/255 − 0.5)/0.5</c>, i.e. the range [−1, 1].
    /// </summary>
    private static float Normalize(byte value) => (value / 255f - 0.5f) / 0.5f;

    /// <summary>
    /// Returns the index of the largest score and reports that score via <paramref name="max"/>.
    /// </summary>
    private static int ArgMax(ReadOnlySpan<float> scores, out float max)
    {
        int best = 0;
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
