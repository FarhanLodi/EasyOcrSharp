using EasyImageSharp;
using EasyOcrSharp.Structure.Engine.Models;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Engine.Detection;

/// <summary>
/// Pure geometry behind the opt-in detection passes of <see cref="DbTextDetector"/>: deciding when and how
/// to tile a large image (<see cref="DetectionOptions.TileLargeImages"/>), merging per-tile quads, and
/// the small-text upscale factor (<see cref="DetectionOptions.MinTextHeight"/>). Kept free of ONNX so it
/// is unit-testable.
/// </summary>
internal static class DetectionTiling
{
    /// <summary>Overlap between consecutive tiles, in source pixels.</summary>
    internal const int TileOverlap = 384;

    /// <summary>Tiling applies when the side cap would shrink the input below this fraction.</summary>
    internal const double TileScaleTrigger = 0.75;

    /// <summary>Line height (px) the small-text rescue aims for.</summary>
    internal const double TargetTextHeight = 24;

    /// <summary>Largest upscale the small-text rescue applies.</summary>
    internal const double MaxUpscale = 3;

    /// <summary>Longest side below which an empty page is retried at 2×.</summary>
    internal const int EmptyRetryMaxSide = 1500;

    /// <summary>
    /// True when <paramref name="options"/>' <see cref="DetectionOptions.MaxSideLimit"/> would scale the
    /// detector input below <see cref="TileScaleTrigger"/> of the size the limit policy alone asks for.
    /// <paramref name="tileLength"/> is then the longest source-pixel span whose resize still fits the cap.
    /// </summary>
    internal static bool ShouldTile(int width, int height, DetectionOptions options, out int tileLength)
    {
        tileLength = 0;
        int maxSideLimit = options.MaxSideLimit > 0 ? options.MaxSideLimit : 4000;
        int limit = options.LimitSideLen > 0 ? options.LimitSideLen : 64;
        int longSide = Math.Max(width, height);
        if (longSide <= 0)
        {
            return false;
        }

        // The pre-cap policy scale (same rules as ComputeResize).
        double policy = 1.0;
        if (options.LimitTypeMax)
        {
            if (longSide > limit) policy = (double)limit / longSide;
        }
        else
        {
            int shortSide = Math.Min(width, height);
            if (shortSide < limit) policy = (double)limit / shortSide;
        }

        double uncapped = longSide * policy;
        if (uncapped <= maxSideLimit || maxSideLimit / uncapped >= TileScaleTrigger)
        {
            return false;
        }

        tileLength = (int)Math.Floor(maxSideLimit / policy);
        return tileLength > TileOverlap * 2;
    }

    /// <summary>
    /// Plans tiles of at most <paramref name="tileLength"/> covering [0, <paramref name="longSide"/>) with
    /// at least <paramref name="overlap"/> shared pixels; the last tile is aligned to the far end.
    /// </summary>
    internal static List<(int Start, int Length)> PlanTiles(int longSide, int tileLength, int overlap)
    {
        var tiles = new List<(int, int)>();
        if (longSide <= tileLength)
        {
            tiles.Add((0, longSide));
            return tiles;
        }

        int step = Math.Max(1, tileLength - overlap);
        int count = (int)Math.Ceiling((double)(longSide - tileLength) / step) + 1;
        for (int i = 0; i < count; i++)
        {
            int start = Math.Min(i * step, longSide - tileLength);
            tiles.Add((start, tileLength));
        }
        return tiles;
    }

    /// <summary>
    /// Merges quads gathered from overlapping tiles: candidates clear of their tile's cut edge are
    /// considered first, then larger boxes; a candidate is dropped when its axis-aligned IoU with an
    /// already-kept quad exceeds <paramref name="iouThreshold"/>.
    /// </summary>
    internal static List<TextQuad> Merge(IReadOnlyList<(TextQuad Quad, bool TouchesCut)> candidates, double iouThreshold)
    {
        var ordered = candidates
            .Select(c => (c.Quad, c.TouchesCut, Box: c.Quad.ToAxisAlignedBounds()))
            .OrderBy(c => c.TouchesCut)
            .ThenByDescending(c => c.Box.Width * c.Box.Height)
            .ToList();

        var kept = new List<(TextQuad Quad, RectangleF Box)>(ordered.Count);
        foreach (var c in ordered)
        {
            bool duplicate = false;
            foreach (var k in kept)
            {
                if (Iou(c.Box, k.Box) > iouThreshold)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
            {
                kept.Add((c.Quad, c.Box));
            }
        }
        return kept.Select(k => k.Quad).ToList();
    }

    /// <summary>Axis-aligned intersection-over-union of two rectangles.</summary>
    internal static double Iou(RectangleF a, RectangleF b)
    {
        double ix = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        double iy = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        double inter = ix * iy;
        double union = (double)a.Width * a.Height + (double)b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>Median of each quad's shorter side (the line height for horizontal text); 0 when empty.</summary>
    internal static double MedianShortSide(IReadOnlyList<TextQuad> quads)
    {
        if (quads.Count == 0)
        {
            return 0;
        }

        var sides = new double[quads.Count];
        for (int i = 0; i < quads.Count; i++)
        {
            var q = quads[i];
            double s01 = Distance(q.P0, q.P1);
            double s12 = Distance(q.P1, q.P2);
            sides[i] = Math.Min(s01, s12);
        }
        Array.Sort(sides);
        int mid = sides.Length / 2;
        return sides.Length % 2 == 1 ? sides[mid] : (sides[mid - 1] + sides[mid]) / 2;
    }

    /// <summary>
    /// The small-text upscale factor: <c>min(target / median, 3, maxSideLimit / longSide)</c> with
    /// <c>target = max(24, minTextHeight)</c>, or 1 when no rescale is warranted.
    /// </summary>
    internal static double UpscaleFactor(double medianShortSide, int minTextHeight, int longSide, int maxSideLimit)
    {
        if (minTextHeight <= 0 || medianShortSide <= 0 || medianShortSide >= minTextHeight || longSide <= 0)
        {
            return 1;
        }

        double target = Math.Max(TargetTextHeight, minTextHeight);
        double f = Math.Min(Math.Min(target / medianShortSide, MaxUpscale), (double)(maxSideLimit > 0 ? maxSideLimit : 4000) / longSide);
        return f > 1.05 ? f : 1;
    }

    private static double Distance(PointF a, PointF b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
