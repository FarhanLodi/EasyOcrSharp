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
    /// overlaps it vertically by at least <see cref="MinOverlapRatio"/> of the smaller height.
    /// </summary>
    public static List<OcrLine> Merge(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count <= 1) return lines.ToList();

        var result = new List<OcrLine>(lines.Count);
        var current = new List<OcrLine> { lines[0] };
        double curMinY = lines[0].BoundingBox.MinY;
        double curMaxY = lines[0].BoundingBox.MaxY;

        for (int i = 1; i < lines.Count; i++)
        {
            var box = lines[i].BoundingBox;
            double overlap = Math.Min(curMaxY, box.MaxY) - Math.Max(curMinY, box.MinY);
            double smallerHeight = Math.Min(curMaxY - curMinY, box.Height);
            if (smallerHeight > 0 && overlap >= MinOverlapRatio * smallerHeight)
            {
                current.Add(lines[i]);
                curMinY = Math.Min(curMinY, box.MinY);
                curMaxY = Math.Max(curMaxY, box.MaxY);
            }
            else
            {
                result.Add(Flush(current));
                current = new List<OcrLine> { lines[i] };
                curMinY = box.MinY;
                curMaxY = box.MaxY;
            }
        }
        result.Add(Flush(current));
        return result;
    }

    private static OcrLine Flush(List<OcrLine> members)
    {
        if (members.Count == 1) return members[0];

        var ordered = members.OrderBy(l => l.BoundingBox.MinX).ToList();
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
        };
    }
}
