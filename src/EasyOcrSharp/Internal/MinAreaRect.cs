using EasyOcrSharp.Models;

namespace EasyOcrSharp.Internal;

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
    /// Reorder a 4-point quadrilateral so the first corner is the one closest to (minX, minY)
    /// of the bounding box, then proceeds clockwise.
    /// </summary>
    private static OcrPoint[] OrderClockwiseStartingTopLeft(OcrPoint[] corners)
    {
        if (corners.Length != 4) return corners;

        // Find corner with smallest x+y (top-left).
        int startIdx = 0;
        double minSum = double.PositiveInfinity;
        for (int i = 0; i < 4; i++)
        {
            double sum = corners[i].X + corners[i].Y;
            if (sum < minSum) { minSum = sum; startIdx = i; }
        }

        // Determine winding (clockwise vs counter-clockwise) using shoelace.
        double signedArea = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % 4];
            signedArea += (b.X - a.X) * (b.Y + a.Y);
        }
        bool clockwise = signedArea > 0; // image coords: y grows downward, so positive shoelace = clockwise

        var ordered = new OcrPoint[4];
        for (int i = 0; i < 4; i++)
        {
            int idx = clockwise ? (startIdx + i) % 4 : (startIdx - i + 4) % 4;
            ordered[i] = corners[idx];
        }
        return ordered;
    }
}
