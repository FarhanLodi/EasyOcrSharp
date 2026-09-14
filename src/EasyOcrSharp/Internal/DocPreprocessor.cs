using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Document pre-processor. When enabled, runs the PP-LCNet document-orientation classifier (0/90/180/270°)
/// to upright the page and/or the UVDoc unwarp model to dewarp it, before text detection. Either model
/// may be absent (<c>null</c> session), in which case that stage is skipped.
/// <para>
/// Orientation: PP-LCNet doc-ori (input <c>x</c> <c>[N,3,224,224]</c> → output <c>[N,4]</c> over
/// {0°,90°,180°,270°}) → argmax → in-place 90° rotation. The page is stretched to 224×224 (RGB,
/// ImageNet-normalized) rather than PaddleX's resize-short-256 + center-crop-224, which measurably
/// mis-classified multi-column pages; see <see cref="BuildOrientationTensor"/>.
/// </para>
/// <para>
/// Unwarp: UVDoc (input <c>image</c> <c>[N,3,H,W]</c>, <b>BGR</b>, <c>pixel/255</c> → output
/// <c>[N,3,H,W]</c>, a rectified <b>BGR</b> image in [0,1] at the input resolution — verified against the
/// real <c>UVDoc.onnx</c> export; it is a picture, not a sampling grid/flow). PaddleX reads the page as BGR
/// and feeds it at its own resolution with no resize, then flips the output back (<c>[:, :, ::-1]</c>).
/// We do the same, except the working resolution is capped at <see cref="UnwarpMaxSide"/> px on the longest
/// side to bound memory (the dewarped result is upscaled back to the page size only when that cap applied),
/// and H/W are edge-padded to a multiple of <see cref="UnwarpStride"/> and cropped back afterwards.
/// </para>
/// Reference: PaddleX <c>image_classification</c> / <c>image_unwarping</c> predictors, and the PaddleOcrNet
/// 2.1.0 port of the same stages.
/// </summary>
internal sealed class DocPreprocessor : IDisposable
{
    // ImageNet mean/std (RGB), applied to pixel/255 — the PP-LCNet doc-ori classifier's normalization.
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    /// <summary>PP-LCNet_x1_0_doc_ori's fixed square input (and PaddleX's center-crop size).</summary>
    internal const int OrientSize = 224;


    /// <summary>Longest side of the UVDoc working image; larger pages are downscaled (and upscaled back).</summary>
    internal const int UnwarpMaxSide = 2000;

    /// <summary>UVDoc input H/W are padded up to a multiple of this so the conv graph downsamples cleanly.</summary>
    internal const int UnwarpStride = 32;

    // Output index -> clockwise rotation (degrees) PaddleX assigns to the page. Index i means "the page is
    // currently rotated by OrientationAngles[i]° clockwise from upright"; we rotate by the inverse to correct.
    // Verified empirically: feeding a page rotated 0/90/180/270° CW yields argmax 0/1/2/3 respectively.
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
    /// Cancellation is observed before each model pass.
    /// </remarks>
    public (Image<Rgb24> image, int rotationApplied) Apply(
        Image<Rgb24> input, bool useOrientation, bool useUnwarp, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        // Work on an owned copy so the input remains the caller's to dispose and intermediate stages can be
        // swapped without aliasing. Each stage replaces 'current' and disposes the one it consumed.
        Image<Rgb24> current = input.Clone();
        int rotationApplied = 0;

        try
        {
            if (useOrientation && _orientation is not null)
            {
                rotationApplied = ClassifyAndRotate(current);
            }

            if (useUnwarp && _unwarp is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
    private int ClassifyAndRotate(Image<Rgb24> image)
    {
        var input = BuildOrientationTensor(image);

        using var results = _orientation!.Run(
            new[] { NamedOnnxValue.CreateFromTensor(_orientationInputName!, input) });

        // Output is [1, 4]: probabilities over {0°, 90°, 180°, 270°}. Take the argmax label.
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
    /// <remarks>
    /// Deliberately NOT PaddleX's <c>ResizeImage resize_short=256</c> + center <c>CropImage 224</c>. That parity
    /// preprocessing was implemented and measured on the 100-page synthetic corpus rotated 0/90/180/270:
    /// 383/400 correct verdicts against 395/400 for this stretch (C# spot check 28/36 vs 35/36). The center
    /// crop lands on column gutters, so upright multi-column pages were classified as 180°. The stretch sees
    /// the whole page, and this option exists to fix whole-page rotation, so it is kept.
    /// </remarks>
    internal static DenseTensor<float> BuildOrientationTensor(Image<Rgb24> image)
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
                    buffer[idx] = (px.R / 255f - Mean[0]) / Std[0];            // R plane
                    buffer[plane + idx] = (px.G / 255f - Mean[1]) / Std[1];     // G plane
                    buffer[2 * plane + idx] = (px.B / 255f - Mean[2]) / Std[2]; // B plane
                }
            }
        });

