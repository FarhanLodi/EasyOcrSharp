namespace EasyOcrSharp.Models;

/// <summary>
/// Optional image clean-up applied before detection/recognition — useful for scanned documents
/// and photos. All steps are off by default. When <see cref="Deskew"/> or
/// <see cref="DetectOrientation"/> rotate the image, reported bounding boxes are in the
/// corrected image's coordinate space.
/// </summary>
public sealed record PreprocessingOptions
{
    /// <summary>
    /// Auto-correct small skew angles (±15°) using a projection-profile estimate, so slightly
    /// tilted scans are straightened before OCR.
    /// </summary>
    public bool Deskew { get; init; }

    /// <summary>
    /// Detect and correct 90°/180°/270° page rotation by running OCR at all four orientations and
    /// keeping the highest-confidence one. Accurate but ~4× the cost — enable only when input
    /// orientation is unknown.
    /// </summary>
    public bool DetectOrientation { get; init; }

    /// <summary>
    /// Binarize to black-and-white with adaptive (local) thresholding. Helps documents with uneven
    /// lighting or faint print.
    /// </summary>
    public bool Binarize { get; init; }

    /// <summary>Apply a light blur to suppress speckle/scanner noise before thresholding.</summary>
    public bool Denoise { get; init; }

    /// <summary>No preprocessing (the default).</summary>
    public static PreprocessingOptions None { get; } = new();

    /// <summary>Whether any preprocessing step is enabled.</summary>
    public bool IsAnyEnabled => Deskew || DetectOrientation || Binarize || Denoise;
}
