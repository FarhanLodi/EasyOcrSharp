namespace EasyOcrSharp.Structure.Engine.Models;

/// <summary>
/// Image clean-up applied before OCR. All steps are off by default. Deskew/denoise/binarize run on a
/// working copy; <see cref="DetectOrientation"/> is handled at the service level (it scores each 90°
/// rotation using the OCR result).
/// </summary>
internal sealed record PreprocessingOptions
{
    /// <summary>
    /// Apply a light Gaussian denoise before detection. Default false.
    /// </summary>
    public bool Denoise { get; init; }

    /// <summary>
    /// Estimate and correct a small skew angle via projection profiles. Default false.
    /// </summary>
    public bool Deskew { get; init; }

    /// <summary>
    /// Adaptive-threshold binarization (helpful for low-contrast scans). Default false.
    /// </summary>
    public bool Binarize { get; init; }

    /// <summary>
    /// Detect page orientation by running OCR at 0/90/180/270° and keeping the strongest result.
    /// Default false. Handled at the service level. PaddleOCR's <c>use_textline_orientation</c> covers
    /// per-line 180° flips separately via the angle classifier.
    /// </summary>
    public bool DetectOrientation { get; init; }

    /// <summary>
    /// No preprocessing (the default).
    /// </summary>
    public static PreprocessingOptions None { get; } = new();
}