        return tensor;
    }

    /// <summary>
    /// UVDoc working geometry for a page: the (possibly memory-capped) working size the page is resampled to,
    /// and the stride-padded tensor size fed to the model. <see cref="Downscaled"/> is true when the cap
    /// applied, i.e. when the dewarped output must be upscaled back to the page size.
    /// </summary>
    internal readonly record struct UnwarpGeometry(int WorkWidth, int WorkHeight, int PaddedWidth, int PaddedHeight)
    {
        /// <summary>Whether the working size is smaller than the page (the memory cap applied).</summary>
        public bool Downscaled(int pageWidth, int pageHeight) => WorkWidth != pageWidth || WorkHeight != pageHeight;
    }

    /// <summary>
    /// Computes the UVDoc working size (the page's own size, scaled so its longest side is at most
    /// <paramref name="maxSide"/>) and the input tensor size (each side rounded up to a multiple of
    /// <paramref name="stride"/>).
    /// </summary>
    internal static UnwarpGeometry ComputeUnwarpGeometry(int width, int height, int maxSide = UnwarpMaxSide, int stride = UnwarpStride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSide, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, 1);

        int workW = width, workH = height;
        int longest = Math.Max(width, height);
        if (longest > maxSide)
        {
            double scale = (double)maxSide / longest;
            workW = Math.Clamp((int)Math.Round(width * scale), 1, maxSide);
            workH = Math.Clamp((int)Math.Round(height * scale), 1, maxSide);
        }

        int padW = (workW + stride - 1) / stride * stride;
        int padH = (workH + stride - 1) / stride * stride;
        return new UnwarpGeometry(workW, workH, padW, padH);
    }

    /// <summary>
    /// Runs the UVDoc unwarp model on <paramref name="image"/> and returns a new dewarped image at the
    /// page's original resolution. The caller owns and disposes the returned image.
    /// </summary>
    private Image<Rgb24> Unwarp(Image<Rgb24> image)
    {
        int originalWidth = image.Width;
        int originalHeight = image.Height;
        var g = ComputeUnwarpGeometry(originalWidth, originalHeight);

        var input = BuildUnwarpTensor(image, g);

        using var results = _unwarp!.Run(
            new[] { NamedOnnxValue.CreateFromTensor(_unwarpInputName!, input) });

        // Output [1,3,Ho,Wo] = dewarped BGR image in [0,1], CHW, Ho/Wo equal to the padded input size.
        var output = results[0].AsTensor<float>();
        if (output.Dimensions.Length != 4 || output.Dimensions[1] < 3)
        {
            // Unexpected (the real model emits [1,3,H,W]). Fall back to the input rather than crash.
            return image;
        }
        int outHeight = output.Dimensions[2];
        int outWidth = output.Dimensions[3];

        // Drop the stride padding: only the top-left working-size window is page content.
        var dewarped = DecodeUnwarpOutput(output, outWidth, outHeight,
            Math.Min(g.WorkWidth, outWidth), Math.Min(g.WorkHeight, outHeight));
        try
        {
            // Upscale back to the page size only when the memory cap shrank the working size.
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
    /// Preprocesses <paramref name="image"/> into the UVDoc model's <c>[1,3,PaddedHeight,PaddedWidth]</c>
    /// input: bilinear resize to the working size only when the memory cap shrank it, <c>pixel/255</c> (no
    /// ImageNet normalization — it drives the output out of [0,1]), <b>BGR</b> plane order (what the model
    /// was trained on), CHW layout, with the last row/column edge-replicated into the stride padding.
    /// </summary>
    internal static DenseTensor<float> BuildUnwarpTensor(Image<Rgb24> image, UnwarpGeometry g)
    {
        int workW = g.WorkWidth, workH = g.WorkHeight, padW = g.PaddedWidth, padH = g.PaddedHeight;

        Image<Rgb24>? resized = null;
        var source = image;
        if (image.Width != workW || image.Height != workH)
        {
            resized = image.Clone(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(workW, workH),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Triangle, // bilinear
            }));
            source = resized;
        }

        try
        {
            var tensor = new DenseTensor<float>(new[] { 1, 3, padH, padW });
            int plane = padW * padH;
            Memory<float> bufferMem = tensor.Buffer;

            source.ProcessPixelRows(accessor =>
            {
                var buffer = bufferMem.Span;
                for (int y = 0; y < padH; y++)
                {
                    var row = accessor.GetRowSpan(Math.Min(y, workH - 1));
                    int rowOffset = y * padW;
                    for (int x = 0; x < padW; x++)
                    {
                        var px = row[Math.Min(x, workW - 1)];
                        int idx = rowOffset + x;
                        buffer[idx] = px.B / 255f;                 // plane 0 = B (model consumes BGR)
                        buffer[plane + idx] = px.G / 255f;         // plane 1 = G
                        buffer[2 * plane + idx] = px.R / 255f;     // plane 2 = R
                    }
                }
            });

            return tensor;
        }
        finally
        {
            resized?.Dispose();
        }
    }

    /// <summary>
    /// Converts the UVDoc <c>[1,3,H,W]</c> [0,1] <b>BGR</b> output tensor into an <see cref="Image{Rgb24}"/>
    /// (flipping the planes back to RGB, as PaddleX's <c>im[:, :, ::-1]</c>), clamping into [0,255]. Only the
    /// top-left <paramref name="cropW"/>×<paramref name="cropH"/> window is decoded — the stride padding that
    /// was added to the input is discarded here.
    /// </summary>
    /// <remarks>
    /// When the runtime hands back a contiguous <see cref="DenseTensor{T}"/> we read its buffer span directly
    /// (fast path, matching the rest of the codebase); otherwise we fall back to 4-index access.
    /// </remarks>
    internal static Image<Rgb24> DecodeUnwarpOutput(Tensor<float> output, int width, int height, int cropW, int cropH)
    {
        int plane = width * height;
        var result = new Image<Rgb24>(cropW, cropH);
        try
        {
            if (output is DenseTensor<float> dense)
            {
                // Leading singleton/batch dims are absorbed by the offset of the last 3*H*W contiguous values.
                int baseOffset = Math.Max(0, dense.Buffer.Length - 3 * plane);
                int bOff = baseOffset, gOff = baseOffset + plane, rOff = baseOffset + 2 * plane;
                result.ProcessPixelRows(accessor =>
                {
                    var d = dense.Buffer.Span;
                    for (int y = 0; y < cropH; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        int rowOffset = y * width;
                        for (int x = 0; x < cropW; x++)
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
                    for (int y = 0; y < cropH; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < cropW; x++)
                        {
                            row[x] = new Rgb24(
                                ToByte(output[0, 2, y, x]),  // R = plane 2 of the BGR output
                                ToByte(output[0, 1, y, x]),  // G
                                ToByte(output[0, 0, y, x])); // B = plane 0
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
