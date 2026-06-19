using EasyOcrSharp.Models;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Non-maximum suppression over detected region polygons. CRAFT can emit overlapping connected
/// components (and grouping can re-merge them differently), which otherwise lead to the same text being
/// recognized twice. This greedily keeps the largest box and drops any later box whose axis-aligned IoU
/// with an already-kept box exceeds the threshold. Axis-aligned IoU is an approximation for rotated
/// quads but is the right, cheap signal for the duplicate-detection case it targets.
/// </summary>
internal static class BoxNms
{
    public static IReadOnlyList<OcrPoint[]> Reduce(IReadOnlyList<OcrPoint[]> polygons, double iouThreshold)
    {
        if (iouThreshold <= 0 || polygons.Count < 2) return polygons;

        var items = new (OcrPoint[] Poly, OcrBoundingBox Box, double Area)[polygons.Count];
        for (int i = 0; i < polygons.Count; i++)
        {
            var box = OcrBoundingBox.FromPoints(polygons[i]);
            items[i] = (polygons[i], box, box.Width * box.Height);
        }
        // Largest first, so the most complete box survives and tighter duplicates fall away.
        Array.Sort(items, (a, b) => b.Area.CompareTo(a.Area));

        var kept = new List<(OcrPoint[] Poly, OcrBoundingBox Box)>(polygons.Count);
        foreach (var item in items)
        {
            bool duplicate = false;
            foreach (var k in kept)
            {
                if (Iou(item.Box, k.Box) > iouThreshold) { duplicate = true; break; }
            }
            if (!duplicate) kept.Add((item.Poly, item.Box));
        }

        return kept.Count == polygons.Count ? polygons : kept.Select(k => k.Poly).ToArray();
    }

    private static double Iou(OcrBoundingBox a, OcrBoundingBox b)
    {
        double ix = Math.Max(0, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX));
        double iy = Math.Max(0, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));
        double inter = ix * iy;
        double union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }
}
