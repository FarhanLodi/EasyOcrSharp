namespace EasyOcrSharp.Models;

/// <summary>
/// Optional image clean-up applied before detection/recognition — useful for scanned documents
/// and photos. All steps are off by default. When <see cref="Deskew"/>,
/// <see cref="DetectOrientation"/>, <see cref="DocumentOrientation"/> or
/// <see cref="DocumentUnwarp"/> rotate or rectify the image, reported bounding boxes are in the
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

    /// <summary>
    /// Sharpen the document with an unsharp mask (Gaussian) before OCR — recovers soft, slightly
    /// out-of-focus or low-DPI scans where glyph edges are blurry. Strength is controlled by
    /// <see cref="SharpenAmount"/>. Applied after <see cref="Denoise"/>/<see cref="Deskew"/> so noise
    /// isn't amplified.
    /// </summary>
    public bool Sharpen { get; init; }

    /// <summary>
    /// Sharpening strength (the Gaussian sigma of the unsharp mask) used when <see cref="Sharpen"/> is
    /// enabled. Sensible range 0.5–3; higher is stronger. Default 1.0.
    /// </summary>
    public float SharpenAmount { get; init; } = 1.0f;

    /// <summary>
    /// Correct whole-page 90°/180°/270° rotation with PaddleOCR's PP-LCNet document-orientation
    /// classifier (a single tiny model pass) instead of OCR-ing all four orientations. Much cheaper
    /// than <see cref="DetectOrientation"/> and usually just as accurate on document scans. The model
    /// (~7 MB) downloads on first use. Reported bounding boxes are in the corrected image's coordinate
    /// space.
    /// </summary>
    public bool DocumentOrientation { get; init; }

    /// <summary>
    /// Dewarp a curved or folded page (photographed book pages, creased receipts) with PaddleOCR's
    /// UVDoc unwarp model before OCR. The model (~11 MB) downloads on first use. Reported bounding
    /// boxes are in the dewarped image's coordinate space.
    /// </summary>
    public bool DocumentUnwarp { get; init; }

    /// <summary>No preprocessing (the default).</summary>
    public static PreprocessingOptions None { get; } = new();

    /// <summary>Whether any preprocessing step is enabled.</summary>
    public bool IsAnyEnabled => Deskew || DetectOrientation || Binarize || Denoise || Sharpen || DocumentOrientation || DocumentUnwarp;
}
