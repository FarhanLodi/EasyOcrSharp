using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Document pre-processor. When enabled, runs the PP-LCNet document-orientation classifier (0/90/180/270°)
/// to upright the page and/or the UVDoc unwarp model to dewarp it, before text detection. Either model
/// may be absent (<c>null</c> session), in which case that stage is skipped. Owns and disposes both
/// optional sessions.
/// <para>
/// <b>Implementation status:</b> both stages are <b>fully implemented</b>. Orientation: PP-LCNet doc-ori
/// (input <c>x</c> <c>[N,3,224,224]</c> → output <c>[N,4]</c> over {0°,90°,180°,270°}) → argmax → in-place
/// 90° rotation. Unwarp: UVDoc (input <c>image</c> <c>[N,3,H,W]</c>, [0,1]-normalized → output
/// <c>[N,3,H,W]</c>) which emits a <b>dewarped RGB image</b> at the input resolution (verified against the
/// real <c>UVDoc.onnx</c> export: the output is a rectified picture in [0,1], not a sampling grid/flow),
/// which we resize back to the original page size.
/// </para>
/// Reference: PaddleX <c>doc_orientation_classify</c> (PP-LCNet_x1_0_doc_ori) and <c>UVDoc</c> dewarp.
/// </summary>
internal sealed class DocPreprocessor : IDisposable
{
    // ImageNet mean/std (RGB), applied to pixel/255 — the PP-LCNet doc-ori classifier's normalization.
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    // PP-LCNet_x1_0_doc_ori input is a fixed 3×224×224 (standard PP-LCNet classification head). Verified
    // against the real export: input "x" [N,3,224,224] tensor(float), output [N,4] tensor(float).
    private const int OrientSize = 224;

    // UVDoc.onnx accepts a dynamic [N,3,H,W] input; we feed the model's canonical training size
    // (488 wide × 712 tall, i.e. tensor [N,3,712,488]). The output is a dewarped RGB image with the SAME
    // spatial dims as the input (verified: out H,W == in H,W for every probed size), which we then resize
    // back to the original page resolution.
    private const int UnwarpWidth = 488;
    private const int UnwarpHeight = 712;

    // Output index -> clockwise rotation (degrees) PaddleX assigns to the page. Index i means "the page is
    // currently rotated by OrientationAngles[i]° clockwise from upright"; we rotate by the inverse to correct.
    // Verified empirically: feeding ocr_test1.png rotated 0/90/180/270° CW yields argmax 0/1/2/3 respectively.
    private static readonly int[] OrientationAngles = { 0, 90, 180, 270 };

    private readonly InferenceSession? _orientation;
    private readonly InferenceSession? _unwarp;
    private readonly string? _orientationInputName;
    private readonly string? _unwarpInputName;

    /// <summary>
    /// Creates the pre-processor over optional orientation / unwarp ONNX sessions.
    /// </summary>
    /// <param name="orientation">The doc-orientation classifier session, or <c>null</c> to disable orientation.</param>
    /// <param name="unwarp">The UVDoc unwarp session, or <c>null</c> to disable unwarping.</param>
    public DocPreprocessor(InferenceSession? orientation, InferenceSession? unwarp)
    {
        _orientation = orientation;
        _unwarp = unwarp;
        // Each graph has a single input; resolve its name once when present (doc-ori: "x", UVDoc: "image").
        _orientationInputName = orientation?.InputMetadata.Keys.First();
        _unwarpInputName = unwarp?.InputMetadata.Keys.First();
    }

    /// <summary>
    /// Applies the enabled pre-processing stages to <paramref name="input"/> and returns the processed page
    /// plus the corrective clockwise rotation (degrees) applied by the orientation stage (0 when none).
    /// </summary>
    /// <remarks>
    /// Order matches PaddleX's document pipeline: orientation correction first (so unwarp sees an upright
    /// page), then unwarp. The returned image is always a fresh image the caller owns and disposes — even
    /// when no stage changes the pixels we hand back a clone so callers can dispose the input independently.
    /// </remarks>
    public (Image<Rgb24> image, int rotationApplied) Apply(Image<Rgb24> input, bool useOrientation, bool useUnwarp)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Work on an owned copy so the input remains the caller's to dispose and intermediate stages can be
        // swapped without aliasing. Each stage replaces 'current' and disposes the one it consumed.
        Image<Rgb24> current = input.Clone();
        int rotationApplied = 0;

