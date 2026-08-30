using System.Buffers;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Image preprocessing helpers shared by the CRAFT detector and CRNN recognizer.
/// All routines work on EasyImageSharp images and return CHW float tensors
/// directly consumable by ONNX Runtime.
/// </summary>
internal static class ImageProcessing
{
    /// <summary>
    /// Minimum width (px) of a recognizer input. The CRNN's conv/pooling stack downsamples width ~4×;
    /// a thinner input (a 1–2px sliver from a noisy scan) collapses to a zero/one-width feature map and
    /// makes ONNX Runtime throw "Invalid input shape". Narrow crops are edge-padded up to this width.
    /// </summary>
    private const int MinCrnnWidth = 16;


    /// <summary>
    /// EasyOCR's CRAFT preprocessing: resize so the longer edge ≤ <paramref name="canvasSize"/>,
    /// pad to a multiple of 32, normalize with ImageNet mean/std.
    /// Returns the input tensor in NCHW order plus the (heatmap → original-image) scale factors
    /// needed to project detector outputs back to source coordinates.
    /// </summary>
    public static CraftPreprocessResult PreprocessForCraft(Image<Rgb24> source, int canvasSize, double magRatio)
    {
        int srcW = source.Width;
        int srcH = source.Height;

        // Both knobs are public and unvalidated on DetectionOptions; a zero or negative value would drive
        // every dimension below to 0.
        canvasSize = Math.Max(32, canvasSize);
        magRatio = Math.Max(1e-3, magRatio);

        // EasyOCR: target_size = mag_ratio * max(h, w); then clamp to canvas; resize keeping aspect.
        double targetSize = magRatio * Math.Max(srcH, srcW);
        if (targetSize > canvasSize) targetSize = canvasSize;

        double ratio = targetSize / Math.Max(srcH, srcW);

        // Floor both edges at 1px. On an extreme aspect ratio — a 10000x3 receipt strip, a 2561x1 rule —
        // the short edge truncates to 0, Resize(w, 0) silently derives 1 from the aspect ratio, and the
        // zero then survives the pad-to-32 rounding below (which maps 0 -> 0) into the tensor shape.
        // ONNX Runtime rejects that with "Invalid input shape: {0,2560}" from inside the CRAFT Conv node,
        // and nothing on the path catches it, so the whole OCR call fails.
        int targetH = Math.Max(1, (int)(srcH * ratio));
        int targetW = Math.Max(1, (int)(srcW * ratio));

        // Pad to multiple of 32 (CRAFT requirement), never to zero.
        int padH = Math.Max(32, (targetH + 31) / 32 * 32);
        int padW = Math.Max(32, (targetW + 31) / 32 * 32);

        using var resized = source.Clone(ctx => ctx.Resize(targetW, targetH));

        // Allocate padded canvas, copy resized image into top-left. Computed as long: canvasSize is caller
        // -supplied, and 3 * padH * padW overflows int well before the allocation would fail on its own.
        long tensorLength = 3L * padH * padW;
        if (tensorLength > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(canvasSize), canvasSize,
                $"DetectionOptions.CanvasSize {canvasSize} with MagRatio {magRatio} needs a {tensorLength}-element " +
                "input tensor, which exceeds the maximum array length. Lower CanvasSize or MagRatio.");
        }
        var tensor = new float[(int)tensorLength];
        // ImageNet normalization in 0-255 space: (pixel - mean*255) / (std*255)
        // mean = (0.485, 0.456, 0.406), std = (0.229, 0.224, 0.225)
        const float meanR = 0.485f * 255f, meanG = 0.456f * 255f, meanB = 0.406f * 255f;
        const float stdR = 0.229f * 255f, stdG = 0.224f * 255f, stdB = 0.225f * 255f;

        int hwPad = padH * padW;

        // Pre-fill with normalized BLACK, then overwrite the real pixels below. EasyOCR pads in *pixel*
        // space with zeros and normalizes afterwards, so its padding lands at (0 - mean*255)/(std*255) ≈ -2.1.
        // Leaving the tensor at 0.0 instead encodes pixel ≈ (124,116,104) — mid-grey — which for an extreme
        // aspect ratio is most of the canvas (a 5000x2 image resizes to height 1 and pads to 32 rows).
        Array.Fill(tensor, (0f - meanR) / stdR, 0 * hwPad, hwPad);
        Array.Fill(tensor, (0f - meanG) / stdG, 1 * hwPad, hwPad);
        Array.Fill(tensor, (0f - meanB) / stdB, 2 * hwPad, hwPad);

