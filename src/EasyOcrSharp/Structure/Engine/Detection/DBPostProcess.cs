using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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
/// <para>
/// A second, polygon-preserving mode (<see cref="GetPolygons"/>) mirrors PaddleX's
/// <c>polygons_from_bitmap</c> (<c>box_type='poly'</c>): instead of collapsing each region to a min-area
/// quad it traces the region's outer contour, simplifies it with Douglas–Peucker
/// (<c>approxPolyDP</c>, epsilon = 0.002 × perimeter), scores/unclips the simplified polygon, and emits
/// the resulting N-point polygon. Curved text (seal arcs) keeps its true outline this way. Currently
/// consumed only by the seal pipeline (<see cref="Structure.Seal.SealRecognizer"/>).
/// </para>
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
        var bitmap = Binarize(prob, width * height, thresh);

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

        // Reused across regions: at most two hull candidates per region row.
        OcrPoint[] extremes = Array.Empty<OcrPoint>();

        // components[0] is the background placeholder; real regions start at index 1.
        for (int label = 1; label <= maxCandidates && label < components.Length; label++)
        {
            var stats = components[label];
            if (stats.Area < 4)
            {
                continue;
            }

            // Hull candidates for the min-area quad fit: the leftmost and rightmost pixel of each row. The
            // convex hull of a pixel set equals the hull of its per-row extremes (every other pixel lies on
            // the segment between them), so the fitted rectangle is identical to fitting all pixels.
            int rows = stats.MaxY - stats.MinY + 1;
            if (extremes.Length < 2 * rows)
            {
                extremes = new OcrPoint[Math.Max(2 * rows, 2 * extremes.Length)];
            }
            int extremeCount = CollectRowExtremes(labels, width, stats, label, extremes);

            // get_mini_boxes: min-area quad of the region, ordered + measured for its shortest side.
            var (quad, minSide) = GetMiniBox(extremes.AsSpan(0, extremeCount));
            if (minSide < minSize)
            {
                continue;
            }

            // Score the candidate against the probability map (PaddleOCR scores BEFORE unclip):
            //   Fast — mean probability over the quad's bounding-box crop under a polygon mask.
            //   Slow — mean probability over the region's exact pixel mask (the contour interior).
            float score = options.ScoreMode == DetectionScoreMode.Slow
                ? BoxScoreSlow(prob, labels, width, stats, label)
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
    /// A scored N-point polygon produced by the <c>box_type='poly'</c> post-processing mode, in
    /// DESTINATION pixel coordinates (the caller's original-image space — unlike <see cref="ScoredBox"/>,
    /// the resize is already divided out and each coordinate clamped to the destination bounds, mirroring
    /// PaddleX's <c>polygons_from_bitmap</c>). Points follow the region's outer contour.
    /// </summary>
    /// <param name="Points">The polygon vertices (4 or more), in contour order.</param>
    /// <param name="Score">Mean probability inside the polygon's bounding region (0–1).</param>
    internal readonly record struct ScoredPolygon(OcrPoint[] Points, float Score);

    /// <summary>
    /// Extracts scored free-form text polygons from a DB probability map — PaddleX's
    /// <c>polygons_from_bitmap</c> (<c>box_type='poly'</c>, used by the seal pipeline where curved arc
    /// text must not be flattened to a quad). Steps per connected region: trace the outer contour
    /// (border following; cv2's <c>findContours</c> equivalent), simplify with Douglas–Peucker at
    /// epsilon = 0.002 × contour perimeter (<c>approxPolyDP</c>), drop polygons with fewer than 4
    /// vertices, score with <c>box_score_fast</c> against <see cref="DetectionOptions.BoxThreshold"/>,
    /// unclip with round joins (skipping candidates whose expansion splits into multiple rings, as
    /// Python does), drop results whose min-area-rect short side is under <c>min_size + 2</c>, then map
    /// every vertex into destination coordinates with rounding and clamping.
    /// <para>
    /// Simplifications vs Python (documented): inner (hole) contours are not traced — cv2's
    /// <c>RETR_LIST</c> would surface them, but a hole's interior is background so its mean score falls
    /// below <c>box_thresh</c> and it is filtered anyway; and where pyclipper's ragged multi-ring result
    /// accidentally keeps ring 0 via a <c>ValueError</c> fallback, this port skips all multi-ring
    /// expansions (the dominant Python path).
    /// </para>
    /// </summary>
    /// <param name="prob">Probability map, row-major <c>prob[y*width+x]</c> in [0,1].</param>
    /// <param name="width">Probability-map width (= resized image width).</param>
    /// <param name="height">Probability-map height (= resized image height).</param>
    /// <param name="options">Detection thresholds (binarization, box score floor, min size, unclip ratio).</param>
    /// <param name="destWidth">Original-image width the polygons are mapped back to.</param>
    /// <param name="destHeight">Original-image height the polygons are mapped back to.</param>
    /// <returns>Surviving polygons in destination coordinates, each with its score.</returns>
    public static List<ScoredPolygon> GetPolygons(
        ReadOnlySpan<float> prob, int width, int height, DetectionOptions options, int destWidth, int destHeight)
    {
        var results = new List<ScoredPolygon>();
        if (width <= 0 || height <= 0 || prob.Length < width * height || destWidth <= 0 || destHeight <= 0)
        {
            return results;
        }

        float thresh = (float)options.DetThreshold;     // det_db_thresh: pixel binarization
        double boxThresh = options.BoxThreshold;          // det_db_box_thresh: box score floor
        int minSize = Math.Max(1, options.MinSize);       // det_db_min_size
        double unclipRatio = options.UnclipRatio;         // det_db_unclip_ratio
        double widthScale = (double)destWidth / width;    // dest_width / bitmap width
        double heightScale = (double)destHeight / height;

        // Binarize (and optionally dilate), exactly as the quad path does.
        var bitmap = Binarize(prob, width * height, thresh);

        if (options.UseDilation)
        {
            bitmap = Morphology.Dilate2x2(bitmap, width, height);
        }

        var (labels, components) = ConnectedComponents.Label(bitmap, width, height);
        int maxCandidates = Math.Min(components.Length - 1, 1000);

        for (int label = 1; label <= maxCandidates && label < components.Length; label++)
        {
            // findContours equivalent: the region's outer boundary pixels in traversal order.
            var contour = TraceOuterContour(labels, width, height, components[label], label);
            if (contour.Count < 2)
            {
                continue; // single-pixel region: can never reach 4 approx vertices
            }

            // approxPolyDP with epsilon = 0.002 * arcLength(contour, closed=True).
            double epsilon = 0.002 * ClosedPerimeter(contour);
            var points = ApproximatePolygon(contour, epsilon);
            if (points.Length < 4)
            {
                continue;
            }

            // Score the simplified polygon (Python scores the approx poly, not the raw contour).
            float score = BoxScoreFast(prob, width, height, points);
            if (score < boxThresh)
            {
                continue;
            }

            // Unclip with round joins. Python skips candidates whose offset yields multiple rings.
            var inflated = InflatePolygon(points, unclipRatio);
            if (inflated is null || inflated.Count != 1)
            {
                continue;
            }

            var polygon = new List<OcrPoint>(inflated[0].Count);
            foreach (var pt in inflated[0])
            {
                polygon.Add(new OcrPoint(pt.x, pt.y));
            }
            if (polygon.Count < 3)
            {
                continue;
            }

            // Size filter on the unclipped polygon's min-area rect (min_size + 2, as in Python).
            var (_, minSide) = GetMiniBox(polygon);
            if (minSide < minSize + 2)
            {
                continue;
            }

            // Map back to destination coordinates: round (half-to-even, matching numpy) and clamp to
            // [0, dest] inclusive — Python's max(0, min(round(v*scale), dest)).
            var mapped = new OcrPoint[polygon.Count];
            for (int i = 0; i < polygon.Count; i++)
            {
                mapped[i] = new OcrPoint(
                    Math.Clamp(Math.Round(polygon[i].X * widthScale), 0, destWidth),
                    Math.Clamp(Math.Round(polygon[i].Y * heightScale), 0, destHeight));
            }

            results.Add(new ScoredPolygon(mapped, score));
        }

        return results;
    }

    /// <summary>
    /// The eight neighbor offsets in clockwise screen order (y down) starting East, used by
    /// <see cref="TraceOuterContour"/>: E, SE, S, SW, W, NW, N, NE.
    /// </summary>
    private static readonly (int Dx, int Dy)[] TraceDirections =
    {
        (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1),
    };

    /// <summary>
    /// Traces the outer boundary of an 8-connected component via Moore-neighbor border following with
    /// Jacob's stopping criterion — the cv2 <c>findContours</c> outer-contour equivalent. Starts at the
    /// component's topmost-then-leftmost pixel and walks clockwise (screen orientation, y down),
    /// emitting every boundary pixel once per visit (thin diagonal strokes legitimately revisit pixels
    /// from both sides, as cv2 does). Internal for unit tests.
    /// </summary>
    internal static List<OcrPoint> TraceOuterContour(
        int[] labels, int width, int height, ConnectedComponents.Stats stats, int label)
    {
        // Topmost row, leftmost pixel: guaranteed to lie on the outer boundary.
        int sx = -1, sy = -1;
        for (int y = stats.MinY; y <= stats.MaxY && sx < 0; y++)
        {
            int rowBase = y * width;
            for (int x = stats.MinX; x <= stats.MaxX; x++)
            {
                if (labels[rowBase + x] == label)
                {
                    sx = x;
                    sy = y;
                    break;
                }
            }
        }

        var contour = new List<OcrPoint>();
        if (sx < 0)
        {
            return contour;
        }

        contour.Add(new OcrPoint(sx, sy));
        if (stats.Area == 1)
        {
            return contour;
        }

        int cx = sx, cy = sy;
        int prevDir = -1, firstDir = -1;

        // The boundary walk visits each region pixel at most a handful of times; cap defensively.
        int cap = 8 * stats.Area + 64;
        for (int iter = 0; iter < cap; iter++)
        {
            // After moving in direction d, resume the clockwise scan from (d + 6) % 8 — 90° CCW of the
            // last move. The very first scan starts at West: for the topmost-leftmost start pixel, all
            // of W/NW/N/NE are background, so the walk heads East first (clockwise on screen).
            int searchStart = prevDir < 0 ? 4 : (prevDir + 6) % 8;
            int found = -1;
            for (int k = 0; k < 8; k++)
            {
                int d = (searchStart + k) % 8;
                int nx = cx + TraceDirections[d].Dx;
                int ny = cy + TraceDirections[d].Dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                {
                    continue;
                }
                if (labels[ny * width + nx] == label)
                {
                    found = d;
                    break;
                }
            }

            if (found < 0)
            {
                break; // no foreground neighbor (cannot happen for Area > 1, defensive)
            }

            // Jacob's criterion: stop when back at the start pixel about to repeat the first move.
            if (cx == sx && cy == sy && prevDir >= 0 && found == firstDir)
            {
                break;
            }

            cx += TraceDirections[found].Dx;
            cy += TraceDirections[found].Dy;
            contour.Add(new OcrPoint(cx, cy));
            if (firstDir < 0)
            {
                firstDir = found;
            }
            prevDir = found;
        }

        // The walk re-appends the start pixel just before termination; drop that closing duplicate so
        // the contour is an open ring like cv2's.
        if (contour.Count > 1 && contour[^1] == contour[0])
        {
            contour.RemoveAt(contour.Count - 1);
        }

        return contour;
    }

    /// <summary>
    /// cv2's <c>arcLength(contour, closed=True)</c>: the sum of consecutive point distances including
    /// the closing edge back to the first point.
    /// </summary>
    private static double ClosedPerimeter(IReadOnlyList<OcrPoint> contour)
    {
        double perimeter = 0;
        int n = contour.Count;
        for (int i = 0; i < n; i++)
        {
            perimeter += Distance(contour[i], contour[(i + 1) % n]);
        }
        return perimeter;
    }

    /// <summary>
    /// Closed-curve Douglas–Peucker simplification — cv2's <c>approxPolyDP(closed=True)</c> equivalent.
    /// Splits the ring at its two mutually-farthest anchor points (point 0 and the point farthest from
    /// it), simplifies each open chain independently at <paramref name="epsilon"/> (perpendicular
    /// distance to the chord line), and rejoins them. Geometrically faithful to cv2, though not
    /// byte-identical (cv2 uses an iterative variant with its own anchor search). Internal for unit tests.
    /// </summary>
    internal static OcrPoint[] ApproximatePolygon(IReadOnlyList<OcrPoint> contour, double epsilon)
    {
        int n = contour.Count;
        if (n < 3)
        {
            var copy = new OcrPoint[n];
            for (int i = 0; i < n; i++)
            {
                copy[i] = contour[i];
            }
            return copy;
        }

        // Anchor 1 = point 0; anchor 2 = the point farthest from it.
        int far = 0;
        double best = -1;
        for (int i = 1; i < n; i++)
        {
            double d = Distance(contour[0], contour[i]);
            if (d > best)
            {
                best = d;
                far = i;
            }
        }

        var keep = new bool[n];
        keep[0] = keep[far] = true;
        SimplifyChain(contour, 0, far, epsilon, keep);
        SimplifyChainWrapped(contour, far, n, epsilon, keep);

        var result = new List<OcrPoint>(n);
        for (int i = 0; i < n; i++)
        {
            if (keep[i])
            {
                result.Add(contour[i]);
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// Iterative Douglas–Peucker over the open chain contour[first..last] (indices ascending).
    /// </summary>
    private static void SimplifyChain(IReadOnlyList<OcrPoint> contour, int first, int last, double epsilon, bool[] keep)
    {
        if (last - first < 2)
        {
            return;
        }

        var stack = new Stack<(int First, int Last)>();
        stack.Push((first, last));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double maxDist = -1;
            int maxIdx = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = PerpendicularDistance(contour[i], contour[a], contour[b]);
                if (d > maxDist)
                {
                    maxDist = d;
                    maxIdx = i;
                }
            }

            if (maxIdx >= 0 && maxDist > epsilon)
            {
                keep[maxIdx] = true;
                if (maxIdx - a >= 2)
                {
                    stack.Push((a, maxIdx));
                }
                if (b - maxIdx >= 2)
                {
                    stack.Push((maxIdx, b));
                }
            }
        }
    }

    /// <summary>
    /// <see cref="SimplifyChain"/> for the wrap-around chain contour[first..n-1] + contour[0] (the
    /// second half of the split ring, whose closing anchor is point 0). Indices past n-1 alias i - n.
    /// </summary>
    private static void SimplifyChainWrapped(IReadOnlyList<OcrPoint> contour, int first, int n, double epsilon, bool[] keep)
    {
        int last = n; // virtual index n aliases point 0
        if (last - first < 2)
        {
            return;
        }

        OcrPoint At(int i) => contour[i == n ? 0 : i];

        var stack = new Stack<(int First, int Last)>();
        stack.Push((first, last));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double maxDist = -1;
            int maxIdx = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = PerpendicularDistance(At(i), At(a), At(b));
                if (d > maxDist)
                {
                    maxDist = d;
                    maxIdx = i;
                }
            }

            if (maxIdx >= 0 && maxDist > epsilon)
            {
                keep[maxIdx] = true;
                if (maxIdx - a >= 2)
                {
                    stack.Push((a, maxIdx));
                }
                if (b - maxIdx >= 2)
                {
                    stack.Push((maxIdx, b));
                }
            }
        }
    }

    /// <summary>
    /// Perpendicular distance from <paramref name="p"/> to the infinite line through
    /// <paramref name="a"/> and <paramref name="b"/> (point distance when the anchors coincide).
    /// </summary>
    private static double PerpendicularDistance(OcrPoint p, OcrPoint a, OcrPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-12)
        {
            return Distance(p, a);
        }
        return Math.Abs(dy * (p.X - a.X) - dx * (p.Y - a.Y)) / len;
    }

    /// <summary>
    /// Binarizes the probability map: <c>bitmap[i] = prob[i] &gt; thresh ? 1 : 0</c>, vectorized with a
    /// scalar tail. The ordered float comparison is the same as the scalar one (NaN compares false in
    /// both), so the bitmap is identical. Internal for unit tests.
    /// </summary>
    internal static byte[] Binarize(ReadOnlySpan<float> prob, int length, float thresh)
    {
        var bitmap = new byte[length];
        int i = 0;
        if (Vector256.IsHardwareAccelerated && length >= Vector256<float>.Count)
        {
            var t = Vector256.Create(thresh);
            var one = Vector256.Create(1);
            ref float src = ref MemoryMarshal.GetReference(prob);
            Span<int> tmp = stackalloc int[Vector256<int>.Count];
            for (; i <= length - Vector256<float>.Count; i += Vector256<float>.Count)
            {
                var v = Vector256.LoadUnsafe(ref src, (nuint)i);
                var mask = Vector256.GreaterThan(v, t).AsInt32() & one;
                mask.CopyTo(tmp);
                for (int k = 0; k < tmp.Length; k++)
                {
                    bitmap[i + k] = (byte)tmp[k];
                }
            }
        }
        else if (Vector128.IsHardwareAccelerated && length >= Vector128<float>.Count)
        {
            var t = Vector128.Create(thresh);
            var one = Vector128.Create(1);
            ref float src = ref MemoryMarshal.GetReference(prob);
            Span<int> tmp = stackalloc int[Vector128<int>.Count];
            for (; i <= length - Vector128<float>.Count; i += Vector128<float>.Count)
            {
                var v = Vector128.LoadUnsafe(ref src, (nuint)i);
                var mask = Vector128.GreaterThan(v, t).AsInt32() & one;
                mask.CopyTo(tmp);
                for (int k = 0; k < tmp.Length; k++)
                {
                    bitmap[i + k] = (byte)tmp[k];
                }
            }
        }

        for (; i < length; i++)
        {
            bitmap[i] = prob[i] > thresh ? (byte)1 : (byte)0;
        }
        return bitmap;
    }

    /// <summary>
    /// Writes the leftmost and rightmost pixel of <paramref name="label"/> on every row of its bounding
    /// box into <paramref name="destination"/> (one point when both coincide) and returns the count. These
    /// per-row extremes have the same convex hull as the full pixel set. Internal for unit tests.
    /// </summary>
    internal static int CollectRowExtremes(int[] labels, int width, ConnectedComponents.Stats stats, int label, Span<OcrPoint> destination)
    {
        int n = 0;
        for (int y = stats.MinY; y <= stats.MaxY; y++)
        {
            int rowBase = y * width;
            int left = -1;
            for (int x = stats.MinX; x <= stats.MaxX; x++)
            {
                if (labels[rowBase + x] == label)
                {
                    left = x;
                    break;
                }
            }
            if (left < 0)
            {
                continue;
            }

            int right = left;
            for (int x = stats.MaxX; x > left; x--)
            {
                if (labels[rowBase + x] == label)
                {
                    right = x;
                    break;
                }
            }

            destination[n++] = new OcrPoint(left, y);
            if (right != left)
            {
                destination[n++] = new OcrPoint(right, y);
            }
        }
        return n;
    }

    /// <summary>
    /// PaddleOCR's <c>get_mini_boxes</c> over a list of points (unclipped polygons).
    /// </summary>
    private static (OcrPoint[] Quad, double MinSide) GetMiniBox(IReadOnlyList<OcrPoint> points)
        => GetMiniBox(points is OcrPoint[] arr ? arr : points.ToArray());

    /// <summary>
    /// PaddleOCR's <c>get_mini_boxes</c>: fits the minimum-area rectangle to <paramref name="points"/>,
    /// orders the four corners as top-left, top-right, bottom-right, bottom-left, and returns the
    /// rectangle's shorter side length so tiny boxes can be filtered.
    /// </summary>
    private static (OcrPoint[] Quad, double MinSide) GetMiniBox(ReadOnlySpan<OcrPoint> points)
    {
        var corners = MinAreaRect.Compute(points);
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
    /// counting only pixels that fall inside the quad polygon (point-in-polygon mask). The quad's corner
    /// coordinates are truncated to int before rasterization, mirroring Python's
    /// <c>.astype("int32")</c> + <c>cv2.fillPoly</c> mask.
    /// </summary>
    /// <remarks>
    /// Rasterized per row rather than per pixel: the even-odd rule of <see cref="PointInPolygon"/> puts
    /// integer column <c>x</c> inside exactly when an odd number of the row's edge crossings
    /// <c>xc</c> (computed with the identical expression) satisfy <c>x &lt; xc</c>. Sorting the crossings
    /// turns that into contiguous integer spans, summed row-major and left to right — the same pixels in
    /// the same order as the per-pixel loop, so the double sum and the score are bit-identical.
    /// </remarks>
    internal static float BoxScoreFast(ReadOnlySpan<float> prob, int width, int height, OcrPoint[] quad)
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

        // Shift the polygon into the local bounding-box frame, truncating to int as Python does with
        // .astype("int32") before cv2.fillPoly.
        int localW = xmax - xmin + 1;
        int localH = ymax - ymin + 1;
        var local = new (double X, double Y)[quad.Length];
        for (int i = 0; i < quad.Length; i++)
        {
            local[i] = ((int)(quad[i].X - xmin), (int)(quad[i].Y - ymin));
        }

        int n = local.Length;
        Span<double> crossings = n <= 64 ? stackalloc double[n] : new double[n];

        double sum = 0;
        long count = 0;
        for (int y = 0; y < localH; y++)
        {
            // Edge crossings of this row, with PointInPolygon's exact expression and edge filter.
            int k = 0;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = local[i].X, yi = local[i].Y;
                double xj = local[j].X, yj = local[j].Y;
                if ((yi > y) != (yj > y))
                {
                    double xc = (xj - xi) * (y - yi) / (yj - yi + 1e-12) + xi;

                    // Insertion sort (n is tiny).
                    int p = k++;
                    while (p > 0 && crossings[p - 1] > xc)
                    {
                        crossings[p] = crossings[p - 1];
                        p--;
                    }
                    crossings[p] = xc;
                }
            }
            if (k == 0)
            {
                continue;
            }

            // Column x is inside iff #{c : x < c} is odd. With c sorted, for integer x in
            // [ceil(c[m-1]), ceil(c[m]) - 1] exactly m crossings are <= x, so #{x < c} = k - m.
            int rowBase = (y + ymin) * width + xmin;
            for (int m = 0; m < k; m++)
            {
                if (((k - m) & 1) == 0)
                {
                    continue;
                }

                long start = m == 0 ? 0 : CeilToLong(crossings[m - 1]);
                long end = CeilToLong(crossings[m]) - 1;
                if (start < 0) start = 0;
                if (end > localW - 1) end = localW - 1;
                for (long x = start; x <= end; x++)
                {
                    sum += prob[rowBase + (int)x];
                }
                if (end >= start)
                {
                    count += end - start + 1;
                }
            }
        }

        return count == 0 ? 0f : (float)(sum / count);
    }

    /// <summary>
    /// <c>ceil(v)</c> as a saturating long (so far-off crossings clamp harmlessly to the row span).
    /// </summary>
    private static long CeilToLong(double v)
    {
        double c = Math.Ceiling(v);
        if (c >= long.MaxValue / 2) return long.MaxValue / 2;
        if (c <= long.MinValue / 2) return long.MinValue / 2;
        return (long)c;
    }

    /// <summary>
    /// Reference per-pixel implementation of <see cref="BoxScoreFast"/> (the pre-scanline loop), kept for
    /// equivalence tests only.
    /// </summary>
    internal static float BoxScoreFastReference(ReadOnlySpan<float> prob, int width, int height, OcrPoint[] quad)
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

        int localW = xmax - xmin + 1;
        int localH = ymax - ymin + 1;
        var local = new (double X, double Y)[quad.Length];
        for (int i = 0; i < quad.Length; i++)
        {
            local[i] = ((int)(quad[i].X - xmin), (int)(quad[i].Y - ymin));
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
    /// averages the probability map under it; because the pixels carrying <paramref name="label"/> are precisely the
    /// connected component's labeled pixels, that mask equals this pixel set, so we average directly over it
    /// (no rasterization needed). This is slightly slower but more accurate than <see cref="BoxScoreFast"/>
    /// for slanted or non-rectangular regions, where the bounding-box crop bleeds in background probability.
    /// The sum runs over the label map in raster order (the order the region's pixels were always visited),
    /// so no per-pixel point list is materialized.
    /// </summary>
    private static float BoxScoreSlow(ReadOnlySpan<float> prob, int[] labels, int width, ConnectedComponents.Stats stats, int label)
    {
        double sum = 0;
        long count = 0;
        for (int y = stats.MinY; y <= stats.MaxY; y++)
        {
            int rowBase = y * width;
            for (int x = stats.MinX; x <= stats.MaxX; x++)
            {
                if (labels[rowBase + x] == label)
                {
                    sum += prob[rowBase + x];
                    count++;
                }
            }
        }

        return count == 0 ? 0f : (float)(sum / count);
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
        PathsD? inflated = InflatePolygon(quad, unclipRatio);
        if (inflated is null || inflated.Count == 0)
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
    /// The shared Clipper2 offsetting behind both unclip modes: expands <paramref name="polygon"/>
    /// outward by <c>distance = area * unclip_ratio / perimeter</c> with <see cref="JoinType.Round"/> /
    /// <see cref="EndType.Polygon"/> (pyclipper's <c>JT_ROUND</c> / <c>ET_CLOSEDPOLYGON</c>). The path
    /// is fed positively oriented so the offset always expands regardless of the input winding. Returns
    /// null for degenerate input; otherwise the raw inflated ring set — the quad path picks the largest
    /// ring, the poly path requires exactly one.
    /// </summary>
    private static PathsD? InflatePolygon(IReadOnlyList<OcrPoint> polygon, double unclipRatio)
    {
        double signedArea = PolygonArea(polygon);
        double perimeter = PolygonPerimeter(polygon);
        if (perimeter < 1e-6)
        {
            return null;
        }

        double distance = Math.Abs(signedArea) * unclipRatio / perimeter;

        var path = new PathD(polygon.Count);
        if (signedArea >= 0)
        {
            foreach (var p in polygon)
            {
                path.Add(new PointD(p.X, p.Y));
            }
        }
        else
        {
            for (int i = polygon.Count - 1; i >= 0; i--)
            {
                path.Add(new PointD(polygon[i].X, polygon[i].Y));
            }
        }
        var paths = new PathsD { path };

        // arcTolerance default (0.0 -> auto); precision 2 decimals as in Clipper2's default ClipperD.
        return Clipper.InflatePaths(paths, distance, JoinType.Round, EndType.Polygon);
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
