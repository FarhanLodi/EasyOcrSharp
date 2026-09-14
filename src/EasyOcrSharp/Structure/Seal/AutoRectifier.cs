using Clipper2Lib;
using EasyOcrSharp.Structure.Engine.Geometry;
using EasyOcrSharp.Structure.Engine.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

using OcrPoint = EasyOcrSharp.Models.OcrPoint;

namespace EasyOcrSharp.Structure.Seal;

/// <summary>
/// Curved-text rectification for the seal pipeline — a pragmatic port of PaddleX's
/// <c>CropByPolys.get_poly_rect_crop</c> + the homography mode of <c>seal_det_warp.AutoRectifier</c>
/// (<c>components/common/crop_image_regions.py:525-580</c>, <c>seal_det_warp.py</c>).
/// <para>
/// Given the N-point polygon a <c>box_type='poly'</c> DB detector emits for one curved text line, the
/// crop path is chosen by how rectangular the polygon is: when its IoU against its own min-area
/// rectangle is ≥ 0.7 the line is essentially straight and the existing quad warp
/// (<see cref="PerspectiveWarp"/>.<c>Rectify</c> with the rot90 heuristic) is
/// used; below 0.7 the line is curved (a seal arc) and is straightened piecewise — the polygon is
/// clustered into corner points (<c>sample_points_on_bbox</c>), split into its top and bottom edge
/// chains (<c>reorder_poly_edge</c> / <c>find_head_tail</c>), each chain arc-length resampled to 16
/// points (<c>sample_points_on_bbox_bp</c> with n=15), and every adjacent point-pair quad is warped to
/// an upright strip (per-segment homography, bilinear sampling, black border — Python's divide-and-
/// conquer <c>dc_homo</c>), the strips concatenated horizontally into one straight line image.
/// </para>
/// <para>
/// Documented simplifications vs Python: (1) the "calibration" (virtual-camera cv2.calibrateCamera)
/// mode is not ported — the seal pipeline invokes AutoRectifier with <c>mode="homography"</c> only;
/// (2) vertical curved lines (bbox height/width &gt; 1.5, Python's <c>horizontal_text_estimate</c>)
/// fall back to the quad path instead of the rotated piecewise warp — the quad path's own rot90
/// heuristic still brings tall crops upright for the recognizer; (3) after the top/bottom swap the
/// chains are normalized to read left→right, where Python relies on the contour winding order
/// (protects against mirrored strips); (4) Shapely's <c>is_valid</c> pre-check is unnecessary — the
/// IoU is computed with Clipper2 under nonzero fill, which is robust to self-intersections; (5) the
/// wrap-around cluster merge folds the last cluster into the first (Python's intent; its
/// last-referenced-group variable reuse is a quirk, not a spec).
/// </para>
/// </summary>
internal static class AutoRectifier
{
    // Python resamples each sideline with sample_points_on_bbox_bp(line, 15), which yields 15+1 points.
    private const int SidelineSamples = 15;

    // get_poly_rect_crop: poly-vs-minAreaRect IoU at or above this means "straight enough for a quad".
    private const double QuadIouThreshold = 0.7;

    // find_head_tail's orientation_thr (crop_image_regions.py:234).
    private const double OrientationThreshold = 2.0;

    /// <summary>
    /// Rectifies the region under <paramref name="polygon"/> into an upright single-line crop:
    /// the quad warp for near-rectangular polygons, the piecewise curved-text warp otherwise.
    /// Returns null only when even the quad fallback is degenerate (sub-2px region).
    /// </summary>
    /// <param name="source">The image the polygon lives in (the seal crop).</param>
    /// <param name="polygon">The detected text polygon, 4+ points in contour order.</param>
    public static Image<Rgb24>? GetPolyRectCrop(Image<Rgb24> source, IReadOnlyList<OcrPoint> polygon)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(polygon);
        if (polygon.Count == 0)
        {
            return null;
        }

        // Python truncates the polygon to int32 before everything else.
        var points = new OcrPoint[polygon.Count];
        for (int i = 0; i < polygon.Count; i++)
        {
            points[i] = new OcrPoint((int)polygon[i].X, (int)polygon[i].Y);
        }

