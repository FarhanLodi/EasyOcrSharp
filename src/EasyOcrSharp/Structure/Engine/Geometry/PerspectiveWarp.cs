using EasyOcrSharp.Structure.Engine.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Engine.Geometry;

/// <summary>
/// Rectifies a (possibly rotated) quadrilateral text region into an upright rectangle —
/// the equivalent of PaddleOCR's <c>get_rotate_crop_image</c> / four-point transform. Perfectly
/// axis-aligned quads take a fast crop path; every other quad (small tilts included) is warped with
/// a homography + bicubic sampling (OpenCV's <c>INTER_CUBIC</c> kernel with <c>BORDER_REPLICATE</c>
/// edge handling) so slanted text is straightened before recognition.
/// </summary>
internal static class PerspectiveWarp
{
    /// <summary>
    /// Rectifies <paramref name="quad"/> out of <paramref name="source"/> into an upright crop.
    /// When <paramref name="rotateVertical"/> is set and the crop is tall (height/width ≥ 1.5), it
    /// is rotated 90° counter-clockwise — PaddleOCR's <c>np.rot90</c> heuristic that lets vertical
    /// text lines reach the recognizer horizontally. Returns null for degenerate (sub-2px) regions.
    /// </summary>
    public static Image<Rgb24>? Rectify(Image<Rgb24> source, OcrPoint[] quad, bool rotateVertical = false)
        => Rectify(source, quad, rotateVertical, out _);

    /// <summary>
    /// Rectifies <paramref name="quad"/> like <see cref="Rectify(Image{Rgb24}, OcrPoint[], bool)"/> and also
    /// reports how the returned crop maps back onto <paramref name="source"/> (used to place word boxes).
    /// Safe to call concurrently on the same <paramref name="source"/>: the source is only read.
    /// </summary>
    /// <param name="source">The image to crop from (read only).</param>
    /// <param name="quad">The region polygon.</param>
    /// <param name="rotateVertical">Rotate tall crops 90° counter-clockwise (PaddleOCR's <c>np.rot90</c> rule).</param>
    /// <param name="geometry">The crop's geometry; <c>default</c> when the result is null.</param>
    /// <returns>The upright crop (caller owns it), or null for a degenerate region.</returns>
    public static Image<Rgb24>? Rectify(Image<Rgb24> source, OcrPoint[] quad, bool rotateVertical, out CropGeometry geometry)
    {
        var crop = RectifyUpright(source, quad, out geometry);
        if (crop is null) return null;

        if (rotateVertical && crop.Height / (double)crop.Width >= 1.5)
        {
            crop.Mutate(ctx => ctx.Rotate(RotateMode.Rotate270)); // 270° CW == 90° CCW == np.rot90
            geometry = geometry with { RotatedVertical = true };
        }
        return crop;
    }

