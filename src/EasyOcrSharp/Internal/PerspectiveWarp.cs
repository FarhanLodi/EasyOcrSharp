using EasyOcrSharp.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Rectifies a (possibly rotated) quadrilateral text region into an upright rectangle —
/// a port of EasyOCR's <c>four_point_transform</c>. Axis-aligned quads take a fast crop path;
/// genuinely rotated quads are warped with a homography + bilinear sampling so slanted text
/// is straightened before recognition.
/// </summary>
internal static class PerspectiveWarp
{
    public static Image<Rgb24>? Rectify(Image<Rgb24> source, OcrPoint[] quad)
    {
        if (quad.Length < 4) return AxisAlignedCrop(source, quad);

        // Order corners as top-left, top-right, bottom-right, bottom-left.
        var (tl, tr, br, bl) = OrderCorners(quad);

        double widthTop = Distance(tl, tr), widthBottom = Distance(bl, br);
        double heightLeft = Distance(tl, bl), heightRight = Distance(tr, br);
        int dstW = (int)Math.Round(Math.Max(widthTop, widthBottom));
        int dstH = (int)Math.Round(Math.Max(heightLeft, heightRight));
        if (dstW < 2 || dstH < 2) return null;

        // If the quad is essentially an axis-aligned rectangle, the cheap crop is identical.
        if (IsAxisAligned(tl, tr, br, bl))
        {
            return AxisAlignedCrop(source, quad);
        }

        // Homography mapping destination rectangle corners -> source quad corners.
        var dst = new[]
        {
            new OcrPoint(0, 0), new OcrPoint(dstW - 1, 0),
            new OcrPoint(dstW - 1, dstH - 1), new OcrPoint(0, dstH - 1),
        };
        var src = new[] { tl, tr, br, bl };
        var h = ComputeHomography(dst, src);
        if (h is null) return AxisAlignedCrop(source, quad);

        // Copy only the source bounding box of the quad (plus a 1px halo for bilinear sampling), not the
        // whole frame — a slanted box on a 2560² page otherwise copied ~20 MB per region. Homography
        // sample coordinates are in full-image space, so subtract the sub-rect origin before sampling.
        double qMinX = Math.Min(Math.Min(tl.X, tr.X), Math.Min(br.X, bl.X));
        double qMinY = Math.Min(Math.Min(tl.Y, tr.Y), Math.Min(br.Y, bl.Y));
        double qMaxX = Math.Max(Math.Max(tl.X, tr.X), Math.Max(br.X, bl.X));
        double qMaxY = Math.Max(Math.Max(tl.Y, tr.Y), Math.Max(br.Y, bl.Y));
        int ox = Math.Max(0, (int)Math.Floor(qMinX) - 1);
        int oy = Math.Max(0, (int)Math.Floor(qMinY) - 1);
        int ex = Math.Min(source.Width, (int)Math.Ceiling(qMaxX) + 1);
        int ey = Math.Min(source.Height, (int)Math.Ceiling(qMaxY) + 1);
        int sw = ex - ox, sh = ey - oy;
        if (sw < 2 || sh < 2) return AxisAlignedCrop(source, quad);

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
                dstBuf[v * dstW + u] = BilinearSample(srcBuf, sw, sh, sx - ox, sy - oy);
            }
        }
        return Image.LoadPixelData<Rgb24>(dstBuf, dstW, dstH);
    }

    private static Image<Rgb24>? AxisAlignedCrop(Image<Rgb24> source, OcrPoint[] quad)
    {
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

        return source.Clone(ctx => ctx.Crop(new Rectangle(x, y, w, h)));
    }

    private static Rgb24 BilinearSample(Rgb24[] buf, int w, int h, double x, double y)
    {
        if (x < 0) x = 0; else if (x > w - 1) x = w - 1;
        if (y < 0) y = 0; else if (y > h - 1) y = h - 1;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        double fx = x - x0, fy = y - y0;

        Rgb24 p00 = buf[y0 * w + x0], p10 = buf[y0 * w + x1], p01 = buf[y1 * w + x0], p11 = buf[y1 * w + x1];

        byte Lerp(byte a, byte b, byte c, byte d) =>
            (byte)Math.Clamp(
                a * (1 - fx) * (1 - fy) + b * fx * (1 - fy) + c * (1 - fx) * fy + d * fx * fy + 0.5,
                0, 255);

        return new Rgb24(
            Lerp(p00.R, p10.R, p01.R, p11.R),
            Lerp(p00.G, p10.G, p01.G, p11.G),
            Lerp(p00.B, p10.B, p01.B, p11.B));
    }

    private static (OcrPoint tl, OcrPoint tr, OcrPoint br, OcrPoint bl) OrderCorners(OcrPoint[] quad)
    {
        // tl = min(x+y), br = max(x+y), tr = min(y-x), bl = max(y-x).
        OcrPoint tl = quad[0], br = quad[0], tr = quad[0], bl = quad[0];
        double minSum = double.MaxValue, maxSum = double.MinValue, minDiff = double.MaxValue, maxDiff = double.MinValue;
        foreach (var p in quad)
        {
            double sum = p.X + p.Y, diff = p.Y - p.X;
            if (sum < minSum) { minSum = sum; tl = p; }
            if (sum > maxSum) { maxSum = sum; br = p; }
            if (diff < minDiff) { minDiff = diff; tr = p; }
            if (diff > maxDiff) { maxDiff = diff; bl = p; }
        }
        return (tl, tr, br, bl);
    }

    private static bool IsAxisAligned(OcrPoint tl, OcrPoint tr, OcrPoint br, OcrPoint bl)
    {
        // Near-zero slope on top/bottom edges => treat as axis-aligned.
        double topSlope = Math.Abs(tr.Y - tl.Y) / Math.Max(1.0, Math.Abs(tr.X - tl.X));
        double botSlope = Math.Abs(br.Y - bl.Y) / Math.Max(1.0, Math.Abs(br.X - bl.X));
        return Math.Max(topSlope, botSlope) < 0.02;
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

    /// <summary>Gaussian elimination with partial pivoting for an 8x8 system.</summary>
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
