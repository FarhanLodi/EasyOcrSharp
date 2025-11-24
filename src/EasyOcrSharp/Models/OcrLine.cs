using System.Collections.Generic;

namespace EasyOcrSharp.Models;

/// <summary>
/// Represents a single line recognized in the OCR result.
/// </summary>
public sealed record OcrLine
{
    /// <summary>
    /// Gets the recognized text.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets the confidence score (0-1 range).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Gets the coordinates of the bounding polygon associated with the text line.
    /// </summary>
    public IReadOnlyList<OcrPoint> BoundingPolygon { get; init; } = Array.Empty<OcrPoint>();

    /// <summary>
    /// Gets the axis-aligned bounding box computed from the polygon.
    /// </summary>
    public OcrBoundingBox BoundingBox { get; init; } = OcrBoundingBox.Empty;
}

