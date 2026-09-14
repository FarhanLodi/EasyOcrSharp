using EasyOcrSharp.Structure.Engine.Models;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;

namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// PaddleOCR's <c>sorted_boxes</c> reading-order rule, extracted as a reusable pure helper: a stable
/// sort by each item's key point (the quad's first / top-left point) on (y, then x), followed by a
/// single adjacent-swap bubble pass that fixes pairs sitting on the same text line (key-point y within
/// <see cref="SameLineTolerance"/>) but out of left-to-right order. Used by the OCR engine for its
/// pipeline ordering and by the parity harness to emit lines in the exact Python convention.
/// </summary>
internal static class SortedBoxes
{
    /// <summary>
    /// Vertical reading tolerance (px) within which two boxes are treated as being on the same text line
    /// and therefore ordered left-to-right. PaddleOCR's <c>sorted_boxes</c> uses 10px.
    /// </summary>
    internal const double SameLineTolerance = 10.0;

    /// <summary>
    /// In-place <c>sorted_boxes</c>: a stable sort by the key point's (y, x) — Python sorts on the quad's
    /// first point — followed by a one-pass bubble that swaps an out-of-order neighbour pair that actually
    /// sits on the same text line.
    /// </summary>
    internal static void Sort<T>(List<T> items, Func<T, OcrPoint> keyPointOf)
    {
        // Stable primary sort: key y, then key x. List.Sort is not stable, so use OrderBy/ThenBy (stable)
        // and copy back — matching numpy's lexicographic sort used by PaddleOCR.
        var sorted = items
            .OrderBy(i => keyPointOf(i).Y)
            .ThenBy(i => keyPointOf(i).X)
            .ToList();
        for (int i = 0; i < items.Count; i++) items[i] = sorted[i];

        // Bubble pass: when two adjacent boxes are within the same-line tolerance vertically but the
        // earlier one is further right, swap them so the line reads left-to-right. The inner loop walks
        // back while the pair stays on the same line; the `else break` is load-bearing — the moment a
        // neighbour is on a *different* line we must stop, otherwise a later same-line pair could pull a
        // box across a line boundary and corrupt the vertical order.
        for (int i = 0; i < items.Count - 1; i++)
        {
            for (int j = i; j >= 0; j--)
            {
                var a = keyPointOf(items[j + 1]);
                var b = keyPointOf(items[j]);
                if (Math.Abs(a.Y - b.Y) < SameLineTolerance && a.X < b.X)
                {
                    (items[j], items[j + 1]) = (items[j + 1], items[j]);
                }
                else
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Returns the lines ordered by the exact Python <c>sorted_boxes</c> convention, keyed on each
    /// line's polygon's first point (its top-left corner for detector quads; lines without a polygon
    /// fall back to their bounding box's top-left corner). The input is not mutated.
    /// </summary>
    internal static List<OcrLine> SortLines(IEnumerable<OcrLine> lines)
    {
        var list = lines.ToList();
        if (list.Count > 1) Sort(list, KeyPoint);
        return list;
    }

    /// <summary>
    /// The <c>sorted_boxes</c> key point of a line: its polygon's first point (the top-left corner for
    /// detector quads), falling back to the bounding box's top-left corner for polygon-less lines.
    /// </summary>
    internal static OcrPoint KeyPoint(OcrLine line)
        => line.BoundingPolygon is { Count: > 0 } poly
            ? poly[0]
            : new OcrPoint(line.BoundingBox.MinX, line.BoundingBox.MinY);
}
