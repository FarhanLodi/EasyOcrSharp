using EasyOcrSharp.Structure.Engine.Models;
using EasyImageSharp;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Engine.Detection;

/// <summary>
/// A four-corner text quadrilateral produced by the DB detector, with the detector's confidence score.
/// Corners are in original-image pixel coordinates and are ordered clockwise from the top-left
/// (P0 = top-left, P1 = top-right, P2 = bottom-right, P3 = bottom-left).
/// </summary>
/// <param name="P0">Top-left corner.</param>
/// <param name="P1">Top-right corner.</param>
/// <param name="P2">Bottom-right corner.</param>
/// <param name="P3">Bottom-left corner.</param>
/// <param name="Score">Detection confidence (0–1).</param>
internal readonly record struct TextQuad(PointF P0, PointF P1, PointF P2, PointF P3, float Score)
{
    /// <summary>
    /// Enumerates the four corners in clockwise order (P0, P1, P2, P3).
    /// </summary>
    public IEnumerable<PointF> Points
    {
        get
        {
            yield return P0;
            yield return P1;
            yield return P2;
            yield return P3;
        }
    }

    /// <summary>
    /// The axis-aligned bounding box that contains all four corners.
    /// </summary>
    public RectangleF ToAxisAlignedBounds()
    {
        float minX = Math.Min(Math.Min(P0.X, P1.X), Math.Min(P2.X, P3.X));
        float minY = Math.Min(Math.Min(P0.Y, P1.Y), Math.Min(P2.Y, P3.Y));
        float maxX = Math.Max(Math.Max(P0.X, P1.X), Math.Max(P2.X, P3.X));
        float maxY = Math.Max(Math.Max(P0.Y, P1.Y), Math.Max(P2.Y, P3.Y));
        return new RectangleF(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Converts the four corners to <see cref="OcrPoint"/>s (P0..P3, clockwise from top-left).
    /// </summary>
    public OcrPoint[] ToOcrPoints() => new[]
    {
        new OcrPoint(P0.X, P0.Y),
        new OcrPoint(P1.X, P1.Y),
        new OcrPoint(P2.X, P2.Y),
        new OcrPoint(P3.X, P3.Y),
    };

    /// <summary>
    /// Builds a quad from four <see cref="OcrPoint"/>s and a score.
    /// </summary>
    public static TextQuad FromOcrPoints(OcrPoint p0, OcrPoint p1, OcrPoint p2, OcrPoint p3, float score)
        => new(
            new PointF((float)p0.X, (float)p0.Y),
            new PointF((float)p1.X, (float)p1.Y),
            new PointF((float)p2.X, (float)p2.Y),
            new PointF((float)p3.X, (float)p3.Y),
            score);
}
