using EasyOcrSharp.Structure.Engine.Models;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Engine.Geometry;

/// <summary>
/// Computes the minimum-area rotated bounding rectangle of a point set using the
/// rotating-calipers algorithm on the convex hull. Returns the four corners in
/// clockwise order starting from the top-left.
/// </summary>
internal static class MinAreaRect
{
    public static OcrPoint[] Compute(ReadOnlySpan<OcrPoint> points)
    {
        if (points.Length < 3)
        {
            // Degenerate set — fall back to axis-aligned bbox.
            return AxisAlignedRectFromPoints(points);
        }

        var hull = ConvexHull(points);
        if (hull.Length < 3)
        {
            return AxisAlignedRectFromPoints(points);
        }

        double bestArea = double.PositiveInfinity;
        OcrPoint[] bestCorners = null!;

        // For each hull edge, project all hull points onto the edge and its perpendicular,
        // compute the spanning rectangle, track the minimum area.
        for (int i = 0; i < hull.Length; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Length];
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) continue;
            double ux = dx / len, uy = dy / len;
            double vx = -uy, vy = ux;

            double minU = double.PositiveInfinity, maxU = double.NegativeInfinity;
            double minV = double.PositiveInfinity, maxV = double.NegativeInfinity;
            for (int j = 0; j < hull.Length; j++)
            {
                double pu = hull[j].X * ux + hull[j].Y * uy;
                double pv = hull[j].X * vx + hull[j].Y * vy;
                if (pu < minU) minU = pu;
                if (pu > maxU) maxU = pu;
                if (pv < minV) minV = pv;
                if (pv > maxV) maxV = pv;
            }

            double area = (maxU - minU) * (maxV - minV);
            if (area < bestArea)
            {
                bestArea = area;
                bestCorners = new OcrPoint[4]
                {
                    new(minU * ux + minV * vx, minU * uy + minV * vy),
                    new(maxU * ux + minV * vx, maxU * uy + minV * vy),
                    new(maxU * ux + maxV * vx, maxU * uy + maxV * vy),
                    new(minU * ux + maxV * vx, minU * uy + maxV * vy),
                };
            }
        }

        return OrderClockwiseStartingTopLeft(bestCorners ?? AxisAlignedRectFromPoints(points));
    }

    /// <summary>
    /// Andrew's monotone chain convex hull. O(n log n).
    /// </summary>
    private static OcrPoint[] ConvexHull(ReadOnlySpan<OcrPoint> input)
    {
        var pts = input.ToArray();
        Array.Sort(pts, (p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));

        int n = pts.Length;
        var hull = new OcrPoint[2 * n];
        int k = 0;

        // Lower hull
        for (int i = 0; i < n; i++)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], pts[i]) <= 0) k--;
            hull[k++] = pts[i];
        }
        // Upper hull
        int t = k + 1;
        for (int i = n - 2; i >= 0; i--)
        {
            while (k >= t && Cross(hull[k - 2], hull[k - 1], pts[i]) <= 0) k--;
            hull[k++] = pts[i];
        }

        Array.Resize(ref hull, k - 1);
        return hull;
    }

    private static double Cross(OcrPoint o, OcrPoint a, OcrPoint b)
        => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    private static OcrPoint[] AxisAlignedRectFromPoints(ReadOnlySpan<OcrPoint> pts)
    {
        if (pts.Length == 0) return Array.Empty<OcrPoint>();
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var p in pts)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        return new[]
        {
            new OcrPoint(minX, minY),
            new OcrPoint(maxX, minY),
            new OcrPoint(maxX, maxY),
            new OcrPoint(minX, maxY),
        };
    }

    /// <summary>
    /// Reorder a 4-point quadrilateral into the canonical text-box corner order:
    /// top-left, top-right, bottom-right, bottom-left (clockwise in image coordinates,
    /// where y grows downward). Mirrors OpenCV/PaddleOCR <c>order_points</c>: the sum of
    /// coordinates (x+y) is smallest at the top-left and largest at the bottom-right, while
    /// the difference (x-y) distinguishes top-right (largest) from bottom-left (smallest).
    /// </summary>
    private static OcrPoint[] OrderClockwiseStartingTopLeft(OcrPoint[] corners)
    {
        if (corners.Length != 4) return corners;

        int tl = 0, br = 0, tr = 0, bl = 0;
        double minSum = double.PositiveInfinity, maxSum = double.NegativeInfinity;
        double minDiff = double.PositiveInfinity, maxDiff = double.NegativeInfinity;

        for (int i = 0; i < 4; i++)
        {
            double sum = corners[i].X + corners[i].Y;
            double diff = corners[i].X - corners[i].Y;
            if (sum < minSum) { minSum = sum; tl = i; }
            if (sum > maxSum) { maxSum = sum; br = i; }
            if (diff > maxDiff) { maxDiff = diff; tr = i; }
            if (diff < minDiff) { minDiff = diff; bl = i; }
        }

        return new[] { corners[tl], corners[tr], corners[br], corners[bl] };
    }
}
