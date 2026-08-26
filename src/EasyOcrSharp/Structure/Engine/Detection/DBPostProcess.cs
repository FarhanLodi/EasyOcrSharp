using Clipper2Lib;
using EasyOcrSharp.Structure.Engine.Geometry;
using EasyOcrSharp.Structure.Engine.Models;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Engine.Detection;

/// <summary>
/// DB (Differentiable Binarization) post-processing: turns the network's per-pixel text-probability map
/// into ordered text quadrilaterals. Mirrors PaddleOCR's <c>DBPostProcess</c> (and RapidOcrNet's port):
/// binarize the probability map at <see cref="DetectionOptions.DetThreshold"/>, optionally dilate that
/// binary map with a 2x2 kernel when <see cref="DetectionOptions.UseDilation"/> is set, extract connected
/// regions (via <see cref="ConnectedComponents"/>), fit a minimum-area quad to each region
/// (<see cref="MinAreaRect"/>), score it against the probability map — <c>box_score_fast</c> (the quad's
/// bounding-box crop) or <c>box_score_slow</c> (the exact region mask) per
/// <see cref="DetectionOptions.ScoreMode"/> — drop low-scoring or tiny boxes, then "unclip" (expand) each
/// surviving quad outward by <see cref="DetectionOptions.UnclipRatio"/> using Clipper2's polygon offsetting
/// and re-fit a quad to the inflated polygon. Coordinates are in the detector's resized-image space; the
/// caller rescales them back to the original image.
/// </summary>
internal static class DBPostProcess
{
    /// <summary>
    /// A scored, point-ordered quad produced by post-processing, in resized (network input) pixel
    /// coordinates. Corners are ordered top-left, top-right, bottom-right, bottom-left.
    /// </summary>
    /// <param name="Points">The four corners (TL, TR, BR, BL).</param>
    /// <param name="Score">Mean probability inside the box's bounding region (0–1).</param>
    internal readonly record struct ScoredBox(OcrPoint[] Points, float Score);

    /// <summary>
    /// Extracts scored text boxes from a DB probability map.
    /// </summary>
    /// <param name="prob">Probability map, row-major <c>prob[y*width+x]</c> in [0,1], length <c>width*height</c>.</param>
    /// <param name="width">Probability-map width (= resized image width).</param>
    /// <param name="height">Probability-map height (= resized image height).</param>
    /// <param name="options">Detection thresholds (binarization, box score floor, min size, unclip ratio).</param>
    /// <returns>The surviving boxes in resized-image coordinates, each with its box score.</returns>
    public static List<ScoredBox> GetBoxes(ReadOnlySpan<float> prob, int width, int height, DetectionOptions options)
    {
        var results = new List<ScoredBox>();
        if (width <= 0 || height <= 0 || prob.Length < width * height)
        {
            return results;
        }

        float thresh = (float)options.DetThreshold;     // det_db_thresh (0.3): pixel binarization
        double boxThresh = options.BoxThreshold;          // det_db_box_thresh (0.6): box score floor
        int minSize = Math.Max(1, options.MinSize);       // det_db_min_size (3): smallest accepted side
        double unclipRatio = options.UnclipRatio;         // det_db_unclip_ratio (1.5)

        // Binarize: segmentation = prob > thresh.
        var bitmap = new byte[width * height];
        for (int i = 0; i < bitmap.Length; i++)
        {
            bitmap[i] = prob[i] > thresh ? (byte)1 : (byte)0;
        }

        // use_dilation: bridge thin/broken strokes with cv2.dilate(bitmap, np.ones((2,2))) before
        // contour extraction. Off by default, so the unchanged path stays byte-identical.
        if (options.UseDilation)
        {
            bitmap = Morphology.Dilate2x2(bitmap, width, height);
        }

        // Connected-component (contour/region) extraction over the binary map.
        var (labels, components) = ConnectedComponents.Label(bitmap, width, height);

        // PaddleOCR caps the number of candidate regions at 1000 (max_candidates).
        int maxCandidates = Math.Min(components.Length - 1, 1000);

        // components[0] is the background placeholder; real regions start at index 1.
        for (int label = 1; label <= maxCandidates && label < components.Length; label++)
        {
            var stats = components[label];

            // Gather the region's pixel coordinates for the min-area quad fit.
            var regionPoints = CollectRegionPoints(labels, width, stats, label);
            if (regionPoints.Count < 4)
            {
                continue;
            }

            // get_mini_boxes: min-area quad of the region, ordered + measured for its shortest side.
            var (quad, minSide) = GetMiniBox(regionPoints);
            if (minSide < minSize)
            {
                continue;
            }

            // Score the candidate against the probability map (PaddleOCR scores BEFORE unclip):
            //   Fast — mean probability over the quad's bounding-box crop under a polygon mask.
            //   Slow — mean probability over the region's exact pixel mask (the contour interior).
            float score = options.ScoreMode == DetectionScoreMode.Slow
                ? BoxScoreSlow(prob, width, regionPoints)
                : BoxScoreFast(prob, width, height, quad);
            if (score < boxThresh)
            {
                continue;
            }

            // Unclip: expand the quad outward, then re-fit a min-area quad to the inflated polygon.
            var unclipped = Unclip(quad, unclipRatio);
            if (unclipped is null || unclipped.Count < 4)
            {
                continue;
            }

            var (finalQuad, finalMinSide) = GetMiniBox(unclipped);
            if (finalMinSide < minSize + 2)
            {
                // PaddleOCR drops boxes whose unclipped shortest side is below min_size + 2.
                continue;
            }

            results.Add(new ScoredBox(finalQuad, score));
        }

        return results;
    }

