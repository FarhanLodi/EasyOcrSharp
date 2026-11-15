using EasyOcrSharp.Structure.Engine.Models;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// Merges recognized boxes that sit on the same visual text line into one <see cref="OcrLine"/> —
/// the behaviour <see cref="TextGrouping.Line"/> documents. Two boxes belong to the same line when
/// their vertical overlap is at least half the smaller box's height; a line's members are ordered
/// left-to-right and their texts joined with single spaces. The merged bounding box/polygon is the
/// axis-aligned union of the members.
/// <para>
/// On a skewed page (see <see cref="TextSkew"/>) the overlap and left-to-right tests run on each box
/// rotated into the deskewed frame, so a tilted row is not split into staircase fragments. Pages without a
/// significant skew are grouped exactly as before.
/// </para>
/// </summary>
internal static class LineGrouper
{
    /// <summary>
    /// The fraction of the smaller box's height two boxes must overlap vertically to be merged.
    /// </summary>
    private const double MinOverlapRatio = 0.5;

    /// <summary>
    /// Merges same-line boxes. <paramref name="lines"/> must already be in reading order (top-to-bottom,
    /// left-to-right) — the grouping walks it sequentially, extending the current line while each next box
    /// overlaps it vertically by at least <see cref="MinOverlapRatio"/> of the smaller height. When the page
    /// is significantly skewed the boxes are first re-ordered in the deskewed frame.
    /// </summary>
    public static List<OcrLine> Merge(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count <= 1) return lines.ToList();

        double skew = TextSkew.Estimate(lines);
        if (!TextSkew.IsSignificant(skew))
        {
            return MergeOrdered(lines.Select(l => (Line: l, Box: l.BoundingBox)).ToList());
        }

        var ordered = lines.ToList();
        SortedBoxes.SortDeskewed(ordered, SortedBoxes.KeyPoint, skew);
        return MergeOrdered(ordered.Select(l => (Line: l, Box: TextSkew.DeskewedBox(l, skew))).ToList());
    }

    private static List<OcrLine> MergeOrdered(List<(OcrLine Line, OcrBoundingBox Box)> items)
    {
        var result = new List<OcrLine>(items.Count);
        var current = new List<(OcrLine Line, OcrBoundingBox Box)> { items[0] };
        double curMinY = items[0].Box.MinY;
        double curMaxY = items[0].Box.MaxY;

        for (int i = 1; i < items.Count; i++)
        {
            var box = items[i].Box;
            double overlap = Math.Min(curMaxY, box.MaxY) - Math.Max(curMinY, box.MinY);
            double smallerHeight = Math.Min(curMaxY - curMinY, box.Height);
            if (smallerHeight > 0 && overlap >= MinOverlapRatio * smallerHeight)
            {
                current.Add(items[i]);
                curMinY = Math.Min(curMinY, box.MinY);
                curMaxY = Math.Max(curMaxY, box.MaxY);
            }
            else
            {
                result.Add(Flush(current));
                current = new List<(OcrLine Line, OcrBoundingBox Box)> { items[i] };
                curMinY = box.MinY;
                curMaxY = box.MaxY;
            }
        }
        result.Add(Flush(current));
        return result;
    }

    private static OcrLine Flush(List<(OcrLine Line, OcrBoundingBox Box)> members)
    {
        if (members.Count == 1) return members[0].Line;

        var ordered = members.OrderBy(m => m.Box.MinX).Select(m => m.Line).ToList();
        double minX = ordered.Min(l => l.BoundingBox.MinX);
        double minY = ordered.Min(l => l.BoundingBox.MinY);
        double maxX = ordered.Max(l => l.BoundingBox.MaxX);
        double maxY = ordered.Max(l => l.BoundingBox.MaxY);
        var poly = new[]
        {
            new OcrPoint(minX, minY), new OcrPoint(maxX, minY),
            new OcrPoint(maxX, maxY), new OcrPoint(minX, maxY),
        };

        return new OcrLine
        {
            Text = string.Join(" ", ordered.Select(l => l.Text)),
            Confidence = ordered.Average(l => l.Confidence),
            BoundingPolygon = poly,
            BoundingBox = new OcrBoundingBox(minX, minY, maxX, maxY),
            Words = ordered.SelectMany(l => l.Words).ToArray(),
        };
    }
}
