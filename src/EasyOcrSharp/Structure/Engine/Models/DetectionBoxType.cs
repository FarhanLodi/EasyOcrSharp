namespace EasyOcrSharp.Structure.Engine.Models;

/// <summary>
/// The geometry the DB detection post-processing emits per text region — PaddleOCR's
/// <c>det_box_type</c> ("quad" / "poly").
/// </summary>
internal enum DetectionBoxType
{
    /// <summary>
    /// Fit a minimum-area 4-point quadrilateral to each region (PaddleOCR default; straight text).
    /// </summary>
    Quad,

    /// <summary>
    /// Keep each region's simplified outer contour as an N-point polygon (curved text, e.g. seal arcs).
    /// </summary>
    Poly,
}
