using EasyOcrSharp.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

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
    /// <summary>Largest absolute page skew corrected, in degrees. Beyond this the page is a rotation, not a skew.</summary>
    private const float MaxSkewDegrees = 15f;

    /// <summary>
    /// Skews below this are left alone, so an already-straight page is never resampled. This is the
    /// threshold the hand-rolled estimator used; the deskew has a stricter gate of its own (it also
    /// declines when the best angle is not meaningfully sharper than no rotation), so this is a floor,
    /// not the exact cut-off.
    /// </summary>
    private const float MinSkewDegrees = 0.1f;

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
                // Projection-profile deskew: the rotation that sharpens the horizontal projection of the
                // ink straightens the text lines. Same estimator this used to hand-roll, but it scores
                // candidate angles on the ink coordinates instead of rotating the whole page once per
                // candidate, and it fills the corners exposed by the rotation with white in the same pass
                // (so binarization and detection never see black triangles).
                img.Mutate(c => c.Deskew(new DeskewOptions
                {
                    Method = DeskewMethod.Projection,
                    MaxAngle = MaxSkewDegrees,
                    MinAngle = MinSkewDegrees,
                    FillColor = Color.White,
                }));
            }

            if (options.Sharpen)
            {
                // Unsharp mask; clamp the caller-supplied strength into a range that can't destroy
                // the glyphs (0 disables sharpening entirely inside EasyImageSharp, huge sigmas ring).
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
}
