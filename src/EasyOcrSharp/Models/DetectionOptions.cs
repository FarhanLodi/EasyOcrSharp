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

    /// <summary>The default detection thresholds (match EasyOCR).</summary>
    public static DetectionOptions Default { get; } = new();
}
