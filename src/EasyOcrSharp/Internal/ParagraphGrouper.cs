using EasyOcrSharp.Models;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Merges recognized text lines into paragraph blocks by vertical proximity and horizontal
/// overlap/closeness — a pragmatic port of EasyOCR's <c>get_paragraph</c>. Lines that sit close
/// together vertically and either overlap horizontally or are within a line-height of each other are
/// concatenated (newline-separated) into one result whose bounding box is the union of the merged
/// lines. The vertical/horizontal join distances are exposed via
/// <see cref="GroupingOptions.ParagraphYThreshold"/> / <see cref="GroupingOptions.ParagraphXThreshold"/>.
/// </summary>
internal static class ParagraphGrouper
{
    public static List<OcrLine> Merge(IReadOnlyList<OcrLine> lines, GroupingOptions? grouping = null)
    {
        grouping ??= GroupingOptions.Default;
        double xThreshold = grouping.ParagraphXThreshold;
        double yThreshold = grouping.ParagraphYThreshold;

        var remaining = lines.Where(l => !string.IsNullOrEmpty(l.Text)).ToList();
        remaining.Sort((a, b) => a.BoundingBox.MinY.CompareTo(b.BoundingBox.MinY));

        var paragraphs = new List<List<OcrLine>>();
        foreach (var line in remaining)
        {
            var placed = false;
            foreach (var para in paragraphs)
            {
                var last = para[^1];
                double lineHeight = Math.Max(line.BoundingBox.Height, last.BoundingBox.Height);
                double verticalGap = line.BoundingBox.MinY - last.BoundingBox.MaxY;

                // Same block if the next line starts within ~y_ths line-heights below the previous one
                // and their horizontal spans either overlap or sit within ~x_ths line-heights.
                if (verticalGap <= lineHeight * yThreshold && verticalGap >= -lineHeight
                    && HorizontalClose(last.BoundingBox, line.BoundingBox, lineHeight * xThreshold))
                {
                    para.Add(line);
                    placed = true;
                    break;
                }
            }
            if (!placed) paragraphs.Add(new List<OcrLine> { line });
        }

        var result = new List<OcrLine>(paragraphs.Count);
        foreach (var para in paragraphs)
        {
            if (para.Count == 1)
            {
                result.Add(para[0]);
                continue;
            }

            var ordered = para.OrderBy(l => l.BoundingBox.MinY).ThenBy(l => l.BoundingBox.MinX).ToList();
            var text = string.Join("\n", ordered.Select(l => l.Text));
            double minX = ordered.Min(l => l.BoundingBox.MinX);
            double minY = ordered.Min(l => l.BoundingBox.MinY);
            double maxX = ordered.Max(l => l.BoundingBox.MaxX);
            double maxY = ordered.Max(l => l.BoundingBox.MaxY);
            var poly = new[]
            {
                new OcrPoint(minX, minY), new OcrPoint(maxX, minY),
                new OcrPoint(maxX, maxY), new OcrPoint(minX, maxY),
            };

            result.Add(new OcrLine
            {
                Text = text,
                Confidence = ordered.Average(l => l.Confidence),
                BoundingPolygon = poly,
                BoundingBox = new OcrBoundingBox(minX, minY, maxX, maxY),
            });
        }
        return result;
    }

    private static bool HorizontalClose(OcrBoundingBox a, OcrBoundingBox b, double maxGap)
    {
        double overlap = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
        if (overlap > 0.2 * Math.Min(a.Width, b.Width)) return true;
        // No overlap: allow joining if the horizontal gap between the spans is within maxGap.
        double gap = Math.Max(a.MinX, b.MinX) - Math.Min(a.MaxX, b.MaxX);
        return gap <= maxGap;
    }
}
