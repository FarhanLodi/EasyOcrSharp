using EasyOcrSharp.Models;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Pure geometry behind the opt-in CRAFT detection passes of <see cref="CraftDetector"/>: deciding when and
/// how to tile a large image (<see cref="DetectionOptions.TileLargeImages"/>), merging the per-tile word
/// boxes, and the magnification the small-text rescue (<see cref="DetectionOptions.MinTextHeight"/>) re-detects
/// at. Kept free of ONNX so it is unit-testable.
/// </summary>
/// <remarks>
/// CRAFT emits word-level boxes (connected components of the character/affinity heatmaps) and the line
/// grouping runs afterwards over the whole page, so tiles only have to agree on <i>words</i>: a word longer
/// than a seam's overlap is rare, which is why a plain grid works here where a line-level detector needs
/// strips along the long axis.
/// </remarks>
internal static class DetectionTiling
{
    /// <summary>Overlap between neighbouring tiles, in source pixels.</summary>
    internal const int TileOverlap = 384;

    /// <summary>Tiling applies when the canvas cap would shrink the detector input below this fraction.</summary>
    internal const double TileScaleTrigger = 0.75;

    /// <summary>
    /// How close (tile pixels) a box edge must come to a tile's cut edge to count as cut by it. CRAFT boxes are
    /// fitted on the half-resolution heatmap and clamped to the tile, so a cut box ends within a few pixels.
    /// </summary>
    internal const double CutEdgeTolerance = 4;

    /// <summary>Axis-aligned IoU above which two boxes from different tiles are the same detection.</summary>
    internal const double DuplicateIou = 0.5;

    /// <summary>Share of a box's area inside a box from another tile above which it is a duplicate (or a cut piece of it).</summary>
    internal const double DuplicateContainment = 0.6;

    /// <summary>Line height (detector px) the small-text rescue aims for.</summary>
    internal const double TargetTextHeight = 24;

    /// <summary>Largest magnification the small-text rescue applies on top of the normal pass.</summary>
    internal const double MaxUpscale = 3;

    /// <summary>Detector-input long side below which an empty page is retried magnified.</summary>
    internal const int EmptyRetryMaxSide = 1500;

    /// <summary>Magnification an empty small page is retried at.</summary>
    internal const double EmptyRetryUpscale = 2;

    /// <summary>A rescue pass must magnify by more than this to be worth a second detector run.</summary>
    internal const double MinUsefulUpscale = 1.05;

    /// <summary>One tile of a detection grid, in source pixels, with the sides that are cuts (not page edges).</summary>
    internal readonly record struct Tile(int X, int Y, int Width, int Height, bool CutLeft, bool CutTop, bool CutRight, bool CutBottom);

    /// <summary>A box found in one tile, already shifted into page coordinates.</summary>
    internal readonly record struct TileCandidate(OcrPoint[] Polygon, int TileIndex, bool TouchesCut);

    /// <summary>
    /// The source→detector scale <see cref="ImageProcessing.PreprocessForCraft"/> uses for an image whose long
    /// side is <paramref name="longSide"/>: <c>min(magRatio, canvasSize / longSide)</c>, with the same floors.
    /// </summary>
    internal static double DetectionScale(int longSide, int canvasSize, double magRatio)
    {
        canvasSize = Math.Max(32, canvasSize);
        magRatio = Math.Max(1e-3, magRatio);
        if (longSide <= 0) return magRatio;
        double target = magRatio * longSide;
        if (target > canvasSize) target = canvasSize;
        return target / longSide;
    }

    /// <summary>
    /// True when the canvas cap would shrink a <paramref name="width"/>×<paramref name="height"/> image below
    /// <see cref="TileScaleTrigger"/> of the magnification <paramref name="magRatio"/> asks for.
    /// <paramref name="tileLength"/> is then the longest source span a tile may have and still be detected at
    /// full <paramref name="magRatio"/> (<c>canvasSize / magRatio</c>).
    /// </summary>
    internal static bool ShouldTile(int width, int height, int canvasSize, double magRatio, out int tileLength)
    {
        tileLength = 0;
        canvasSize = Math.Max(32, canvasSize);
        magRatio = Math.Max(1e-3, magRatio);
        int longSide = Math.Max(width, height);
        if (longSide <= 0) return false;

        double uncapped = magRatio * longSide;
        if (uncapped <= canvasSize || canvasSize / uncapped >= TileScaleTrigger) return false;

        tileLength = (int)Math.Floor(canvasSize / magRatio);
        return tileLength > TileOverlap * 2;
    }

