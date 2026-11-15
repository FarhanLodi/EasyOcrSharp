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
    /// Target side length (px) for the detector resize; PaddleOCR's <c>det_limit_side_len</c>. How it is
    /// applied depends on <see cref="LimitTypeMax"/>. Default 64 with <c>limit_type=min</c>, matching the
    /// Python PaddleOCR 3.x pipeline (<c>OCR.yaml</c>): detection runs at near-native resolution — the
    /// shortest side is only upscaled when below 64 — capped by <see cref="MaxSideLimit"/>. For the older
    /// downscale-to-960 behavior use 960 with <see cref="LimitTypeMax"/> = <c>true</c>.
    /// </summary>
    public int LimitSideLen { get; init; } = 64;

    /// <summary>
    /// Resize policy for <see cref="LimitSideLen"/>. When <c>true</c> (PaddleOCR's <c>limit_type=max</c>)
    /// the longest side is capped at <see cref="LimitSideLen"/>; when <c>false</c> (<c>limit_type=min</c>,
    /// the default, matching Python PaddleOCR 3.x) the shortest side is brought up to it and the image is
    /// otherwise left at native resolution (subject to <see cref="MaxSideLimit"/>). Default false.
    /// </summary>
    public bool LimitTypeMax { get; init; } = false;

    /// <summary>
    /// After the <see cref="LimitSideLen"/> policy is applied, the longest resized side is capped at this
    /// many pixels (aspect preserved) — prevents enormous inputs under <c>limit_type=min</c>. PaddleX's
    /// <c>max_side_limit</c>. Default 4000; values &lt;= 0 fall back to 4000.
    /// </summary>
    public int MaxSideLimit { get; init; } = 4000;

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

    /// <summary>
    /// Output geometry of detection post-processing — PaddleOCR's <c>det_box_type</c>.
    /// <see cref="DetectionBoxType.Quad"/> (the default) fits a min-area quadrilateral to every text
    /// region; <see cref="DetectionBoxType.Poly"/> keeps each region's simplified outer contour as an
    /// N-point polygon, which preserves curved text outlines (seal arcs). Poly mode is currently honored
    /// by the seal-recognition pipeline only; the general OCR detection path emits quads regardless.
    /// </summary>
    public DetectionBoxType BoxType { get; init; } = DetectionBoxType.Quad;

    /// <summary>
    /// Discard detected boxes smaller than this side length (px). PaddleOCR's <c>det_db_min_size</c>. Default 3.
    /// </summary>
    public int MinSize { get; init; } = 3;

    /// <summary>
    /// Non-maximum-suppression IoU threshold (0–1) for de-duplicating overlapping detected boxes: when two
    /// boxes overlap by more than this (axis-aligned IoU), the smaller is dropped. 0 or negative disables
    /// NMS entirely — the default, matching Python PaddleOCR, which has no post-detection NMS (nested or
    /// adjacent boxes may legitimately overlap). Set e.g. 0.6 to opt into de-duplication.
    /// </summary>
    public double NmsIouThreshold { get; init; }

    /// <summary>
    /// Small-text rescue, in source pixels. When greater than 0, the detector measures the median short
    /// side of the boxes found by a normal pass; if it is below this value the image is detected again
    /// upscaled so the typical line reaches about <c>max(24, MinTextHeight)</c> pixels (factor capped at
    /// 3× and by <see cref="MaxSideLimit"/>). A page with no boxes at all whose longest side is under
    /// 1500 px is also retried once at 2×. Boxes are always returned in original-image coordinates.
    /// Default 0 (off) — the extra pass costs a second detector run on the images it triggers for, and
    /// Python PaddleOCR has no such step.
    /// </summary>
    public int MinTextHeight { get; init; }

    /// <summary>
    /// Detect very large images in overlapping tiles instead of shrinking them. When
    /// <see cref="MaxSideLimit"/> would scale the detector input below 0.75× of the size the
    /// <see cref="LimitSideLen"/> policy asks for, the image is cut along its long axis into tiles that fit
    /// the limit (overlapping by 384 px), each tile is detected at full resolution, and duplicates in the
    /// overlaps are resolved — boxes clear of a tile's cut edge win, then axis-aligned IoU 0.5 suppression.
    /// Best for tall pages such as long receipts; a line crossing a cut on a very wide image can come back
    /// as two pieces. Default false (Python PaddleOCR downscales).
    /// </summary>
    public bool TileLargeImages { get; init; }

    /// <summary>
    /// Build the detector input from a contrast-enhanced copy of the image (background normalization then
    /// a 0.5–99.5 percentile contrast stretch), which helps faded, grey or unevenly lit scans. Only the
    /// detector sees the enhanced copy; recognition still crops the original pixels. Default false.
    /// </summary>
    public bool EnhanceContrast { get; init; }

    /// <summary>
    /// The default detection thresholds (match PaddleOCR).
    /// </summary>
    public static DetectionOptions Default { get; } = new();
}
