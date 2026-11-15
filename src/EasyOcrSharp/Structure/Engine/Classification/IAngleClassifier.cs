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
    /// <c>Rotated</c> is true when the model's <c>argmax</c> label is 180° (upside-down); <c>Score</c> is
    /// the classifier's confidence (0–1) in that label. This is the model's raw verdict: callers decide
    /// whether to act on it, gating with <see cref="Models.RecognitionOptions.TextLineOrientationThreshold"/>
    /// so that low-confidence misfires do not flip upright text.
    /// </returns>
    (bool Rotated, float Score) Classify(Image<Rgb24> crop);

    /// <summary>
    /// Classifies many crops in fixed-size batches (the model input is a fixed 80×160, so batching never
    /// pads). The result is positional: element <c>i</c> is the raw verdict for <paramref name="crops"/>[i].
    /// </summary>
    /// <param name="crops">The upright text-line crops to classify (caller retains ownership of each).</param>
    /// <param name="maxDegreeOfParallelism">Maximum threads used to build each batch's input tensor.</param>
    /// <returns>One (rotated, score) verdict per crop, in input order.</returns>
    IReadOnlyList<(bool Rotated, float Score)> Classify(IReadOnlyList<Image<Rgb24>> crops, int maxDegreeOfParallelism);
}