    /// <summary>
    /// Collects the (x,y) coordinates of every pixel belonging to <paramref name="label"/>.
    /// </summary>
    private static List<OcrPoint> CollectRegionPoints(int[] labels, int width, ConnectedComponents.Stats stats, int label)
    {
        var points = new List<OcrPoint>(stats.Area);
        for (int y = stats.MinY; y <= stats.MaxY; y++)
        {
            int rowBase = y * width;
            for (int x = stats.MinX; x <= stats.MaxX; x++)
            {
                if (labels[rowBase + x] == label)
                {
                    points.Add(new OcrPoint(x, y));
                }
            }
        }
        return points;
    }

    /// <summary>
    /// PaddleOCR's <c>get_mini_boxes</c>: fits the minimum-area rectangle to <paramref name="points"/>,
    /// orders the four corners as top-left, top-right, bottom-right, bottom-left, and returns the
    /// rectangle's shorter side length so tiny boxes can be filtered.
    /// </summary>
    private static (OcrPoint[] Quad, double MinSide) GetMiniBox(IReadOnlyList<OcrPoint> points)
    {
        var corners = MinAreaRect.Compute(points is OcrPoint[] arr ? arr : points.ToArray());
        if (corners.Length != 4)
        {
            return (corners, 0);
        }

        var ordered = OrderQuad(corners);
        double side01 = Distance(ordered[0], ordered[1]);
        double side12 = Distance(ordered[1], ordered[2]);
        return (ordered, Math.Min(side01, side12));
    }

    /// <summary>
    /// Orders four corners as TL, TR, BR, BL using PaddleOCR's <c>get_mini_boxes</c> logic: sort by x,
    /// split into the two left-most and two right-most points, then assign top/bottom within each pair by y.
    /// </summary>
    private static OcrPoint[] OrderQuad(OcrPoint[] corners)
    {
        var pts = (OcrPoint[])corners.Clone();
        Array.Sort(pts, (a, b) => a.X.CompareTo(b.X));

        // Left pair (smaller x): top = smaller y. Right pair (larger x): top = smaller y.
        var (l1, l2) = pts[0].Y <= pts[1].Y ? (pts[0], pts[1]) : (pts[1], pts[0]);
        var (r1, r2) = pts[2].Y <= pts[3].Y ? (pts[2], pts[3]) : (pts[3], pts[2]);

        // index_1 = top-left, index_2 = top-right, index_3 = bottom-right, index_4 = bottom-left.
        return new[] { l1, r1, r2, l2 };
    }

    /// <summary>
    /// PaddleOCR's <c>box_score_fast</c>: the mean probability over the quad's axis-aligned bounding box,
    /// counting only pixels that fall inside the quad polygon (point-in-polygon mask).
    /// </summary>
    private static float BoxScoreFast(ReadOnlySpan<float> prob, int width, int height, OcrPoint[] quad)
    {
        double minXd = quad[0].X, maxXd = quad[0].X, minYd = quad[0].Y, maxYd = quad[0].Y;
        for (int i = 1; i < quad.Length; i++)
        {
            minXd = Math.Min(minXd, quad[i].X);
            maxXd = Math.Max(maxXd, quad[i].X);
            minYd = Math.Min(minYd, quad[i].Y);
            maxYd = Math.Max(maxYd, quad[i].Y);
        }

        int xmin = Math.Clamp((int)Math.Floor(minXd), 0, width - 1);
        int xmax = Math.Clamp((int)Math.Ceiling(maxXd), 0, width - 1);
        int ymin = Math.Clamp((int)Math.Floor(minYd), 0, height - 1);
        int ymax = Math.Clamp((int)Math.Ceiling(maxYd), 0, height - 1);
        if (xmax < xmin || ymax < ymin)
        {
            return 0f;
        }

        // Shift the polygon into the local bounding-box coordinate frame.
        int localW = xmax - xmin + 1;
        int localH = ymax - ymin + 1;
        var local = new (double X, double Y)[quad.Length];
        for (int i = 0; i < quad.Length; i++)
        {
            local[i] = (quad[i].X - xmin, quad[i].Y - ymin);
        }

        double sum = 0;
        long count = 0;
        for (int y = 0; y < localH; y++)
        {
            for (int x = 0; x < localW; x++)
            {
                if (PointInPolygon(x, y, local))
                {
                    sum += prob[(y + ymin) * width + (x + xmin)];
                    count++;
                }
            }
        }

        return count == 0 ? 0f : (float)(sum / count);
    }

