using EasyOcrSharp.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Internal;

/// <summary>
/// The coordinate frame of a page that <see cref="PreprocessingOptions.Deskew"/> rotated: EasyImageSharp's
/// deskew rotates the page clockwise by <see cref="RotationDegrees"/> about its centre onto an enlarged canvas
/// (<see cref="CanvasWidth"/>×<see cref="CanvasHeight"/>) whose exposed corners are filled white. Detection and
/// recognition run on that canvas; this maps their coordinates back onto the
/// <see cref="SourceWidth"/>×<see cref="SourceHeight"/> image that was deskewed, and maps a caller's region the
/// other way.
/// </summary>
/// <remarks>
/// The maps are exact inverses of the rotation's own sampling grid: EasyImageSharp samples canvas pixel centre
/// <c>c</c> at source position <c>R(θ)·(c − centre_canvas) + centre_source</c> in continuous (pixel-corner)
/// coordinates, with <c>R(θ) = [[cos θ, sin θ], [−sin θ, cos θ]]</c>.
/// </remarks>
/// <param name="RotationDegrees">Clockwise rotation applied to the source, in degrees (non-zero).</param>
/// <param name="CanvasWidth">Width of the rotated canvas.</param>
/// <param name="CanvasHeight">Height of the rotated canvas.</param>
/// <param name="SourceWidth">Width of the image before deskew.</param>
/// <param name="SourceHeight">Height of the image before deskew.</param>
internal readonly record struct DeskewCanvas(float RotationDegrees, int CanvasWidth, int CanvasHeight, int SourceWidth, int SourceHeight)
{
    /// <summary>
    /// Maps a canvas point onto the source image, clamped to its bounds: a box that reached into the white
    /// corners of the canvas ends at the page edge, as detector boxes do on an unrotated page.
    /// </summary>
    internal OcrPoint ToSource(OcrPoint p)
    {
        var s = ToSourceUnclamped(p);
        return new OcrPoint(Math.Clamp(s.X, 0, SourceWidth), Math.Clamp(s.Y, 0, SourceHeight));
    }

    /// <summary>Maps a canvas point onto the source image without clamping.</summary>
    internal OcrPoint ToSourceUnclamped(OcrPoint p)
    {
        double rad = RotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double tx = p.X - (CanvasWidth / 2.0);
        double ty = p.Y - (CanvasHeight / 2.0);
        return new OcrPoint(
            (cos * tx) + (sin * ty) + (SourceWidth / 2.0),
            (-sin * tx) + (cos * ty) + (SourceHeight / 2.0));
    }

    /// <summary>Maps a source point onto the canvas (the inverse of <see cref="ToSourceUnclamped"/>).</summary>
    internal OcrPoint ToCanvas(OcrPoint p)
    {
        double rad = RotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double tx = p.X - (SourceWidth / 2.0);
        double ty = p.Y - (SourceHeight / 2.0);
        return new OcrPoint(
            (cos * tx) - (sin * ty) + (CanvasWidth / 2.0),
            (sin * tx) + (cos * ty) + (CanvasHeight / 2.0));
    }

    /// <summary>
    /// Re-expresses a caller's region (given against the source image) on the canvas: the axis-aligned bounds
    /// of the region's rotated corners, in canvas pixels. The rotated rectangle's bounds are slightly larger
    /// than the region, so text just past a corner of the region can be read too. A region that resolves to
    /// nothing on the source stays empty.
    /// </summary>
    internal OcrRegion RegionToCanvas(OcrRegion region)
    {
        var (x, y, w, h) = region.Resolve(SourceWidth, SourceHeight);
        if (w <= 0 || h <= 0) return OcrRegion.Pixels(0, 0, 0, 0);

        var box = OcrBoundingBox.FromPoints(new[]
        {
            ToCanvas(new OcrPoint(x, y)), ToCanvas(new OcrPoint(x + w, y)),
            ToCanvas(new OcrPoint(x + w, y + h)), ToCanvas(new OcrPoint(x, y + h)),
        });
        return OcrRegion.Pixels(box.MinX, box.MinY, box.Width, box.Height);
    }

    /// <summary>Maps recognized lines — with their words and characters — from the canvas onto the source image.</summary>
    internal IReadOnlyList<OcrLine> LinesToSource(IReadOnlyList<OcrLine> lines)
    {
        var self = this;
        return LineGeometry.Transform(lines, p => self.ToSource(p));
    }

    /// <summary>Maps one recognized line from the canvas onto the source image.</summary>
    internal OcrLine LineToSource(OcrLine line)
    {
        var self = this;
        return LineGeometry.Transform(line, p => self.ToSource(p));
    }
}

