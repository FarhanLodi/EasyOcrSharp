using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Structure.Engine.Classification;

/// <summary>
/// Text-line orientation classifier: decides whether a rectified text-line crop is rotated 180°
/// (upside-down) and should be flipped before recognition. Implemented by
/// <see cref="TextLineClassifier"/> (PaddleOCR's <c>cls</c> model). PaddleOCR's
/// <c>use_textline_orientation</c> gates whether this runs at all.
/// </summary>
internal interface IAngleClassifier : IDisposable
{
    /// <summary>
    /// Classifies a single rectified text-line crop.
    /// </summary>
    /// <param name="crop">The upright text-line crop to classify (caller retains ownership).</param>
    /// <returns>
    /// <c>Rotated</c> is true when the crop is 180° (upside-down) and should be flipped; <c>Score</c> is
    /// the classifier's confidence (0–1) in the chosen label.
    /// </returns>
    (bool Rotated, float Score) Classify(Image<Rgb24> crop);
}
