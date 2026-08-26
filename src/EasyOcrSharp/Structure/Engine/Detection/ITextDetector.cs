using EasyOcrSharp.Structure.Engine.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Structure.Engine.Detection;

/// <summary>
/// Locates text regions in an image. Implemented by <see cref="DbTextDetector"/> (PaddleOCR's
/// DB / DBNet detector). Returns one <see cref="TextQuad"/> per detected box, in original-image
/// pixel coordinates.
/// </summary>
internal interface ITextDetector : IDisposable
{
    /// <summary>
    /// Runs detection on <paramref name="image"/> and returns the located text quadrilaterals.
    /// </summary>
    /// <param name="image">The image to detect text in (caller retains ownership).</param>
    /// <param name="options">DB detection thresholds (limit_side_len, box/det thresholds, unclip ratio, ...).</param>
    /// <returns>The detected quads; empty when no text is found.</returns>
    IReadOnlyList<TextQuad> Detect(Image<Rgb24> image, DetectionOptions options);
}
