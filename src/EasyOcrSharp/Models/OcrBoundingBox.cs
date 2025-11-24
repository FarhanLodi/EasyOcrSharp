using System;
using System.Collections.Generic;

namespace EasyOcrSharp.Models;

/// <summary>
/// Represents an axis-aligned bounding box computed from OCR results.
/// </summary>
public readonly record struct OcrBoundingBox(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>
    /// Gets an empty bounding box.
    /// </summary>
    public static OcrBoundingBox Empty { get; } = new(0, 0, 0, 0);

    /// <summary>
    /// Gets the width of the bounding box.
    /// </summary>
    public double Width => Math.Max(0, MaxX - MinX);

    /// <summary>
    /// Gets the height of the bounding box.
    /// </summary>
    public double Height => Math.Max(0, MaxY - MinY);

    /// <summary>
    /// Gets the horizontal center of the bounding box.
    /// </summary>
    public double CenterX => MinX + (Width / 2.0);

    /// <summary>
    /// Gets the vertical center of the bounding box.
    /// </summary>
    public double CenterY => MinY + (Height / 2.0);

    /// <summary>
    /// Gets a value indicating whether the bounding box contains no area.
    /// </summary>
    public bool IsEmpty => Width <= 0 && Height <= 0;

    /// <summary>
    /// Computes an axis-aligned bounding box from a collection of points.
    /// </summary>
    public static OcrBoundingBox FromPoints(IEnumerable<OcrPoint> points)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;

        var hasPoint = false;
        foreach (var point in points)
        {
            hasPoint = true;
            if (point.X < minX) minX = point.X;
            if (point.Y < minY) minY = point.Y;
            if (point.X > maxX) maxX = point.X;
            if (point.Y > maxY) maxY = point.Y;
        }

        return hasPoint
            ? new OcrBoundingBox(minX, minY, maxX, maxY)
            : Empty;
    }
}