        resized.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < targetH; y++)
            {
                var row = rows.GetRowSpan(y);
                int baseIdx = y * padW;
                for (int x = 0; x < targetW; x++)
                {
                    var px = row[x];
                    tensor[0 * hwPad + baseIdx + x] = (px.R - meanR) / stdR;
                    tensor[1 * hwPad + baseIdx + x] = (px.G - meanG) / stdG;
                    tensor[2 * hwPad + baseIdx + x] = (px.B - meanB) / stdB;
                }
            }
        });
        // Padded regions stay 0 (which corresponds to the negative-mean value; matches EasyOCR's zero-padded behaviour after subtraction since they pre-pad pixel space and then normalize — close enough for detection).

        // Heatmap is half resolution; the back-projection factor is heatmap_pixel * 2 / ratio = original_pixel.
        double ratioH = 1.0 / ratio;
        double ratioW = 1.0 / ratio;
        double heatmapToSourceScale = 2.0;

        return new CraftPreprocessResult(
            Tensor: tensor,
            Height: padH,
            Width: padW,
            HeatmapToSourceX: heatmapToSourceScale * ratioW,
            HeatmapToSourceY: heatmapToSourceScale * ratioH);
    }

    /// <summary>
    /// CRNN recognizer input, matching EasyOCR's preprocessing exactly: convert the crop to
    /// grayscale, resize to height <paramref name="targetHeight"/> (64) with width proportional to
    /// the aspect ratio (bicubic), optionally apply EasyOCR's grey-contrast stretch, then normalize
    /// to [-1, 1] as (pixel/255 - 0.5) / 0.5. Returns a 1×1×<paramref name="targetHeight"/>×W tensor.
    /// Width is dynamic (no fixed-canvas padding) — the ONNX graph accepts variable width, so each
    /// box is fed at its true resized width just like upstream EasyOCR's single-box inference.
    /// </summary>
    public static float[] PreprocessForCrnn(Image<Rgb24> crop, int targetHeight, int maxWidth, bool adjustContrast, out int width, double contrastTarget = 0.5)
    {
        int srcW = crop.Width;
        int srcH = crop.Height;
        if (srcH == 0 || srcW == 0)
        {
            width = 0;
            return Array.Empty<float>();
        }

        // EasyOCR: resized_w = ceil(imgH * w/h), capped at imgW (the batch max width).
        double ratio = (double)srcW / srcH;
        int contentW = (int)Math.Ceiling(targetHeight * ratio);
        if (contentW < 1) contentW = 1;
        if (contentW > maxWidth) contentW = maxWidth;

        // Guarantee a minimum width so the CRNN never receives a degenerate tensor. Narrow crops keep
        // their true aspect (resized to contentW) and are right-padded by replicating the last column —
        // EasyOCR's NormalizePAD behaviour.
        int finalW = Math.Min(maxWidth, Math.Max(contentW, MinCrnnWidth));
        width = finalW;

        using var resized = crop.Clone(ctx => ctx
            .Grayscale()
            .Resize(new ResizeOptions
            {
                Size = new Size(contentW, targetHeight),
                Sampler = KnownResamplers.Bicubic,
                Mode = ResizeMode.Stretch,
            }));

        // Pull grayscale bytes (stored equally in R/G/B) into a H×finalW buffer, edge-padding the right.
        // The scratch grey buffer is pooled (its lifetime is fully contained here); the returned tensor
        // escapes to ONNX Runtime, so it stays a plain allocation.
        int greyLen = targetHeight * finalW;
        var grey = ArrayPool<byte>.Shared.Rent(greyLen);
        try
        {
            resized.ProcessPixelRows(rows =>
            {
                for (int y = 0; y < targetHeight; y++)
                {
                    var row = rows.GetRowSpan(y);
                    int baseIdx = y * finalW;
                    for (int x = 0; x < contentW; x++)
                        grey[baseIdx + x] = row[x].R;

                    byte edge = row[contentW - 1].R; // replicate last real column into the padding
                    for (int x = contentW; x < finalW; x++)
                        grey[baseIdx + x] = edge;
                }
            });

            if (adjustContrast)
            {
                AdjustContrastGrey(grey, greyLen, target: contrastTarget);
            }

            var tensor = new float[greyLen];
            for (int i = 0; i < greyLen; i++)
            {
                tensor[i] = (grey[i] / 255f - 0.5f) / 0.5f;
            }
            return tensor;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(grey);
        }
    }

    /// <summary>
    /// EasyOCR's <c>adjust_contrast_grey</c>: if the 10th–90th percentile contrast of the grayscale
    /// patch is below <paramref name="target"/>, linearly stretch it. Operates in place on 0–255 bytes.
    /// </summary>
    private static void AdjustContrastGrey(byte[] grey, int length, double target)
    {
        // contrast = (high - low) / max(10, high + low), where high/low are the 90th/10th percentiles.
        // `grey` may be an over-sized pooled buffer, so operate strictly on the first `length` bytes.
        var sorted = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Array.Copy(grey, sorted, length);
            Array.Sort(sorted, 0, length);
            byte high = sorted[(int)(0.9 * (length - 1))];
            byte low = sorted[(int)(0.1 * (length - 1))];

            double contrast = (high - low) / Math.Max(10.0, high + low);
            if (contrast >= target) return;

            double ratio = 200.0 / Math.Max(10, high - low);
            for (int i = 0; i < length; i++)
            {
                double v = (grey[i] - low + 25) * ratio;
                grey[i] = (byte)Math.Clamp(v, 0, 255);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sorted);
        }
    }
}

internal readonly record struct CraftPreprocessResult(
    float[] Tensor,
    int Height,
    int Width,
    double HeatmapToSourceX,
    double HeatmapToSourceY);
