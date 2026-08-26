namespace EasyOcrSharp.Structure.Engine.Models;

/// <summary>
/// How the binarized DB probability map is turned into text-box scores during detection post-processing.
/// Mirrors PaddleOCR's <c>det_db_score_mode</c>.
/// </summary>
internal enum DetectionScoreMode
{
    /// <summary>
    /// Average the probability map over the full min-area rectangle of the contour (PaddleOCR default).
    /// </summary>
    Fast,

    /// <summary>
    /// Average over the exact polygon mask — slightly slower, slightly more accurate.
    /// </summary>
    Slow,
}