    private static Image<Rgb24>? RectifyUpright(Image<Rgb24> source, OcrPoint[] quad, out CropGeometry geometry)
    {
        geometry = default;
        if (quad.Length < 4) return AxisAlignedCrop(source, quad, out geometry);

        // Order corners as top-left, top-right, bottom-right, bottom-left.
        var (tl, tr, br, bl) = OrderCorners(quad);

        // Python sizes the destination rect by truncating the edge norms (int(), not round).
        int dstW = (int)Math.Max(Distance(tl, tr), Distance(bl, br));
        int dstH = (int)Math.Max(Distance(tl, bl), Distance(tr, br));
        if (dstW < 2 || dstH < 2) return null;

        // Only a perfectly axis-aligned rectangle may skip the warp; any tilt, however small,
        // must be deskewed (Python always warps).
        if (IsAxisAligned(tl, tr, br, bl))
        {
            return AxisAlignedCrop(source, quad, out geometry);
        }

        // Homography mapping destination rectangle corners -> source quad corners. cv2's pts_std
        // places the far corners at (W,0),(W,H),(0,H) — not (W-1,H-1).
        var dst = new[]
        {
            new OcrPoint(0, 0), new OcrPoint(dstW, 0),
            new OcrPoint(dstW, dstH), new OcrPoint(0, dstH),
        };
        var src = new[] { tl, tr, br, bl };
        var h = ComputeHomography(dst, src);
        if (h is null) return AxisAlignedCrop(source, quad, out geometry);

        // Copy only the source bounding box of the quad (plus a 2px halo for the 4×4 bicubic
        // support), not the whole frame — a slanted box on a 2560² page otherwise copied ~20 MB per
        // region. Homography sample coordinates are in full-image space, so subtract the sub-rect
        // origin before sampling. Tap clamping inside BicubicSample replicates edge pixels
        // (BORDER_REPLICATE); samples stay inside the quad, so the sub-rect edge is only ever
        // replicated where it coincides with the image edge.
        double qMinX = Math.Min(Math.Min(tl.X, tr.X), Math.Min(br.X, bl.X));
        double qMinY = Math.Min(Math.Min(tl.Y, tr.Y), Math.Min(br.Y, bl.Y));
        double qMaxX = Math.Max(Math.Max(tl.X, tr.X), Math.Max(br.X, bl.X));
        double qMaxY = Math.Max(Math.Max(tl.Y, tr.Y), Math.Max(br.Y, bl.Y));
        int ox = Math.Max(0, (int)Math.Floor(qMinX) - 2);
        int oy = Math.Max(0, (int)Math.Floor(qMinY) - 2);
        int ex = Math.Min(source.Width, (int)Math.Ceiling(qMaxX) + 2);
        int ey = Math.Min(source.Height, (int)Math.Ceiling(qMaxY) + 2);
        int sw = ex - ox, sh = ey - oy;
        if (sw < 2 || sh < 2) return AxisAlignedCrop(source, quad, out geometry);

        var srcBuf = new Rgb24[sw * sh];
        source.ProcessPixelRows(rows =>
        {
            for (int yy = 0; yy < sh; yy++)
            {
                var row = rows.GetRowSpan(oy + yy);
                row.Slice(ox, sw).CopyTo(srcBuf.AsSpan(yy * sw, sw));
            }
        });

        var dstBuf = new Rgb24[dstW * dstH];
        for (int v = 0; v < dstH; v++)
        {
            for (int u = 0; u < dstW; u++)
            {
                double denom = h[6] * u + h[7] * v + h[8];
                if (Math.Abs(denom) < 1e-9) continue;
                double sx = (h[0] * u + h[1] * v + h[2]) / denom;
                double sy = (h[3] * u + h[4] * v + h[5]) / denom;
                // A degenerate homography yields NaN/Infinity here. The sampler's range clamps are both
                // false for NaN, so it would sample at NaN and silently write black; leave the pixel at the
                // buffer default instead.
                if (!double.IsFinite(sx) || !double.IsFinite(sy)) continue;
                dstBuf[v * dstW + u] = BicubicSample(srcBuf, sw, sh, sx - ox, sy - oy);
            }
        }
        geometry = new CropGeometry(tl, tr, br, bl, dstW, dstH, RotatedVertical: false);
        return Image.LoadPixelData<Rgb24>(dstBuf, dstW, dstH);
    }

    private static Image<Rgb24>? AxisAlignedCrop(Image<Rgb24> source, OcrPoint[] quad, out CropGeometry geometry)
    {
        geometry = default;
        if (quad.Length < 3) return null;
        double minX = quad.Min(p => p.X), minY = quad.Min(p => p.Y);
        double maxX = quad.Max(p => p.X), maxY = quad.Max(p => p.Y);

        int x = Math.Max(0, (int)Math.Floor(minX));
        int y = Math.Max(0, (int)Math.Floor(minY));
        int w = (int)Math.Ceiling(maxX - minX);
        int h = (int)Math.Ceiling(maxY - minY);
        if (x + w > source.Width) w = source.Width - x;
        if (y + h > source.Height) h = source.Height - y;
        if (w < 2 || h < 2) return null;

        geometry = new CropGeometry(
            new OcrPoint(x, y), new OcrPoint(x + w, y), new OcrPoint(x + w, y + h), new OcrPoint(x, y + h),
            w, h, RotatedVertical: false);
        return source.Clone(ctx => ctx.Crop(new Rectangle(x, y, w, h)));
    }