        try
        {
            // --- Orientation: classify {0,90,180,270} and rotate the whole page upright (FULLY IMPLEMENTED).
            if (useOrientation && _orientation is not null)
            {
                rotationApplied = ClassifyAndRotate(current);
            }

            // --- Unwarp: UVDoc dewarp (FULLY IMPLEMENTED — returns a rectified image; see Unwarp()).
            if (useUnwarp && _unwarp is not null)
            {
                Image<Rgb24> dewarped = Unwarp(current);
                if (!ReferenceEquals(dewarped, current))
                {
                    current.Dispose();
                    current = dewarped;
                }
            }

            return (current, rotationApplied);
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs the PP-LCNet doc-orientation classifier on <paramref name="image"/>, takes the argmax over the
    /// four {0,90,180,270} logits, and rotates the image in place to upright it. Returns the rotation (in
    /// degrees, clockwise) that was actually applied to the image (0 when the page is already upright).
    /// </summary>
    /// <remarks>
    /// PaddleX labels index <c>i</c> with <see cref="OrientationAngles"/>[i] = the clockwise angle by which
    /// the page is currently rotated away from upright. To correct it we rotate by the same magnitude in the
    /// opposite direction: a page predicted as "90° clockwise" is rotated 270° clockwise (= −90°) back to
    /// upright. We report the applied (corrective) clockwise rotation so the caller can map any downstream
    /// coordinates back to the original page if needed.
    /// </remarks>
    private int ClassifyAndRotate(Image<Rgb24> image)
    {
        var input = BuildOrientationTensor(image);

        using var results = _orientation!.Run(
            new[] { NamedOnnxValue.CreateFromTensor(_orientationInputName!, input) });

        // Output is [1, 4]: logits/probabilities over {0°, 90°, 180°, 270°}. Take the argmax label.
        var scores = results[0].AsEnumerable<float>().ToArray();
        int label = ArgMax(scores);
        int detected = label >= 0 && label < OrientationAngles.Length ? OrientationAngles[label] : 0;
        if (detected == 0)
        {
            return 0;
        }

        // Corrective clockwise rotation = 360 - detected (e.g. detected 90 -> rotate 270 CW to upright).
        int correction = (360 - detected) % 360;
        var mode = correction switch
        {
            90 => RotateMode.Rotate90,
            180 => RotateMode.Rotate180,
            270 => RotateMode.Rotate270,
            _ => RotateMode.None,
        };
        if (mode != RotateMode.None)
        {
            image.Mutate(ctx => ctx.Rotate(mode));
        }
        return correction;
    }

    /// <summary>
    /// Preprocesses <paramref name="image"/> into the doc-ori model's <c>[1,3,224,224]</c> input: resize
    /// (stretch) to 224×224, ImageNet-normalize <c>(pixel/255 - mean)/std</c> in RGB order, CHW layout.
    /// </summary>
    private static DenseTensor<float> BuildOrientationTensor(Image<Rgb24> image)
    {
        using var resized = image.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(OrientSize, OrientSize),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic,
        }));

        var tensor = new DenseTensor<float>(new[] { 1, 3, OrientSize, OrientSize });
        int plane = OrientSize * OrientSize;
        Memory<float> bufferMem = tensor.Buffer;

        resized.ProcessPixelRows(accessor =>
        {
            var buffer = bufferMem.Span;
            for (int y = 0; y < OrientSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                int rowOffset = y * OrientSize;
                for (int x = 0; x < OrientSize; x++)
                {
                    var px = row[x];
                    int idx = rowOffset + x;
                    buffer[idx] = (px.R / 255f - Mean[0]) / Std[0];            // R channel
                    buffer[plane + idx] = (px.G / 255f - Mean[1]) / Std[1];     // G channel
                    buffer[2 * plane + idx] = (px.B / 255f - Mean[2]) / Std[2]; // B channel
                }
            }
        });

        return tensor;
    }

    /// <summary>
    /// Runs the UVDoc unwarp model on <paramref name="image"/> and returns a new dewarped image at the
    /// <i>original</i> page resolution. The caller owns and disposes the returned image.
    /// </summary>
    /// <remarks>
    /// Verified against the real <c>UVDoc.onnx</c> export: the model takes input <c>image</c>
    /// <c>[N,3,H,W]</c> (RGB, CHW, simply <c>pixel/255</c> — <b>no</b> ImageNet mean/std; ImageNet
    /// normalization drives the output out of [0,1] and corrupts it) and emits <c>[N,3,H,W]</c> which is a
    /// <b>rectified RGB image</b> in [0,1] at the same spatial size as the input — not a sampling grid or
    /// flow field, so no <c>grid_sample</c> remap is needed. We feed the canonical 488×712 size, decode the
    /// returned image, and resize it back to the original page dimensions so downstream stages see the page
    /// at its expected resolution.
    /// </remarks>
    private Image<Rgb24> Unwarp(Image<Rgb24> image)
    {
        int originalWidth = image.Width;
        int originalHeight = image.Height;

        var input = BuildUnwarpTensor(image);

        using var results = _unwarp!.Run(
            new[] { NamedOnnxValue.CreateFromTensor(_unwarpInputName!, input) });

        // Output [1,3,Ho,Wo] = dewarped RGB image in [0,1], CHW. Ho,Wo equal the fed input size (712×488).
        var output = results[0].AsTensor<float>();
        int outChannels = output.Dimensions[1];
        int outHeight = output.Dimensions[2];
        int outWidth = output.Dimensions[3];
        if (outChannels < 3)
        {
            // Unexpected (the real model emits 3 channels). Fall back to the input rather than crash.
            return image;
        }

        var dewarped = DecodeUnwarpOutput(output, outWidth, outHeight);
        try
        {
            // Resize the rectified page back to the original resolution the rest of the pipeline expects.
            if (dewarped.Width != originalWidth || dewarped.Height != originalHeight)
            {
                dewarped.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(originalWidth, originalHeight),
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Bicubic,
                }));
            }
            return dewarped;
        }
        catch
        {
            dewarped.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Preprocesses <paramref name="image"/> into the UVDoc model's <c>[1,3,712,488]</c> input: resize
    /// (stretch) to 488×712, scale to [0,1] (<c>pixel/255</c>, no ImageNet normalization), RGB, CHW layout.
    /// </summary>
    private static DenseTensor<float> BuildUnwarpTensor(Image<Rgb24> image)
    {
        using var resized = image.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(UnwarpWidth, UnwarpHeight),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Triangle, // bilinear
        }));

        var tensor = new DenseTensor<float>(new[] { 1, 3, UnwarpHeight, UnwarpWidth });
        int plane = UnwarpWidth * UnwarpHeight;
        Memory<float> bufferMem = tensor.Buffer;

        resized.ProcessPixelRows(accessor =>
        {
            var buffer = bufferMem.Span;
            for (int y = 0; y < UnwarpHeight; y++)
            {
                var row = accessor.GetRowSpan(y);
                int rowOffset = y * UnwarpWidth;
                for (int x = 0; x < UnwarpWidth; x++)
                {
                    var px = row[x];
                    int idx = rowOffset + x;
                    buffer[idx] = px.R / 255f;                 // R channel
                    buffer[plane + idx] = px.G / 255f;         // G channel
                    buffer[2 * plane + idx] = px.B / 255f;     // B channel
                }
            }
        });

        return tensor;
    }

    /// <summary>
    /// Converts the UVDoc <c>[1,3,H,W]</c> [0,1] RGB output tensor into an <see cref="Image{Rgb24}"/>,
    /// clamping each channel into [0,255]. The returned image is <paramref name="width"/>×<paramref name="height"/>.
    /// </summary>
    /// <remarks>
    /// The tensor is CHW (channel-planar): the first <c>H*W</c> values are the red plane, the next the green,
    /// the next the blue. When the runtime hands back a contiguous <see cref="DenseTensor{T}"/> we read its
    /// buffer span directly (fast path, matching the rest of the codebase); otherwise we fall back to
    /// 4-index access <c>output[0, c, y, x]</c>.
    /// </remarks>
    private static Image<Rgb24> DecodeUnwarpOutput(Tensor<float> output, int width, int height)
    {
        int plane = width * height;
        var result = new Image<Rgb24>(width, height);
        try
        {
            if (output is DenseTensor<float> dense)
            {
                var data = dense.Buffer.Span;
                // Leading singleton/batch dims are absorbed by the offset of the last 3*H*W contiguous values.
                int baseOffset = data.Length - 3 * plane;
                if (baseOffset < 0) baseOffset = 0;
                int rOff = baseOffset, gOff = baseOffset + plane, bOff = baseOffset + 2 * plane;
                result.ProcessPixelRows(accessor =>
                {
                    var d = dense.Buffer.Span;
                    for (int y = 0; y < height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        int rowOffset = y * width;
                        for (int x = 0; x < width; x++)
                        {
                            int idx = rowOffset + x;
                            row[x] = new Rgb24(ToByte(d[rOff + idx]), ToByte(d[gOff + idx]), ToByte(d[bOff + idx]));
                        }
                    }
                });
            }
            else
            {
                result.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < width; x++)
                        {
                            row[x] = new Rgb24(
                                ToByte(output[0, 0, y, x]),
                                ToByte(output[0, 1, y, x]),
                                ToByte(output[0, 2, y, x]));
                        }
                    }
                });
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Scales a [0,1] (clamped) float to a 0–255 byte.
    /// </summary>
    private static byte ToByte(float v)
    {
        float scaled = v * 255f;
        if (scaled <= 0f) return 0;
        if (scaled >= 255f) return 255;
        return (byte)(scaled + 0.5f);
    }

    /// <summary>
    /// Returns the index of the largest score in <paramref name="scores"/> (0 when empty).
    /// </summary>
    private static int ArgMax(ReadOnlySpan<float> scores)
    {
        if (scores.Length == 0) return 0;
        int best = 0;
        float max = scores[0];
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

    /// <summary>
    /// Disposes the owned orientation / unwarp sessions.
    /// </summary>
    public void Dispose()
    {
        _orientation?.Dispose();
        _unwarp?.Dispose();
    }
}