    /// <summary>
    /// Plans spans of at most <paramref name="tileLength"/> covering [0, <paramref name="length"/>) with at
    /// least <paramref name="overlap"/> shared pixels; the last span is aligned to the far end.
    /// </summary>
    internal static List<(int Start, int Length)> PlanTiles(int length, int tileLength, int overlap)
    {
        var tiles = new List<(int, int)>();
        if (length <= tileLength)
        {
            tiles.Add((0, length));
            return tiles;
        }

        int step = Math.Max(1, tileLength - overlap);
        int count = (int)Math.Ceiling((double)(length - tileLength) / step) + 1;
        for (int i = 0; i < count; i++)
        {
            int start = Math.Min(i * step, length - tileLength);
            tiles.Add((start, tileLength));
        }
        return tiles;
    }

    /// <summary>
    /// Plans the tile grid for an image: strips along the long axis when the short side fits a tile (tall
    /// receipts, wide banners), a full grid when it does not. Tiles are listed row by row.
    /// </summary>
    internal static List<Tile> PlanGrid(int width, int height, int tileLength, int overlap)
    {
        var columns = PlanTiles(width, tileLength, overlap);
        var rows = PlanTiles(height, tileLength, overlap);
        var tiles = new List<Tile>(columns.Count * rows.Count);
        foreach (var (y, h) in rows)
        {
            foreach (var (x, w) in columns)
            {
                tiles.Add(new Tile(x, y, w, h,
                    CutLeft: x > 0,
                    CutTop: y > 0,
                    CutRight: x + w < width,
                    CutBottom: y + h < height));
            }
        }
        return tiles;
    }

    /// <summary>
    /// True when <paramref name="boxInTile"/> (tile coordinates) reaches one of the tile's cut edges, i.e.
    /// the tile may hold only part of it.
    /// </summary>
    internal static bool TouchesCut(OcrBoundingBox boxInTile, Tile tile, double tolerance = CutEdgeTolerance)
        => (tile.CutLeft && boxInTile.MinX <= tolerance)
        || (tile.CutTop && boxInTile.MinY <= tolerance)
        || (tile.CutRight && boxInTile.MaxX >= tile.Width - tolerance)
        || (tile.CutBottom && boxInTile.MaxY >= tile.Height - tolerance);

    /// <summary>Shifts a polygon from tile coordinates into page coordinates.</summary>
    internal static OcrPoint[] Offset(IReadOnlyList<OcrPoint> polygon, int dx, int dy)
    {
        var shifted = new OcrPoint[polygon.Count];
        for (int i = 0; i < shifted.Length; i++)
        {
            shifted[i] = new OcrPoint(polygon[i].X + dx, polygon[i].Y + dy);
        }
        return shifted;
    }

    /// <summary>
    /// Merges the boxes gathered from overlapping tiles. Candidates clear of their tile's cut edges are
    /// considered first, then larger boxes. Two cut pieces of one box that is larger than the overlap — both
    /// touching a cut and overlapping each other by half their extent in both axes — are joined into one
    /// minimum-area rectangle. Otherwise a candidate is dropped when a kept box from a <b>different</b> tile
    /// overlaps it with axis-aligned IoU above <see cref="DuplicateIou"/> or contains at least
    /// <see cref="DuplicateContainment"/> of it (the same word seen twice in an overlap, or the cut piece of a
    /// word the neighbouring tile saw whole). Boxes from the same tile are never suppressed here: overlaps inside one pass
    /// are left to the normal pipeline (<see cref="BoxNms"/>), exactly as without tiling. The result is
    /// ordered top-to-bottom, then left-to-right.
    /// </summary>
    internal static List<OcrPoint[]> Merge(IReadOnlyList<TileCandidate> candidates)
    {
        var ordered = candidates
            .Select((c, i) => (Candidate: c, Box: OcrBoundingBox.FromPoints(c.Polygon), Index: i))
            .OrderBy(c => c.Candidate.TouchesCut)
            .ThenByDescending(c => Area(c.Box))
            .ThenBy(c => c.Index)
            .ToList();

        var kept = new List<(OcrPoint[] Polygon, OcrBoundingBox Box, int Tile, bool TouchesCut)>(ordered.Count);
        foreach (var (candidate, box, _) in ordered)
        {
            bool absorbed = false;
            for (int k = 0; k < kept.Count; k++)
            {
                var other = kept[k];
                if (other.Tile == candidate.TileIndex) continue;

                double inter = Intersection(box, other.Box);
                if (inter <= 0) continue;

                // Two cut pieces: neither saw the whole box, so keep their union rather than either piece.
                // (Joining a piece with a whole copy that merely sits near another cut yields that whole copy.)
                if (candidate.TouchesCut && other.TouchesCut && IsSeamSplit(box, other.Box))
                {
                    var joined = Join(candidate.Polygon, other.Polygon);
                    kept[k] = (joined, OcrBoundingBox.FromPoints(joined), other.Tile, true);
                    absorbed = true;
                    break;
                }

                double area = Area(box);
                double union = area + Area(other.Box) - inter;
                if ((union > 0 && inter / union > DuplicateIou) || (area > 0 && inter / area >= DuplicateContainment))
                {
                    absorbed = true;
                    break;
                }
            }

            if (!absorbed) kept.Add((candidate.Polygon, box, candidate.TileIndex, candidate.TouchesCut));
        }

        return kept
            .OrderBy(k => k.Box.MinY)
            .ThenBy(k => k.Box.MinX)
            .Select(k => k.Polygon)
            .ToList();
    }