/// <summary>
/// Recovers the rotation <see cref="ImagePreprocessor"/>'s deskew step applied. EasyImageSharp's
/// <c>Deskew(DeskewOptions)</c> rotates the page but does not report the angle, and its projection-profile
/// estimator is internal, so this carries an exact port of that estimator (EasyImageSharp 1.1.0
/// <c>DeskewOps.EstimateProjectionSkew</c>) and cross-checks the result against the canvas the library
/// actually produced. The page pixels themselves still come from the library, unchanged.
/// </summary>
internal static class DeskewRotation
{
    /// <summary>Longest side of the strided sample the estimator scores angles on (EasyImageSharp's constant).</summary>
    private const int MaxSampleDimension = 1200;

    /// <summary>
    /// The clockwise rotation (degrees) that turned a <paramref name="sourceWidth"/>×<paramref name="sourceHeight"/>
    /// page into an <paramref name="outputWidth"/>×<paramref name="outputHeight"/> canvas, given the skew the
    /// estimator port measured on the page (<paramref name="estimatedSkew"/>, the content's clockwise skew; the
    /// library rotates by its negation). Returns 0 when the canvas has the source's size (the library declined to
    /// rotate). When the port's angle does not reproduce the canvas size — a future imaging library whose
    /// estimator differs — the magnitude is recovered from the canvas growth and the port's direction kept.
    /// </summary>
    internal static float ResolveApplied(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight, double estimatedSkew)
    {
        if (outputWidth == sourceWidth && outputHeight == sourceHeight) return 0f;

        float candidate = (float)(-estimatedSkew);
        if (candidate != 0f && PredictCanvasSize(sourceWidth, sourceHeight, candidate) == (outputWidth, outputHeight))
        {
            return candidate;
        }

        float sign = candidate < 0f ? -1f : 1f;
        double wanted = Math.Abs(candidate);
        double best = double.NaN, bestDistance = double.PositiveInfinity;
        double fallback = 0.01, fallbackError = double.PositiveInfinity;
        for (int i = 1; i <= 4500; i++)
        {
            float magnitude = i / 100f;
            var (w, h) = PredictCanvasSize(sourceWidth, sourceHeight, magnitude);
            int error = Math.Abs(w - outputWidth) + Math.Abs(h - outputHeight);
            if (error == 0)
            {
                double distance = wanted > 0 ? Math.Abs(magnitude - wanted) : 0;
                if (double.IsNaN(best) || distance < bestDistance)
                {
                    best = magnitude;
                    bestDistance = distance;
                }
            }
            else if (error < fallbackError)
            {
                fallback = magnitude;
                fallbackError = error;
            }
        }
        return sign * (float)(double.IsNaN(best) ? fallback : best);
    }

    /// <summary>
    /// The canvas EasyImageSharp's arbitrary-angle rotation produces for a <paramref name="width"/>×<paramref name="height"/>
    /// image rotated by <paramref name="degrees"/>: <c>ceil(|w cos| + |h sin|)</c> × <c>ceil(|w sin| + |h cos|)</c>.
    /// </summary>
    internal static (int Width, int Height) PredictCanvasSize(int width, int height, float degrees)
    {
        double radians = degrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        int newW = Math.Max(1, (int)Math.Ceiling(Math.Abs(width * cos) + Math.Abs(height * sin)));
        int newH = Math.Max(1, (int)Math.Ceiling(Math.Abs(width * sin) + Math.Abs(height * cos)));
        return (newW, newH);
    }

