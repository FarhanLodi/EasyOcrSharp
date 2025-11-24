using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace EasyOcrSharp.Models;

/// <summary>
/// Represents a 2D point within an OCR bounding polygon.
/// </summary>
public readonly record struct OcrPoint(double X, double Y)
{
    /// <summary>
    /// Converts the point collection to a read-only list.
    /// </summary>
    public static IReadOnlyList<OcrPoint> AsReadOnly(IEnumerable<OcrPoint> points)
        => points is IReadOnlyList<OcrPoint> list
            ? list
            : new ReadOnlyCollection<OcrPoint>(points.ToArray());
}