    /// <summary>
    /// Samples <paramref name="buf"/> at (<paramref name="x"/>, <paramref name="y"/>) over the 4×4
    /// neighborhood with OpenCV's <c>INTER_CUBIC</c> convolution kernel, clamping out-of-range taps
    /// to the nearest edge pixel (<c>BORDER_REPLICATE</c>).
    /// </summary>
    private static Rgb24 BicubicSample(Rgb24[] buf, int w, int h, double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        Span<double> wx = stackalloc double[4];
        Span<double> wy = stackalloc double[4];
        CubicWeights(x - x0, wx);
        CubicWeights(y - y0, wy);

        double r = 0, g = 0, b = 0;
        if (w >= 4 && h >= 4 && (uint)(x0 - 1) <= (uint)(w - 4) && (uint)(y0 - 1) <= (uint)(h - 4))
        {
            // Interior fast path: all 16 taps are in range, so no clamping is needed. The summation order is
            // identical to the clamped loop below, so the result is bit-for-bit the same.
            for (int j = 0; j < 4; j++)
            {
                int rowOffset = (y0 - 1 + j) * w;
                for (int i = 0; i < 4; i++)
                {
                    double weight = wy[j] * wx[i];
                    Rgb24 p = buf[rowOffset + x0 - 1 + i];
                    r += weight * p.R;
                    g += weight * p.G;
                    b += weight * p.B;
                }
            }
        }
        else
        for (int j = 0; j < 4; j++)
        {
            int sy = Math.Clamp(y0 - 1 + j, 0, h - 1);
            int rowOffset = sy * w;
            for (int i = 0; i < 4; i++)
            {
                int sx = Math.Clamp(x0 - 1 + i, 0, w - 1);
                double weight = wy[j] * wx[i];
                Rgb24 p = buf[rowOffset + sx];
                r += weight * p.R;
                g += weight * p.G;
                b += weight * p.B;
            }
        }

        // The cubic kernel has negative lobes, so channel sums can over/undershoot [0,255].
        return new Rgb24(
            (byte)Math.Clamp(r + 0.5, 0, 255),
            (byte)Math.Clamp(g + 0.5, 0, 255),
            (byte)Math.Clamp(b + 0.5, 0, 255));
    }

    /// <summary>
    /// OpenCV's cubic convolution weights (<c>interpolateCubic</c>, A = −0.75) for the four taps at
    /// offsets −1..2 around the sample, where <paramref name="t"/> is the fractional position.
    /// </summary>
    private static void CubicWeights(double t, Span<double> w)
    {
        const double a = -0.75;
        w[0] = ((a * (t + 1) - 5 * a) * (t + 1) + 8 * a) * (t + 1) - 4 * a;
        w[1] = ((a + 2) * t - (a + 3)) * t * t + 1;
        w[2] = ((a + 2) * (1 - t) - (a + 3)) * (1 - t) * (1 - t) + 1;
        w[3] = 1 - w[0] - w[1] - w[2];
    }