    /// <summary>
    /// PaddleOCR's <c>box_score_slow</c>: the mean probability over the region's exact mask rather than the
    /// quad's bounding-box crop. PaddleOCR rasterizes the contour polygon with <c>cv2.fillPoly</c> and
    /// averages the probability map under it; because <paramref name="regionPoints"/> are precisely the
    /// connected component's labeled pixels, that mask equals this pixel set, so we average directly over it
    /// (no rasterization needed). This is slightly slower but more accurate than <see cref="BoxScoreFast"/>
    /// for slanted or non-rectangular regions, where the bounding-box crop bleeds in background probability.
    /// </summary>
    private static float BoxScoreSlow(ReadOnlySpan<float> prob, int width, IReadOnlyList<OcrPoint> regionPoints)
    {
        if (regionPoints.Count == 0)
        {
            return 0f;
        }

        double sum = 0;
        for (int i = 0; i < regionPoints.Count; i++)
        {
            var p = regionPoints[i];
            sum += prob[(int)p.Y * width + (int)p.X];
        }

        return (float)(sum / regionPoints.Count);
    }

    /// <summary>
    /// Even-odd ray-cast point-in-polygon test at pixel-center (x, y).
    /// </summary>
    private static bool PointInPolygon(double x, double y, (double X, double Y)[] poly)
    {
        bool inside = false;
        int n = poly.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = poly[i].X, yi = poly[i].Y;
            double xj = poly[j].X, yj = poly[j].Y;
            bool intersect = ((yi > y) != (yj > y)) &&
                             (x < (xj - xi) * (y - yi) / (yj - yi + 1e-12) + xi);
            if (intersect)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>
    /// PaddleOCR's <c>unclip</c>: offsets the quad outward by
    /// <c>distance = area * unclip_ratio / perimeter</c> using Clipper2's polygon inflation
    /// (<see cref="Clipper.InflatePaths(PathsD, double, JoinType, EndType, double, int, double)"/> with
    /// <see cref="JoinType.Round"/> / <see cref="EndType.Polygon"/>), and returns the inflated polygon's
    /// vertices. Returns null if the polygon is degenerate or inflation produces nothing.
    /// </summary>
    private static List<OcrPoint>? Unclip(OcrPoint[] quad, double unclipRatio)
    {
        double area = Math.Abs(PolygonArea(quad));
        double perimeter = PolygonPerimeter(quad);
        if (perimeter < 1e-6)
        {
            return null;
        }

        double distance = area * unclipRatio / perimeter;

        var path = new PathD(quad.Length);
        foreach (var p in quad)
        {
            path.Add(new PointD(p.X, p.Y));
        }
        var paths = new PathsD { path };

        // arcTolerance default (0.0 -> auto); precision 2 decimals as in Clipper2's default ClipperD.
        PathsD inflated = Clipper.InflatePaths(paths, distance, JoinType.Round, EndType.Polygon);
        if (inflated.Count == 0)
        {
            return null;
        }

        // Use the largest inflated ring (offsetting a convex quad yields a single ring).
        PathD best = inflated[0];
        double bestArea = Math.Abs(Clipper.Area(best));
        for (int i = 1; i < inflated.Count; i++)
        {
            double a = Math.Abs(Clipper.Area(inflated[i]));
            if (a > bestArea)
            {
                bestArea = a;
                best = inflated[i];
            }
        }

        var result = new List<OcrPoint>(best.Count);
        foreach (var pt in best)
        {
            result.Add(new OcrPoint(pt.x, pt.y));
        }
        return result;
    }

    /// <summary>
    /// Signed polygon area via the shoelace formula.
    /// </summary>
    private static double PolygonArea(IReadOnlyList<OcrPoint> poly)
    {
        double area = 0;
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % n];
            area += a.X * b.Y - b.X * a.Y;
        }
        return area / 2.0;
    }

    /// <summary>
    /// Sum of edge lengths around the polygon.
    /// </summary>
    private static double PolygonPerimeter(IReadOnlyList<OcrPoint> poly)
    {
        double perimeter = 0;
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            perimeter += Distance(poly[i], poly[(i + 1) % n]);
        }
        return perimeter;
    }

    private static double Distance(OcrPoint a, OcrPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
