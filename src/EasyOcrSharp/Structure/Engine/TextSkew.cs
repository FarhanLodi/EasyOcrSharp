using EasyOcrSharp.Structure.Engine.Models;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// Estimates the dominant text-line skew of a page from its detected quads, and rotates reading-order
/// sort keys into the deskewed frame. Pure geometry: nothing here touches pixels, and the emitted lines
/// always keep their original coordinates — only the keys the sorters/groupers compare are rotated.
/// <para>
/// The estimate is deliberately conservative. Only clearly elongated, roughly horizontal quads
/// (long/short ≥ <see cref="MinElongation"/>, |angle| ≤ 45°) vote; the result is the length-weighted
/// median of their long-edge angles, and it is reported only when at least <see cref="MinQuads"/> quads
/// agree (median absolute deviation below <see cref="MaxMadDegrees"/>). Anything else reports 0, so a
/// sparse or mixed page keeps the exact unrotated ordering.
/// </para>
/// </summary>
internal static class TextSkew
{
    /// <summary>
    /// Smallest |skew| (degrees) at which sort keys are rotated. Below it the callers use the exact
    /// unrotated keys, which keeps <see cref="SortedBoxes"/> byte-identical to Python's <c>sorted_boxes</c>.
    /// </summary>
    internal const double MinCorrectionDegrees = 0.5;

    /// <summary>Minimum number of agreeing elongated quads before a skew is reported.</summary>
    internal const int MinQuads = 5;

    /// <summary>Minimum long-edge / short-edge ratio for a quad to vote.</summary>
    internal const double MinElongation = 3.0;

    /// <summary>The votes' median absolute deviation (degrees) must stay below this for a skew to be reported.</summary>
    internal const double MaxMadDegrees = 2.0;

    /// <summary>
    /// Estimates the clockwise (image coordinates, y down) skew of the text lines in degrees, or 0 when
    /// there is no confident estimate.
    /// </summary>
    internal static double Estimate(IEnumerable<IReadOnlyList<OcrPoint>> polygons)
    {
        var votes = new List<(double Angle, double Weight)>();
        foreach (var poly in polygons)
        {
            if (poly is not { Count: >= 4 }) continue;

            // Average opposite edges so a slightly non-rectangular quad still gives a stable direction.
            double hx = ((poly[1].X - poly[0].X) + (poly[2].X - poly[3].X)) / 2.0;
            double hy = ((poly[1].Y - poly[0].Y) + (poly[2].Y - poly[3].Y)) / 2.0;
            double vx = ((poly[3].X - poly[0].X) + (poly[2].X - poly[1].X)) / 2.0;
            double vy = ((poly[3].Y - poly[0].Y) + (poly[2].Y - poly[1].Y)) / 2.0;
            double hLen = Math.Sqrt((hx * hx) + (hy * hy));
            double vLen = Math.Sqrt((vx * vx) + (vy * vy));

            double longLen = Math.Max(hLen, vLen);
            double shortLen = Math.Min(hLen, vLen);
            if (shortLen <= 0 || longLen / shortLen < MinElongation) continue;

            double angle = hLen >= vLen
                ? Math.Atan2(hy, hx) * 180.0 / Math.PI
                : Math.Atan2(vy, vx) * 180.0 / Math.PI;
            angle = Fold(angle);

            // Vertical text lines (long edge near ±90°) say nothing about the tilt of horizontal rows.
            if (Math.Abs(angle) > 45.0) continue;
            votes.Add((angle, longLen));
        }

        if (votes.Count < MinQuads) return 0;

        double median = WeightedMedian(votes);
        var deviations = votes.Select(v => Math.Abs(v.Angle - median)).ToArray();
        Array.Sort(deviations);
        int mid = deviations.Length / 2;
        double mad = (deviations.Length & 1) == 1 ? deviations[mid] : (deviations[mid - 1] + deviations[mid]) / 2.0;
        return mad < MaxMadDegrees ? median : 0;
    }

    /// <summary>
    /// Estimates the skew of recognized lines (see <see cref="Estimate(IEnumerable{IReadOnlyList{OcrPoint}})"/>).
    /// </summary>
    internal static double Estimate(IEnumerable<OcrLine> lines)
        => Estimate(lines.Select(l => l.BoundingPolygon));

    /// <summary>True when <paramref name="skewDegrees"/> is large enough that sort keys should be rotated.</summary>
    internal static bool IsSignificant(double skewDegrees) => Math.Abs(skewDegrees) >= MinCorrectionDegrees;

    /// <summary>
    /// Rotates a point by −<paramref name="skewDegrees"/> about the origin, so a line that runs at the
    /// estimated skew becomes horizontal. Ordering only needs relative positions, so the pivot is irrelevant.
    /// </summary>
    internal static OcrPoint Deskew(OcrPoint p, double skewDegrees)
    {
        double rad = skewDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        return new OcrPoint((p.X * cos) + (p.Y * sin), (-p.X * sin) + (p.Y * cos));
    }

    /// <summary>
    /// The axis-aligned box of a line's polygon (or of its bounding box's four corners when it has no
    /// polygon) after rotating it into the deskewed frame.
    /// </summary>
    internal static OcrBoundingBox DeskewedBox(OcrLine line, double skewDegrees)
        => OcrBoundingBox.FromPoints(CornersOf(line).Select(p => Deskew(p, skewDegrees)));

    /// <summary>A line's polygon, or its bounding box's four corners when it has none.</summary>
    internal static IReadOnlyList<OcrPoint> CornersOf(OcrLine line)
    {
        if (line.BoundingPolygon is { Count: > 0 } poly) return poly;
        var b = line.BoundingBox;
        return new[]
        {
            new OcrPoint(b.MinX, b.MinY), new OcrPoint(b.MaxX, b.MinY),
            new OcrPoint(b.MaxX, b.MaxY), new OcrPoint(b.MinX, b.MaxY),
        };
    }

    private static double Fold(double angle)
    {
        while (angle > 90.0) angle -= 180.0;
        while (angle <= -90.0) angle += 180.0;
        return angle;
    }

    private static double WeightedMedian(List<(double Angle, double Weight)> votes)
    {
        var sorted = votes.OrderBy(v => v.Angle).ToList();
        double half = sorted.Sum(v => v.Weight) / 2.0;
        double cumulative = 0;
        foreach (var v in sorted)
        {
            cumulative += v.Weight;
            if (cumulative >= half) return v.Angle;
        }
        return sorted[^1].Angle;
    }
}
