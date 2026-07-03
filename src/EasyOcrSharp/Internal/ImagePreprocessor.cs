using EasyOcrSharp.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Scanned-document clean-up: optional denoise, unsharp-mask sharpen, adaptive binarization, and
/// projection-profile deskew. Orientation (90°/180°/270°) detection is handled at the service level
/// because it needs the OCR result to score each rotation; the model-based document orientation /
/// unwarp steps live in <see cref="DocPreprocessor"/>. Returns a new image; the caller owns and
/// disposes it.
/// </summary>
internal static class ImagePreprocessor
{
    /// <summary>
    /// Applies denoise → deskew → sharpen → binarize (in that order) per <paramref name="options"/>.
    /// Sharpening runs after denoise/deskew so speckle noise isn't amplified, and before binarize so
    /// the threshold sees the crisper edges. Always returns a fresh image (a clone even when nothing
    /// is enabled) so the caller can dispose uniformly without touching the original.
    /// </summary>
    public static Image<Rgb24> Apply(Image<Rgb24> source, PreprocessingOptions options)
    {
        var img = source.Clone();
        try
        {
            if (options.Denoise)
            {
                img.Mutate(c => c.GaussianBlur(0.6f));
            }

            if (options.Deskew)
            {
                double angle = EstimateSkewAngle(img);
                if (Math.Abs(angle) > 0.1)
                {
                    var rotated = RotateWithWhiteBackground(img, (float)angle);
                    img.Dispose();
                    img = rotated;
                }
            }

            if (options.Sharpen)
            {
                // Unsharp mask; clamp the caller-supplied strength into a range that can't destroy
                // the glyphs (0 disables sharpening entirely inside ImageSharp, huge sigmas ring).
                float sigma = Math.Clamp(options.SharpenAmount, 0.1f, 10f);
                img.Mutate(c => c.GaussianSharpen(sigma));
            }

            if (options.Binarize)
            {
                img.Mutate(c => c.AdaptiveThreshold());
            }

            return img;
        }
        catch
        {
            img.Dispose();
            throw;
        }
    }

    /// <summary>Rotates by an exact multiple of 90° (lossless, no fill needed). Returns a new image.</summary>
    public static Image<Rgb24> RotateRightAngle(Image<Rgb24> source, int degrees)
        => source.Clone(c => c.Rotate(degrees));

    /// <summary>
    /// Estimates the correction angle (degrees) that best straightens the text using a
    /// projection-profile search: the rotation that maximizes the variance of per-row ink counts
    /// aligns text lines horizontally. Coarse pass then a fine refinement around the best coarse angle.
    /// </summary>
    private static double EstimateSkewAngle(Image<Rgb24> source)
    {
        // Downscale + binarize a working copy for speed.
        using var work = source.Clone(c =>
        {
            if (source.Width > 800) c.Resize(800, 0);
            c.Grayscale().BinaryThreshold(0.5f);
        });

        double best = SearchSkew(work, -15, 15, 1.0, out _);
        best = SearchSkew(work, best - 1.0, best + 1.0, 0.2, out _);
        return best;
    }

    private static double SearchSkew(Image<Rgb24> binary, double from, double to, double step, out double bestScore)
    {
        double best = 0;
        bestScore = -1;
        for (double a = from; a <= to + 1e-9; a += step)
        {
            using var rot = RotateWithWhiteBackground(binary, (float)a);
            double score = RowInkVariance(rot);
            if (score > bestScore)
            {
                bestScore = score;
                best = a;
            }
        }
        return best;
    }

    /// <summary>Variance across rows of the dark-pixel count per row (higher ⇒ text lines aligned).</summary>
    private static double RowInkVariance(Image<Rgb24> binary)
    {
        int h = binary.Height, w = binary.Width;
        var rowCounts = new double[h];
        binary.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                int dark = 0;
                for (int x = 0; x < w; x++)
                    if (row[x].R < 128) dark++;
                rowCounts[y] = dark;
            }
        });

        double mean = 0;
        for (int i = 0; i < h; i++) mean += rowCounts[i];
        mean /= h;
        double var = 0;
        for (int i = 0; i < h; i++) { double d = rowCounts[i] - mean; var += d * d; }
        return var / h;
    }

    /// <summary>
    /// Rotates by an arbitrary angle, filling the exposed corners with white (so binarization and
    /// detection don't see black triangles). Composites the transparent-corner rotation over a white
    /// canvas.
    /// </summary>
    private static Image<Rgb24> RotateWithWhiteBackground(Image<Rgb24> source, float degrees)
    {
        using var rgba = source.CloneAs<Rgba32>();
        rgba.Mutate(c => c.Rotate(degrees)); // exposed area is transparent
        var result = new Image<Rgb24>(rgba.Width, rgba.Height, new Rgb24(255, 255, 255));
        result.Mutate(c => c.DrawImage(rgba, 1f));
        return result;
    }
}
