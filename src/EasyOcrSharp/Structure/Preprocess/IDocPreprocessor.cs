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
    /// <param name="input">The page image (caller retains ownership of the input).</param>
    /// <param name="useOrientation">When true, run the orientation classifier and rotate the page upright.</param>
    /// <param name="useUnwarp">When true, run the unwarp model and dewarp the page.</param>
    /// <returns>
    /// The processed image (a new image the caller must dispose) and the rotation in degrees that was
    /// applied to upright the page (0 when none / orientation disabled).
    /// </returns>
    (Image<Rgb24> image, int rotationApplied) Apply(Image<Rgb24> input, bool useOrientation, bool useUnwarp);
}