        // Min-area rect: both the IoU reference and the fallback crop.
        var rect = MinAreaRect.Compute(points);
        if (points.Length < 4)
        {
            return QuadCrop(source, rect);
        }

        double iou = PolygonIoU(points, rect);
        if (iou >= QuadIouThreshold)
        {
            return QuadCrop(source, rect);
        }

        // Curved line: cluster the dense contour into corner points (sample_points_on_bbox), int32-
        // truncated as in Python.
        var clustered = ClusterPolygonPoints(points);
        for (int i = 0; i < clustered.Length; i++)
        {
            clustered[i] = new OcrPoint((int)clustered[i].X, (int)clustered[i].Y);
        }
        if (clustered.Length < 4)
        {
            return QuadCrop(source, rect);
        }

        // Split into the two sidelines and arc-length resample each to 16 points.
        var (sideA, sideB) = ReorderPolyEdge(clustered);
        if (sideA.Length < 2 || sideB.Length < 2)
        {
            return QuadCrop(source, rect);
        }

        var top = ResampleLine(sideA, SidelineSamples);
        var bottom = ResampleLine(sideB, SidelineSamples);

        // sideline_mean_shift: make sure 'top' really is the upper chain.
        if (MeanY(top) > MeanY(bottom))
        {
            (top, bottom) = (bottom, top);
        }

        // The sidelines run in opposite winding directions; reverse the bottom so bottom[j] sits under
        // top[j] (Python's dc_homo achieves the same via mirrored indexing).
        Array.Reverse(bottom);

        // Pragmatic deviation: normalize to left→right so the stitched strip always reads forward.
        if (top[0].X > top[^1].X)
        {
            Array.Reverse(top);
            Array.Reverse(bottom);
        }

        // horizontal_text_estimate: vertical curved lines fall back to the quad path (documented
        // simplification — Python warps them sideways and rot90s the strip).
        if (IsVertical(top, bottom))
        {
            return QuadCrop(source, rect);
        }

