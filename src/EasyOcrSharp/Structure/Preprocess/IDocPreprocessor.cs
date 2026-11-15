using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Structure.Preprocess;

/// <summary>
/// Document pre-processor: optionally corrects whole-page orientation (0/90/180/270°) and/or unwarps a
/// curved/skewed page before layout detection. Implemented by <see cref="DocPreprocessor"/>. Owns and
/// disposes its optional orientation / unwarp sessions.
/// </summary>
internal interface IDocPreprocessor : IDisposable
{
    /// <summary>
    /// Pre-processes a page image.
    /// </summary>
    /// <param name="input">The page image (caller retains ownership of the input; it is never modified or disposed).</param>
    /// <param name="useOrientation">When true, run the orientation classifier and rotate the page upright.</param>
    /// <param name="useUnwarp">When true, run the unwarp model and dewarp the page.</param>
    /// <returns>
    /// The processed page, the rotation applied to upright it, and whether the returned image is a new
    /// image the caller must dispose. When no stage changed the pixels the input itself is returned with
    /// <see cref="DocPreprocessResult.OwnsImage"/> false.
    /// </returns>
    DocPreprocessResult Apply(Image<Rgb24> input, bool useOrientation, bool useUnwarp);
}

/// <summary>
/// The outcome of <see cref="IDocPreprocessor.Apply"/>.
/// </summary>
/// <param name="Image">
/// The processed page. This is either a new image or, when no stage changed the pixels, the caller's input
/// itself.
/// </param>
/// <param name="RotationApplied">The clockwise rotation (degrees) applied to upright the page; 0 when none.</param>
/// <param name="OwnsImage">
/// True when <paramref name="Image"/> is a new image the caller must dispose; false when it is the caller's
/// own input, which must not be disposed on its behalf.
/// </param>
/// <param name="UnwarpApplied">True only when the nonlinear model produced a replacement image.</param>
internal readonly record struct DocPreprocessResult(Image<Rgb24> Image, int RotationApplied, bool OwnsImage, bool UnwarpApplied = false);
