namespace EasyOcrSharp.Models;

/// <summary>
/// Optional image clean-up applied before detection/recognition — useful for scanned documents
/// and photos. The clean-up steps are off by default; <see cref="CorrectNonSquarePixels"/> is on because it
/// only acts on images whose metadata declares non-square pixels and reports coordinates on the original
/// pixel grid. When <see cref="DocumentOrientation"/> or <see cref="DocumentUnwarp"/> rotate or rectify the
/// image, reported bounding boxes are in the corrected image's coordinate space. <see cref="Deskew"/> and
/// <see cref="DetectOrientation"/> report boxes on the image that was passed in (after any document
/// correction).
/// </summary>
public sealed record PreprocessingOptions
{
    /// <summary>
    /// Auto-correct small skew angles (±15°) using a projection-profile estimate, so slightly
    /// tilted scans are straightened before OCR. Detection and recognition run on the straightened (rotated,
    /// slightly enlarged) canvas; line, word and character boxes are mapped back onto the unrotated image, and
    /// the rotation applied is reported as <see cref="OcrResult.DeskewAngle"/>. A <see cref="RecognitionOptions.Region"/>
    /// is given in the unrotated image's coordinates, as without deskew.
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

    /// <summary>
    /// Resample images whose metadata declares non-square pixels — fax pages scanned at 204×98 DPI, which
    /// otherwise reach the detector squashed to half height — to square pixels before OCR, then scale every
    /// line, word and character box back onto the original pixel grid (<see cref="OcrResult.SourceWidth"/> /
    /// <see cref="OcrResult.SourceHeight"/> stay the original size). The lower-resolution axis is brought up
    /// to the higher one. Images without resolution metadata, with square pixels (the two resolutions within
    /// 15 % of each other), or declaring an implausible aspect (more than 4:1) are not touched: the pipeline
    /// runs on the very image passed in, exactly as with the option off. A pixel
    /// <see cref="RecognitionOptions.Region"/> is given on the original grid. Applies to the
    /// <c>ExtractTextFromImage</c> and streaming calls; <c>DetectRegionsAsync</c> ignores preprocessing.
    /// Default true.
    /// </summary>
    public bool CorrectNonSquarePixels { get; init; } = true;

    /// <summary>
    /// No clean-up steps (the default). Non-square pixel correction stays on; see
    /// <see cref="CorrectNonSquarePixels"/>.
    /// </summary>
    public static PreprocessingOptions None { get; } = new();

    /// <summary>
    /// Whether any clean-up step is enabled. <see cref="CorrectNonSquarePixels"/> is not counted: it is on by
    /// default and does nothing unless the image declares non-square pixels.
    /// </summary>
    public bool IsAnyEnabled => Deskew || DetectOrientation || Binarize || Denoise || Sharpen || DocumentOrientation || DocumentUnwarp;
}