        return PiecewiseRectify(source, top, bottom) ?? QuadCrop(source, rect);
    }

    /// <summary>
    /// The straight-line fallback: min-area-rect quad warp with the tall-crop rot90 heuristic —
    /// Python's <c>get_minarea_rect</c> + <c>get_rotate_crop_image</c>.
    /// </summary>
    private static Image<Rgb24>? QuadCrop(Image<Rgb24> source, OcrPoint[] rect)
        => PerspectiveWarp.Rectify(source, rect, rotateVertical: true);

    /// <summary>
    /// IoU of two polygons via Clipper2 boolean clipping under nonzero fill (robust to concave and
    /// mildly self-intersecting rings, where Python needs Shapely's <c>is_valid</c> guard). Matches
    /// <c>get_intersection_over_union</c>: intersection / (union + 1e-10). Internal for unit tests.
    /// </summary>
    internal static double PolygonIoU(IReadOnlyList<OcrPoint> a, IReadOnlyList<OcrPoint> b)
    {
        var subject = new PathsD { ToPath(a) };
        var clip = new PathsD { ToPath(b) };

        double intersection = SumAbsArea(Clipper.Intersect(subject, clip, FillRule.NonZero));
        double union = SumAbsArea(Clipper.Union(subject, clip, FillRule.NonZero));
        return union <= 0 ? 0 : intersection / (union + 1e-10);
    }

    /// <summary>
    /// Port of <c>sample_points_on_bbox</c> (crop_image_regions.py:475-523): collapses runs of
    /// closely-spaced polygon points into their mean position, keeping true corners apart. An edge
    /// shorter than 0.9× the mean edge length joins its endpoints into one cluster; a new cluster
    /// starts at each long edge; when the closing edge (last→first point) is also short, the last
    /// cluster folds into the first. Returns one mean point per cluster. Internal for unit tests.
    /// </summary>
    internal static OcrPoint[] ClusterPolygonPoints(IReadOnlyList<OcrPoint> line)
    {
        int n = line.Count;
        if (n < 2)
        {
            var copy = new OcrPoint[n];
            for (int i = 0; i < n; i++)
            {
                copy[i] = line[i];
            }
            return copy;
        }

        var edgeLen = new double[n - 1];
        double total = 0;
        for (int i = 0; i < n - 1; i++)
        {
            edgeLen[i] = Distance(line[i], line[i + 1]);
            total += edgeLen[i];
        }
        double mean = total / (edgeLen.Length + 1e-8);

        var groups = new List<List<int>> { new() { 0 } };
        var groupOf = new int[n];
        for (int i = 0; i < edgeLen.Length; i++)
        {
            int pointId = i + 1;
            if (edgeLen[i] < 0.9 * mean)
            {
                int g = groupOf[i];
                groups[g].Add(pointId);
                groupOf[pointId] = g;
            }
            else
            {
                groupOf[pointId] = groups.Count;
                groups.Add(new List<int> { pointId });
            }
        }

        // Wrap-around: a short closing edge means the last cluster and the first are one corner.
        double closing = Distance(line[0], line[n - 1]);
        if (closing < 0.9 * mean && groups.Count > 1)
        {
            groups[0].AddRange(groups[^1]);
            groups.RemoveAt(groups.Count - 1);
        }

        var means = new OcrPoint[groups.Count];
        for (int g = 0; g < groups.Count; g++)
        {
            double sx = 0, sy = 0;
            foreach (int idx in groups[g])
            {
                sx += line[idx].X;
                sy += line[idx].Y;
            }
            means[g] = new OcrPoint(sx / groups[g].Count, sy / groups[g].Count);
        }
        return means;
    }

    /// <summary>
    /// Port of <c>reorder_poly_edge</c> (crop_image_regions.py:210-244): finds the polygon's head and
    /// tail edges (the short ends of the text line) via <see cref="FindHeadTail"/> and returns the two
    /// sideline chains between them, each in the polygon's winding order (so they run in opposite
    /// directions along the line). Internal for unit tests.
    /// </summary>
    internal static (OcrPoint[] SideA, OcrPoint[] SideB) ReorderPolyEdge(OcrPoint[] points)
    {
        int n = points.Length;
        var (_, headEnd, _, tailEnd) = FindHeadTail(points, OrientationThreshold);
        if (tailEnd < 1)
        {
            tailEnd = n;
        }

        var sideA = SliceWrapped(points, headEnd, tailEnd);
        var sideB = SliceWrapped(points, tailEnd, headEnd + n);
        return (sideA, sideB);
    }

    /// <summary>
    /// Port of <c>find_head_tail</c> (crop_image_regions.py:262-370). For quads, the head/tail pair is
    /// picked by comparing summed edge slopes and lengths against <paramref name="orientationThr"/>.
    /// For denser polygons, every edge is scored by its angles to its neighbors (0.5·θ-sum + 0.15·
    /// adjacent-θ), its distance from the polygon center (0.35), and a positional prior for even point
    /// counts (0.1); a Gaussian-weighted pairing matrix then selects the head edge and its opposing
    /// tail edge. Returns start/end point indices of both edges. Internal for unit tests.
    /// </summary>
    internal static (int HeadStart, int HeadEnd, int TailStart, int TailEnd) FindHeadTail(
        OcrPoint[] points, double orientationThr)
    {
        int n = points.Length;
        if (n > 4)
        {
            // Edge vectors including the closing edge.
            var edgeVec = new OcrPoint[n];
            for (int i = 0; i < n; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % n];
                edgeVec[i] = new OcrPoint(b.X - a.X, b.Y - a.Y);
            }

            double cx = 0, cy = 0;
            foreach (var p in points)
            {
                cx += p.X;
                cy += p.Y;
            }
            cx /= n;
            cy /= n;

            var score = new double[n];
            double maxEdgeDist = 0;
            var edgeDist = new double[n];
            for (int i = 0; i < n; i++)
            {
                var prev = edgeVec[(i - 1 + n) % n];
                var next = edgeVec[(i + 1) % n];
                double thetaSum = VectorAngle(edgeVec[i], prev) + VectorAngle(edgeVec[i], next);
                double adjacentTheta = VectorAngle(prev, next);
                score[i] = 0.5 * (thetaSum / Math.PI) + 0.15 * (adjacentTheta / Math.PI);

                var pa = points[i];
                var pb = points[(i + 1) % n];
                double da = Math.Sqrt((pa.X - cx) * (pa.X - cx) + (pa.Y - cy) * (pa.Y - cy));
                double db = Math.Sqrt((pb.X - cx) * (pb.X - cx) + (pb.Y - cy) * (pb.Y - cy));
                edgeDist[i] = Math.Max(da, db);
                maxEdgeDist = Math.Max(maxEdgeDist, edgeDist[i]);
            }

            for (int i = 0; i < n; i++)
            {
                score[i] += 0.35 * (maxEdgeDist > 0 ? edgeDist[i] / maxEdgeDist : 0);
            }
            if (n % 2 == 0)
            {
                score[n / 2 - 1] += 0.1;
                score[n - 1] += 0.1;
            }

            // Gaussian pairing weights over the n-3 possible tail offsets.
            int m = n - 3;
            var gaussian = new double[m];
            double gMax = 0;
            for (int k = 0; k < m; k++)
            {
                double x = m == 1 ? 0 : k / (double)(n - 4);
                gaussian[k] = 1.0 / (Math.Sqrt(2.0 * Math.PI) * 0.5)
                              * Math.Exp(-Math.Pow((x - 0.5) / 0.5, 2.0) / 2.0);
                gMax = Math.Max(gMax, gaussian[k]);
            }
            for (int k = 0; k < m; k++)
            {
                gaussian[k] /= gMax;
            }

            double bestVal = double.NegativeInfinity;
            int headStart = 0, tailIncrement = 0;
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < m; j++)
                {
                    double val = score[i] + score[(i + 2 + j) % n] * gaussian[j] * 0.3;
                    if (val > bestVal) // strict: first max wins, like numpy argmax
                    {
                        bestVal = val;
                        headStart = i;
                        tailIncrement = j;
                    }
                }
            }

            int tailStart = (headStart + tailIncrement + 2) % n;
            int headEnd = (headStart + 1) % n;
            int tailEnd = (tailStart + 1) % n;
            if (headEnd > tailEnd)
            {
                (headStart, tailStart) = (tailStart, headStart);
                (headEnd, tailEnd) = (tailEnd, headEnd);
            }
            return (headStart, headEnd, tailStart, tailEnd);
        }
        else
        {
            // Quad: decide which edge pair is "horizontal" by summed slopes, then pick the short ends.
            int[][] horizontal, vertical;
            if (VectorSlope(Sub(points[1], points[0])) + VectorSlope(Sub(points[3], points[2])) <
                VectorSlope(Sub(points[2], points[1])) + VectorSlope(Sub(points[0], points[3])))
            {
                horizontal = new[] { new[] { 0, 1 }, new[] { 2, 3 } };
                vertical = new[] { new[] { 3, 0 }, new[] { 1, 2 } };
            }
            else
            {
                horizontal = new[] { new[] { 3, 0 }, new[] { 1, 2 } };
                vertical = new[] { new[] { 0, 1 }, new[] { 2, 3 } };
            }

            double verticalLen = Distance(points[vertical[0][0]], points[vertical[0][1]])
                                 + Distance(points[vertical[1][0]], points[vertical[1][1]]);
            double horizontalLen = Distance(points[horizontal[0][0]], points[horizontal[0][1]])
                                   + Distance(points[horizontal[1][0]], points[horizontal[1][1]]);

            int[] head, tail;
            if (verticalLen > horizontalLen * orientationThr)
            {
                head = horizontal[0];
                tail = horizontal[1];
            }
            else
            {
                head = vertical[0];
                tail = vertical[1];
            }
            return (head[0], head[1], tail[0], tail[1]);
        }
    }

    /// <summary>
    /// Port of <c>sample_points_on_bbox_bp</c> (crop_image_regions.py:428-473): resamples a polyline
    /// at <paramref name="n"/> equal arc-length steps, returning the original endpoints plus the n−1
    /// interior samples (n+1 points total). Internal for unit tests.
    /// </summary>
    internal static OcrPoint[] ResampleLine(IReadOnlyList<OcrPoint> line, int n)
    {
        int count = line.Count;
        if (count < 2 || n < 1)
        {
            var copy = new OcrPoint[count];
            for (int i = 0; i < count; i++)
            {
                copy[i] = line[i];
            }
            return copy;
        }

        var cumulative = new double[count];
        double total = 0;
        for (int i = 0; i < count - 1; i++)
        {
            total += Distance(line[i], line[i + 1]);
            cumulative[i + 1] = total;
        }

        double delta = total / (n + 1e-8);
        var result = new List<OcrPoint>(n + 1) { line[0] };
        int edge = 0;
        for (int i = 1; i < n; i++)
        {
            double target = i * delta;
            while (edge + 1 < count && target >= cumulative[edge + 1])
            {
                edge++;
            }
            if (edge >= count - 1)
            {
                break;
            }

            double segLen = cumulative[edge + 1] - cumulative[edge];
            double ratio = segLen > 0 ? (target - cumulative[edge]) / segLen : 0;
            result.Add(new OcrPoint(
                line[edge].X + (line[edge + 1].X - line[edge].X) * ratio,
                line[edge].Y + (line[edge + 1].Y - line[edge].Y) * ratio));
        }
        result.Add(line[count - 1]);
        return result.ToArray();
    }

    /// <summary>
    /// The divide-and-conquer homography (<c>dc_homo</c>, seal_det_warp.py:550-620): warps each
    /// adjacent point-pair quad (top[i], top[i+1], bottom[i+1], bottom[i]) to a strip of width
    /// (top-segment + bottom-segment)/2 and the line's mean height, then concatenates the strips
    /// horizontally. Bilinear sampling, black outside the source (cv2 INTER_LINEAR + BORDER_CONSTANT).
    /// Returns null when every segment is degenerate.
    /// </summary>
    private static Image<Rgb24>? PiecewiseRectify(Image<Rgb24> source, OcrPoint[] top, OcrPoint[] bottom)
    {
        int n = Math.Min(top.Length, bottom.Length);
        if (n < 2)
        {
            return null;
        }

        // Mean line height over all aligned point pairs (Python's np.around(np.mean(dy_list))).
        double heightSum = 0;
        for (int j = 0; j < n; j++)
        {
            heightSum += Distance(top[j], bottom[j]);
        }
        int stripHeight = (int)Math.Round(heightSum / n);
        if (stripHeight < 1)
        {
            return null;
        }

        // Copy the source once; every segment warp samples from this buffer.
        int srcW = source.Width, srcH = source.Height;
        var srcBuf = new Rgb24[srcW * srcH];
        source.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < srcH; y++)
            {
                rows.GetRowSpan(y).CopyTo(srcBuf.AsSpan(y * srcW, srcW));
            }
        });

        var strips = new List<(Rgb24[] Pixels, int Width)>(n - 1);
        int totalWidth = 0;
        for (int i = 0; i < n - 1; i++)
        {
            // world_width: mean of the top and mirrored bottom segment lengths, int-truncated.
            double width = (Distance(top[i], top[i + 1]) + Distance(bottom[i], bottom[i + 1])) / 2.0;
            int stripWidth = (int)width;
            if (stripWidth < 1)
            {
                continue;
            }

            var strip = WarpQuadToRect(
                srcBuf, srcW, srcH,
                top[i], top[i + 1], bottom[i + 1], bottom[i],
                stripWidth, stripHeight);
            if (strip is null)
            {
                continue;
            }

            strips.Add((strip, stripWidth));
            totalWidth += stripWidth;
        }

        if (totalWidth < 1)
        {
            return null;
        }

        // Stitch left to right (all strips share stripHeight, so no padding rows appear).
        var canvas = new Rgb24[totalWidth * stripHeight];
        int xOffset = 0;
        foreach (var (pixels, width) in strips)
        {
            for (int y = 0; y < stripHeight; y++)
            {
                Array.Copy(pixels, y * width, canvas, y * totalWidth + xOffset, width);
            }
            xOffset += width;
        }

        return Image.LoadPixelData<Rgb24>(canvas, totalWidth, stripHeight);
    }

    /// <summary>
    /// Warps the source quad (tl, tr, br, bl — explicit correspondence, no corner re-ordering) onto a
    /// <paramref name="width"/>×<paramref name="height"/> rectangle: solves the homography mapping the
    /// destination rect corners to the quad, then bilinear-samples the source per destination pixel
    /// with out-of-bounds taps contributing black. Returns null when the homography is singular.
    /// </summary>
    private static Rgb24[]? WarpQuadToRect(
        Rgb24[] srcBuf, int srcW, int srcH,
        OcrPoint tl, OcrPoint tr, OcrPoint br, OcrPoint bl,
        int width, int height)
    {
        var from = new[]
        {
            new OcrPoint(0, 0), new OcrPoint(width, 0),
            new OcrPoint(width, height), new OcrPoint(0, height),
        };
        var to = new[] { tl, tr, br, bl };
        var h = SolveHomography(from, to);
        if (h is null)
        {
            return null;
        }

        var dst = new Rgb24[width * height];
        for (int v = 0; v < height; v++)
        {
            for (int u = 0; u < width; u++)
            {
                double denom = h[6] * u + h[7] * v + h[8];
                if (Math.Abs(denom) < 1e-9)
                {
                    continue;
                }
                double sx = (h[0] * u + h[1] * v + h[2]) / denom;
                double sy = (h[3] * u + h[4] * v + h[5]) / denom;
                dst[v * width + u] = BilinearSample(srcBuf, srcW, srcH, sx, sy);
            }
        }
        return dst;
    }

    /// <summary>
    /// Bilinear interpolation at (x, y) with cv2's BORDER_CONSTANT semantics: taps outside the image
    /// contribute black rather than clamping to the edge.
    /// </summary>
    private static Rgb24 BilinearSample(Rgb24[] buf, int w, int h, double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;

        double r = 0, g = 0, b = 0;
        for (int j = 0; j <= 1; j++)
        {
            int sy = y0 + j;
            if (sy < 0 || sy >= h)
            {
                continue;
            }
            double wy = j == 0 ? 1 - fy : fy;
            int rowOffset = sy * w;
            for (int i = 0; i <= 1; i++)
            {
                int sx = x0 + i;
                if (sx < 0 || sx >= w)
                {
                    continue;
                }
                double weight = wy * (i == 0 ? 1 - fx : fx);
                Rgb24 p = buf[rowOffset + sx];
                r += weight * p.R;
                g += weight * p.G;
                b += weight * p.B;
            }
        }

        return new Rgb24(
            (byte)Math.Clamp(r + 0.5, 0, 255),
            (byte)Math.Clamp(g + 0.5, 0, 255),
            (byte)Math.Clamp(b + 0.5, 0, 255));
    }

    /// <summary>
    /// Solves the 3x3 homography H (8 DOF, h[8] = 1) mapping the four <paramref name="from"/> points
    /// to the four <paramref name="to"/> points via the DLT linear system (same approach as
    /// <see cref="PerspectiveWarp"/>, whose solver is private). Returns row-major length-9
    /// coefficients, or null if the system is singular.
    /// </summary>
    private static double[]? SolveHomography(OcrPoint[] from, OcrPoint[] to)
    {
        var a = new double[8, 8];
        var bvec = new double[8];
        for (int i = 0; i < 4; i++)
        {
            double x = from[i].X, y = from[i].Y, X = to[i].X, Y = to[i].Y;
            int r = i * 2;
            a[r, 0] = x; a[r, 1] = y; a[r, 2] = 1; a[r, 6] = -x * X; a[r, 7] = -y * X;
            bvec[r] = X;
            int r2 = r + 1;
            a[r2, 3] = x; a[r2, 4] = y; a[r2, 5] = 1; a[r2, 6] = -x * Y; a[r2, 7] = -y * Y;
            bvec[r2] = Y;
        }

        const int size = 8;
        var sol = new double[size];
        for (int col = 0; col < size; col++)
        {
            int pivot = col;
            double best = Math.Abs(a[col, col]);
            for (int row = col + 1; row < size; row++)
            {
                double v = Math.Abs(a[row, col]);
                if (v > best)
                {
                    best = v;
                    pivot = row;
                }
            }
            if (best < 1e-12)
            {
                return null;
            }
            if (pivot != col)
            {
                for (int c = 0; c < size; c++)
                {
                    (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                }
                (bvec[col], bvec[pivot]) = (bvec[pivot], bvec[col]);
            }
            for (int row = col + 1; row < size; row++)
            {
                double f = a[row, col] / a[col, col];
                for (int c = col; c < size; c++)
                {
                    a[row, c] -= f * a[col, c];
                }
                bvec[row] -= f * bvec[col];
            }
        }
        for (int row = size - 1; row >= 0; row--)
        {
            double sum = bvec[row];
            for (int c = row + 1; c < size; c++)
            {
                sum -= a[row, c] * sol[c];
            }
            sol[row] = sum / a[row, row];
        }

        return new[] { sol[0], sol[1], sol[2], sol[3], sol[4], sol[5], sol[6], sol[7], 1.0 };
    }

    /// <summary>
    /// Slices points[start..endExclusive) out of the doubled point ring (indices wrap modulo the
    /// polygon length) — Python's <c>pad_points = vstack([points, points])</c> indexing.
    /// </summary>
    private static OcrPoint[] SliceWrapped(OcrPoint[] points, int start, int endExclusive)
    {
        int count = Math.Max(0, endExclusive - start);
        var result = new OcrPoint[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = points[(start + i) % points.Length];
        }
        return result;
    }

    /// <summary>
    /// <c>horizontal_text_estimate</c>: a bbox taller than 1.5× its width is vertical text.
    /// </summary>
    private static bool IsVertical(OcrPoint[] top, OcrPoint[] bottom)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var chain in new[] { top, bottom })
        {
            foreach (var p in chain)
            {
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
            }
        }
        double w = maxX - minX;
        double h = maxY - minY;
        return w <= 0 || h / w > 1.5;
    }

    /// <summary>
    /// Angle between two vectors in radians — <c>vector_angle</c> (arccos of the clipped unit dot).
    /// </summary>
    private static double VectorAngle(OcrPoint a, OcrPoint b)
    {
        double na = Math.Sqrt(a.X * a.X + a.Y * a.Y) + 1e-8;
        double nb = Math.Sqrt(b.X * b.X + b.Y * b.Y) + 1e-8;
        double dot = (a.X / na) * (b.X / nb) + (a.Y / na) * (b.Y / nb);
        return Math.Acos(Math.Clamp(dot, -1.0, 1.0));
    }

    /// <summary>
    /// <c>vector_slope</c>: |dy / (dx + 1e-8)|.
    /// </summary>
    private static double VectorSlope(OcrPoint v) => Math.Abs(v.Y / (v.X + 1e-8));

    private static OcrPoint Sub(OcrPoint a, OcrPoint b) => new(a.X - b.X, a.Y - b.Y);

    private static double MeanY(OcrPoint[] points)
    {
        double sum = 0;
        foreach (var p in points)
        {
            sum += p.Y;
        }
        return points.Length == 0 ? 0 : sum / points.Length;
    }

    private static double Distance(OcrPoint a, OcrPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static PathD ToPath(IReadOnlyList<OcrPoint> polygon)
    {
        var path = new PathD(polygon.Count);
        foreach (var p in polygon)
        {
            path.Add(new PointD(p.X, p.Y));
        }
        return path;
    }

    private static double SumAbsArea(PathsD paths)
    {
        double area = 0;
        foreach (var path in paths)
        {
            area += Math.Abs(Clipper.Area(path));
        }
        return area;
    }
}
