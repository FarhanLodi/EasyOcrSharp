namespace EasyOcrSharp.Models;

/// <summary>
/// Low-level tuning for the CRAFT text-<b>detection</b> stage (where boxes are found), exposed for
/// difficult inputs. The defaults mirror upstream EasyOCR and are correct for most images — only
/// change these when detection is missing or splitting text. Recognition accuracy is controlled
/// separately via <see cref="RecognitionOptions"/>.
/// </summary>
public sealed record DetectionOptions
{
    /// <summary>
    /// Text-region confidence floor (0–1). Lower it to catch faint text, raise it to suppress noise.
    /// EasyOCR's <c>text_threshold</c>. Default 0.7.
    /// </summary>
    public double TextThreshold { get; init; } = 0.7;

    /// <summary>
    /// Affinity/link confidence floor (0–1) that decides whether neighbouring characters join into a
    /// word. EasyOCR's <c>link_threshold</c>. Default 0.4.
    /// </summary>
    public double LinkThreshold { get; init; } = 0.4;

    /// <summary>
    /// Low-bound text score (0–1) used when growing region pixels. Lower it to keep more of each glyph.
    /// EasyOCR's <c>low_text</c>. Default 0.4.
    /// </summary>
    public double LowText { get; init; } = 0.4;

    /// <summary>
    /// Image magnification before detection. Values &gt; 1 enlarge the image (better on small text, slower).
    /// EasyOCR's <c>mag_ratio</c>. Default 1.0.
    /// </summary>
    public double MagRatio { get; init; } = 1.0;

    /// <summary>
    /// Maximum size (px) of the longest side fed to the detector; larger images are scaled down to fit.
    /// EasyOCR's <c>canvas_size</c>. Default 2560.
    /// </summary>
    public int CanvasSize { get; init; } = 2560;

    /// <summary>Discard detected components smaller than this area (px). Default 10.</summary>
    public int MinSize { get; init; } = 10;

    /// <summary>
    /// Non-maximum-suppression IoU threshold (0–1) for de-duplicating overlapping detected boxes: when two
    /// boxes overlap by more than this (axis-aligned IoU), the smaller is dropped, removing duplicate lines
    /// from overlapping CRAFT components. Default 0.6. Set to 0 to disable.
    /// </summary>
    public double NmsIouThreshold { get; init; } = 0.6;

    /// <summary>
    /// Detect very large images in overlapping full-resolution tiles instead of shrinking them to
    /// <see cref="CanvasSize"/>. When the canvas cap would scale the detector input below 0.75× of the size
    /// <see cref="MagRatio"/> asks for (a long side above ~3400 px at the defaults), the page is cut into
    /// tiles of <c>CanvasSize / MagRatio</c> source pixels overlapping by 384 px — along the long axis for
    /// tall receipts and wide banners, in a grid when both sides are too large. Each tile is detected at
    /// full resolution, the word boxes are shifted back into page coordinates, and the duplicates the overlaps
    /// produce are resolved (boxes clear of a tile's cut edge win; a box cut in two by a seam is re-joined)
    /// before the usual line grouping runs over the whole page. Costs one detector pass per tile. Default
    /// false (EasyOCR downscales).
    /// </summary>
    public bool TileLargeImages { get; init; }

    /// <summary>
    /// Small-text rescue: the text height (px, at the detector's input scale — source pixels at the default
    /// <see cref="MagRatio"/> for images within <see cref="CanvasSize"/>) below which detection is repeated
    /// at a higher magnification. When greater than 0, the median short side of the boxes found by the
    /// normal pass is measured; if it is below this value the image is detected again magnified so the
    /// typical line reaches about <c>max(24, MinTextHeight)</c> px (at most 3×, and never past
    /// <see cref="CanvasSize"/>). A page where nothing was found and whose detector input is under 1500 px is
    /// also retried once at 2×. Boxes are always returned in original-image coordinates, and recognition
    /// still reads the original pixels. Default 0 (off) — the extra pass costs a second detector run on the
    /// images it triggers for, and EasyOCR has no such step (its manual equivalent is <see cref="MagRatio"/>).
    /// </summary>
    public int MinTextHeight { get; init; }

    /// <summary>
    /// Run detection on a contrast-enhanced copy of the image (background normalization, then a 0.5–99.5
    /// percentile contrast stretch), which helps faded, grey or unevenly lit scans the detector otherwise
    /// under-segments. Only the detector sees the enhanced copy — recognition still crops the original
    /// pixels, and box coordinates are unchanged by it. Can cost a little on clean, high-contrast pages, so
    /// enable it for faded or unevenly lit input. Default false.
    /// </summary>
    public bool EnhanceContrast { get; init; }

    /// <summary>
    /// Whether any of the opt-in detection passes (<see cref="TileLargeImages"/>, <see cref="MinTextHeight"/>,
    /// <see cref="EnhanceContrast"/>) is enabled. With none enabled, detection is EasyOCR's single pass.
    /// </summary>
    internal bool UsesExtraPasses => TileLargeImages || EnhanceContrast || MinTextHeight > 0;

    /// <summary>The default detection thresholds (match EasyOCR).</summary>
    public static DetectionOptions Default { get; } = new();
}
