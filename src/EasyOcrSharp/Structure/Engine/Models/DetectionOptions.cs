namespace EasyOcrSharp.Structure.Engine.Models;

/// <summary>
/// Low-level tuning for the DB (Differentiable Binarization) text-<b>detection</b> stage (where boxes
/// are found), exposed for difficult inputs. The defaults mirror upstream PaddleOCR and are correct for
/// most images — only change these when detection is missing or splitting text. Recognition accuracy is
/// controlled separately via <see cref="RecognitionOptions"/>.
/// </summary>
internal sealed record DetectionOptions
{
    /// <summary>
    /// Maximum size (px) of the longest side fed to the detector; larger images are scaled down to fit,
    /// then results are mapped back. PaddleOCR's <c>det_limit_side_len</c>. Default 960.
    /// </summary>
    public int LimitSideLen { get; init; } = 960;

    /// <summary>
    /// Resize policy for <see cref="LimitSideLen"/>. When <c>true</c> (PaddleOCR's <c>limit_type=max</c>,
    /// the default) the longest side is capped at <see cref="LimitSideLen"/>; when <c>false</c>
    /// (<c>limit_type=min</c>) the shortest side is brought up to it. Default true.
    /// </summary>
    public bool LimitTypeMax { get; init; } = true;

    /// <summary>
    /// Pixel-level binarization threshold (0–1) applied to the DB probability map. Pixels above this are
    /// considered text. PaddleOCR's <c>det_db_thresh</c>. Default 0.3.
    /// </summary>
    public double DetThreshold { get; init; } = 0.3;

    /// <summary>
    /// Box-level confidence floor (0–1): a candidate box whose mean probability is below this is dropped.
    /// PaddleOCR's <c>det_db_box_thresh</c>. Default 0.6.
    /// </summary>
    public double BoxThreshold { get; init; } = 0.6;

    /// <summary>
    /// Polygon expansion ratio used to "unclip" each shrunken DB contour back to the true glyph extent.
    /// Higher values grow the boxes. PaddleOCR's <c>det_db_unclip_ratio</c>. Default 1.5.
    /// </summary>
    public double UnclipRatio { get; init; } = 1.5;

    /// <summary>
    /// How box scores are computed from the probability map. PaddleOCR's <c>det_db_score_mode</c>.
    /// Default <see cref="DetectionScoreMode.Fast"/>.
    /// </summary>
    public DetectionScoreMode ScoreMode { get; init; } = DetectionScoreMode.Fast;

    /// <summary>
    /// Use dilation on the binarized map before contour extraction (PaddleOCR's <c>use_dilation</c>),
    /// which can recover thin/broken strokes. Default false.
    /// </summary>
    public bool UseDilation { get; init; }

    // TODO(box-type-poly): PaddleOCR's det_box_type ("quad" vs "poly") is not yet exposed. Adding it
    // means emitting many-point polygons (poly mode) instead of 4-point quads, which ripples into the
    // shared output models (TextQuad / DetectedRegion) owned by other code. Deferred to a dedicated task.

    /// <summary>
    /// Discard detected boxes smaller than this side length (px). PaddleOCR's <c>det_db_min_size</c>. Default 3.
    /// </summary>
    public int MinSize { get; init; } = 3;

    /// <summary>
    /// Non-maximum-suppression IoU threshold (0–1) for de-duplicating overlapping detected boxes: when two
    /// boxes overlap by more than this (axis-aligned IoU), the smaller is dropped. Default 0.6. Set to 0 to disable.
    /// </summary>
    public double NmsIouThreshold { get; init; } = 0.6;

    /// <summary>
    /// The default detection thresholds (match PaddleOCR).
    /// </summary>
    public static DetectionOptions Default { get; } = new();
}