    /// <summary>
    /// Orders four corners as TL, TR, BR, BL by x-sorting: the two left-most points form the left edge (top =
    /// smaller y), the two right-most the right edge. This is PaddleOCR's <c>get_mini_boxes</c> ordering, and
    /// the codebase already uses it in <c>DBPostProcess.OrderQuad</c>.
    /// <para>
    /// It replaces a sum/difference heuristic (tl = min(x+y), tr = min(y−x), …) that degenerates when a quad
    /// edge lies along a ±45° diagonal: the metric is constant along such an edge, so the tie-break handed the
    /// SAME point to two roles. A text line rotated exactly 45° — a diagonal watermark or stamp — produced
    /// tl == tr, which rectified a 100×20 line as a 20×102 vertical sliver and generated non-finite sample
    /// coordinates; alternatively dstW collapsed to 0 and the region was silently dropped. Sorting by x
    /// always assigns four distinct points to four distinct roles.
    /// </para>
    /// </summary>
    private static (OcrPoint tl, OcrPoint tr, OcrPoint br, OcrPoint bl) OrderCorners(OcrPoint[] quad)
    {
        // get_minarea_rect_crop's rule: stable-sort by x; the two leftmost points ordered by y give
        // TL/BL and the two rightmost give TR/BR (crop_image_regions.py:144-161). Equal-y pairs
        // follow Python's strict "y >" comparison.
        var byX = quad.OrderBy(p => p.X).ToArray();
        OcrPoint left0 = byX[0], left1 = byX[1], right0 = byX[^2], right1 = byX[^1];
        var (tl, bl) = left1.Y > left0.Y ? (left0, left1) : (left1, left0);
        var (tr, br) = right1.Y > right0.Y ? (right0, right1) : (right1, right0);
        return (tl, tr, br, bl);
    }

    private static bool IsAxisAligned(OcrPoint tl, OcrPoint tr, OcrPoint br, OcrPoint bl)
    {
        // Only an exactly axis-aligned rectangle qualifies for the bbox-crop fast path; Python
        // always warps, so even a fraction of a degree of tilt must be deskewed here.
        return tl.Y == tr.Y && bl.Y == br.Y && tl.X == bl.X && tr.X == br.X;
    }

    private static double Distance(OcrPoint a, OcrPoint b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>
    /// Solves the 3x3 homography H (8 DOF, h[8]=1) mapping the four <paramref name="from"/> points
    /// to the four <paramref name="to"/> points via the DLT linear system. Returns row-major
    /// length-9 coefficients, or null if the system is singular.
    /// </summary>
    private static double[]? ComputeHomography(OcrPoint[] from, OcrPoint[] to)
    {
        // Build 8x8 A and 8x1 b for: to = H * from, with h[8] fixed to 1.
        var a = new double[8, 8];
        var bvec = new double[8];
        for (int i = 0; i < 4; i++)
        {
            double x = from[i].X, y = from[i].Y, X = to[i].X, Y = to[i].Y;
            int r = i * 2;
            a[r, 0] = x; a[r, 1] = y; a[r, 2] = 1; a[r, 3] = 0; a[r, 4] = 0; a[r, 5] = 0; a[r, 6] = -x * X; a[r, 7] = -y * X;
            bvec[r] = X;
            int r2 = r + 1;
            a[r2, 0] = 0; a[r2, 1] = 0; a[r2, 2] = 0; a[r2, 3] = x; a[r2, 4] = y; a[r2, 5] = 1; a[r2, 6] = -x * Y; a[r2, 7] = -y * Y;
            bvec[r2] = Y;
        }

        if (!SolveLinear(a, bvec, out var sol)) return null;
        return new[] { sol[0], sol[1], sol[2], sol[3], sol[4], sol[5], sol[6], sol[7], 1.0 };
    }

    /// <summary>
    /// Gaussian elimination with partial pivoting for an 8x8 system.
    /// </summary>
    private static bool SolveLinear(double[,] a, double[] b, out double[] x)
    {
        const int n = 8;
        x = new double[n];
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            double best = Math.Abs(a[col, col]);
            for (int r = col + 1; r < n; r++)
            {
                double v = Math.Abs(a[r, col]);
                if (v > best) { best = v; pivot = r; }
            }
            if (best < 1e-12) return false;
            if (pivot != col)
            {
                for (int c = 0; c < n; c++) (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }
            for (int r = col + 1; r < n; r++)
            {
                double f = a[r, col] / a[col, col];
                for (int c = col; c < n; c++) a[r, c] -= f * a[col, c];
                b[r] -= f * b[col];
            }
        }
        for (int row = n - 1; row >= 0; row--)
        {
            double sum = b[row];
            for (int c = row + 1; c < n; c++) sum -= a[row, c] * x[c];
            x[row] = sum / a[row, row];
        }
        return true;
    }
}