    /// <summary>
    /// Port of EasyImageSharp 1.1.0's projection-profile skew estimator: the clockwise skew of the content in
    /// degrees, over the root frame. <paramref name="significant"/> is false when there is too little (or too much)
    /// ink to judge, when the best angle is under 0.25°, or when the projection at the best angle is not at least
    /// 5 % sharper than at zero — the cases in which the library leaves the page alone.
    /// </summary>
    internal static double EstimateProjectionSkew(Image<Rgb24> image, float maxAngleDegrees, out bool significant)
    {
        significant = false;
        maxAngleDegrees = Math.Clamp(Math.Abs(maxAngleDegrees), 0.5f, 45f);

        int width = image.Width;
        int height = image.Height;
        int step = Math.Max(1, Math.Max(width, height) / MaxSampleDimension);
        int sampledWidth = (width + step - 1) / step;
        int sampledHeight = (height + step - 1) / step;

        var gray = new byte[sampledWidth * sampledHeight];
        var histogram = new int[256];
        image.ProcessPixelRows(rows =>
        {
            for (int sy = 0; sy < sampledHeight; sy++)
            {
                var row = rows.GetRowSpan(Math.Min(sy * step, height - 1));
                int offset = sy * sampledWidth;
                for (int sx = 0; sx < sampledWidth; sx++)
                {
                    var p = row[Math.Min(sx * step, width - 1)];
                    byte l = Luminance8(p.R, p.G, p.B);
                    gray[offset + sx] = l;
                    histogram[l]++;
                }
            }
        });

        int threshold = OtsuThreshold(histogram, gray.Length);

        var xs = new List<int>();
        var ys = new List<int>();
        for (int sy = 0; sy < sampledHeight; sy++)
        {
            int offset = sy * sampledWidth;
            for (int sx = 0; sx < sampledWidth; sx++)
            {
                if (gray[offset + sx] <= threshold)
                {
                    xs.Add(sx);
                    ys.Add(sy);
                }
            }
        }

        if (xs.Count < 64 || xs.Count > gray.Length / 2)
        {
            return 0;
        }

        int[] xArray = xs.ToArray();
        int[] yArray = ys.ToArray();
        int diagonal = (int)Math.Ceiling(Math.Sqrt(((double)sampledWidth * sampledWidth) + ((double)sampledHeight * sampledHeight)));
        var bins = new int[(2 * diagonal) + 3];

        double zeroScore = ProjectionScore(xArray, yArray, bins, diagonal, 0);
        double bestAngle = SearchBestAngle(xArray, yArray, bins, diagonal, -maxAngleDegrees, maxAngleDegrees, 1.0);
        bestAngle = SearchBestAngle(xArray, yArray, bins, diagonal, bestAngle - 1.0, bestAngle + 1.0, 0.1);
        double bestScore = ProjectionScore(xArray, yArray, bins, diagonal, bestAngle);

        significant = !(Math.Abs(bestAngle) < 0.25 || bestScore <= zeroScore * 1.05);
        return bestAngle;
    }

    /// <summary>BT.709 luminance in 0–255, rounded exactly as EasyImageSharp does.</summary>
    private static byte Luminance8(byte r, byte g, byte b)
    {
        float l = (r * 0.2126f) + (g * 0.7152f) + (b * 0.0722f);
        return (byte)Math.Clamp((int)(l + 0.5f), 0, 255);
    }

    /// <summary>Otsu's threshold over a 256-bin histogram, as EasyImageSharp computes it.</summary>
    private static int OtsuThreshold(int[] histogram, int total)
    {
        long sumAll = 0;
        for (int i = 0; i < 256; i++)
        {
            sumAll += (long)i * histogram[i];
        }

        long sumBackground = 0;
        long weightBackground = 0;
        double bestVariance = -1;
        int bestThreshold = 127;

        for (int t = 0; t < 256; t++)
        {
            weightBackground += histogram[t];
            if (weightBackground == 0)
            {
                continue;
            }

            long weightForeground = total - weightBackground;
            if (weightForeground == 0)
            {
                break;
            }

            sumBackground += (long)t * histogram[t];
            double meanBackground = (double)sumBackground / weightBackground;
            double meanForeground = (double)(sumAll - sumBackground) / weightForeground;
            double difference = meanBackground - meanForeground;
            double variance = (double)weightBackground * weightForeground * difference * difference;
            if (variance > bestVariance)
            {
                bestVariance = variance;
                bestThreshold = t;
            }
        }

        return bestThreshold;
    }

    private static double SearchBestAngle(int[] xs, int[] ys, int[] bins, int diagonal, double from, double to, double stepSize)
    {
        double bestAngle = 0;
        double bestScore = double.MinValue;
        for (double angle = from; angle <= to + 1e-9; angle += stepSize)
        {
            double score = ProjectionScore(xs, ys, bins, diagonal, angle);
            if (score > bestScore)
            {
                bestScore = score;
                bestAngle = angle;
            }
        }

        return bestAngle;
    }

    /// <summary>Sum of squared projection bin counts; maximal when text rows align with the projection axis.</summary>
    private static double ProjectionScore(int[] xs, int[] ys, int[] bins, int diagonal, double angleDegrees)
    {
        Array.Clear(bins);
        double radians = angleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        int offset = diagonal + 1;

        for (int i = 0; i < xs.Length; i++)
        {
            int bin = (int)Math.Round((ys[i] * cos) - (xs[i] * sin)) + offset;
            bins[Math.Clamp(bin, 0, bins.Length - 1)]++;
        }

        double score = 0;
        foreach (int count in bins)
        {
            score += (double)count * count;
        }

        return score;
    }
}