    /// <summary>
    /// Median of each polygon's shorter side — the text height for horizontal words — in the polygons'
    /// coordinates; 0 when there are none.
    /// </summary>
    internal static double MedianShortSide(IReadOnlyList<OcrPoint[]> polygons)
    {
        var sides = new List<double>(polygons.Count);
        foreach (var poly in polygons)
        {
            if (poly.Length < 3) continue;
            double s01 = Distance(poly[0], poly[1]);
            double s12 = Distance(poly[1], poly[2]);
            sides.Add(Math.Min(s01, s12));
        }
        if (sides.Count == 0) return 0;

        sides.Sort();
        int mid = sides.Count / 2;
        return sides.Count % 2 == 1 ? sides[mid] : (sides[mid - 1] + sides[mid]) / 2;
    }

    /// <summary>
    /// The magnification the small-text rescue re-detects at, or 0 when no second pass is warranted.
    /// <paramref name="medianShortSide"/> is in source pixels; it is compared against
    /// <paramref name="minTextHeight"/> at the detector's input scale, and the rescue aims the typical line at
    /// <c>max(<see cref="TargetTextHeight"/>, minTextHeight)</c> detector pixels, magnifying by at most
    /// <see cref="MaxUpscale"/> and never past <paramref name="canvasSize"/>.
    /// </summary>
    internal static double RescueMagnification(double medianShortSide, int minTextHeight, int longSide, int canvasSize, double magRatio)
    {
        if (minTextHeight <= 0 || medianShortSide <= 0 || longSide <= 0) return 0;

        double scale = DetectionScale(longSide, canvasSize, magRatio);
        double detected = medianShortSide * scale;
        if (detected >= minTextHeight) return 0;

        double target = Math.Max(TargetTextHeight, minTextHeight);
        double upscale = Math.Min(target / detected, MaxUpscale);
        return Capped(scale, scale * upscale, longSide, canvasSize);
    }

    /// <summary>
    /// The magnification an image on which the normal pass found nothing is retried at
    /// (<see cref="EmptyRetryUpscale"/>× when its detector input is under <see cref="EmptyRetryMaxSide"/> px),
    /// or 0 when it is not retried.
    /// </summary>
    internal static double EmptyRetryMagnification(int longSide, int canvasSize, double magRatio)
    {
        if (longSide <= 0) return 0;
        double scale = DetectionScale(longSide, canvasSize, magRatio);
        if (longSide * scale >= EmptyRetryMaxSide) return 0;
        return Capped(scale, scale * EmptyRetryUpscale, longSide, canvasSize);
    }

    /// <summary>Caps <paramref name="wanted"/> at the canvas and returns it, or 0 when it barely beats <paramref name="current"/>.</summary>
    private static double Capped(double current, double wanted, int longSide, int canvasSize)
    {
        double cap = (double)Math.Max(32, canvasSize) / longSide;
        double next = Math.Min(wanted, cap);
        return next / current > MinUsefulUpscale ? next : 0;
    }

    /// <summary>True when two cut pieces overlap by at least half of the smaller extent along both axes.</summary>
    private static bool IsSeamSplit(OcrBoundingBox a, OcrBoundingBox b)
    {
        double ox = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
        double oy = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
        double minW = Math.Min(a.Width, b.Width);
        double minH = Math.Min(a.Height, b.Height);
        return minW > 0 && minH > 0 && ox / minW >= 0.5 && oy / minH >= 0.5;
    }

    /// <summary>The minimum-area rectangle around two polygons.</summary>
    private static OcrPoint[] Join(OcrPoint[] a, OcrPoint[] b)
    {
        var points = new OcrPoint[a.Length + b.Length];
        a.CopyTo(points, 0);
        b.CopyTo(points, a.Length);
        return MinAreaRect.Compute(points);
    }

    private static double Area(OcrBoundingBox box) => box.Width * box.Height;

    private static double Intersection(OcrBoundingBox a, OcrBoundingBox b)
    {
        double ix = Math.Max(0, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX));
        double iy = Math.Max(0, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));
        return ix * iy;
    }

    private static double Distance(OcrPoint a, OcrPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
