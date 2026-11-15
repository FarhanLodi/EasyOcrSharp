using EasyOcrSharp.Models;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Applies a point transform to every coordinate a recognized line carries — its polygon, its axis-aligned
/// box, and the polygons and boxes of its <see cref="OcrLine.Words"/> and <see cref="OcrLine.Characters"/> —
/// so a line read on a working image (a deskewed canvas, a square-pixel copy, a region crop) can be reported
/// on the caller's image without its word and character geometry being left behind in the working frame.
/// </summary>
internal static class LineGeometry
{
    /// <summary>
    /// Maps every line through <paramref name="map"/>. Boxes are recomputed from the mapped polygons (the
    /// transform need not keep rectangles axis-aligned); a line, word or character without a polygon has its
    /// box corners mapped instead. Geometry-less entries (the newline characters paragraph merging inserts)
    /// stay as they are. Returns <paramref name="lines"/> itself when it is empty.
    /// </summary>
    internal static IReadOnlyList<OcrLine> Transform(IReadOnlyList<OcrLine> lines, Func<OcrPoint, OcrPoint> map)
    {
        if (lines.Count == 0) return lines;
        var mapped = new List<OcrLine>(lines.Count);
        foreach (var line in lines)
        {
            mapped.Add(Transform(line, map));
        }
        return mapped;
    }

    /// <summary>Maps one line (see <see cref="Transform(IReadOnlyList{OcrLine}, Func{OcrPoint, OcrPoint})"/>).</summary>
    internal static OcrLine Transform(OcrLine line, Func<OcrPoint, OcrPoint> map)
    {
        var (polygon, box) = Map(line.BoundingPolygon, line.BoundingBox, map);
        return line with
        {
            BoundingPolygon = polygon,
            BoundingBox = box,
            Words = Transform(line.Words, map),
            Characters = Transform(line.Characters, map),
        };
    }

    /// <summary>Maps word geometry; returns <paramref name="words"/> itself when it is empty.</summary>
    internal static IReadOnlyList<OcrWord> Transform(IReadOnlyList<OcrWord> words, Func<OcrPoint, OcrPoint> map)
    {
        if (words.Count == 0) return words;
        var mapped = new OcrWord[words.Count];
        for (int i = 0; i < mapped.Length; i++)
        {
            var word = words[i];
            if (HasNoGeometry(word.BoundingPolygon, word.BoundingBox))
            {
                mapped[i] = word;
                continue;
            }
            var (polygon, box) = Map(word.BoundingPolygon, word.BoundingBox, map);
            mapped[i] = word with { BoundingPolygon = polygon, BoundingBox = box };
        }
        return mapped;
    }

    /// <summary>Maps character geometry; returns <paramref name="characters"/> itself when it is empty.</summary>
    internal static IReadOnlyList<OcrChar> Transform(IReadOnlyList<OcrChar> characters, Func<OcrPoint, OcrPoint> map)
    {
        if (characters.Count == 0) return characters;
        var mapped = new OcrChar[characters.Count];
        for (int i = 0; i < mapped.Length; i++)
        {
            var character = characters[i];
            if (HasNoGeometry(character.BoundingPolygon, character.BoundingBox))
            {
                mapped[i] = character;
                continue;
            }
            var (polygon, box) = Map(character.BoundingPolygon, character.BoundingBox, map);
            mapped[i] = character with { BoundingPolygon = polygon, BoundingBox = box };
        }
        return mapped;
    }

    /// <summary>True for an entry that occupies no pixels (no polygon, empty box), which is kept as the same instance.</summary>
    private static bool HasNoGeometry(IReadOnlyList<OcrPoint> polygon, OcrBoundingBox box)
        => polygon.Count == 0 && box == OcrBoundingBox.Empty;

    private static (IReadOnlyList<OcrPoint> Polygon, OcrBoundingBox Box) Map(
        IReadOnlyList<OcrPoint> polygon, OcrBoundingBox box, Func<OcrPoint, OcrPoint> map)
    {
        if (polygon.Count > 0)
        {
            var points = new OcrPoint[polygon.Count];
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = map(polygon[i]);
            }
            return (points, OcrBoundingBox.FromPoints(points));
        }

        // No polygon: an empty box (a geometry-less character) stays empty; otherwise map its corners.
        if (box == OcrBoundingBox.Empty) return (polygon, box);
        var corners = new[]
        {
            map(new OcrPoint(box.MinX, box.MinY)), map(new OcrPoint(box.MaxX, box.MinY)),
            map(new OcrPoint(box.MaxX, box.MaxY)), map(new OcrPoint(box.MinX, box.MaxY)),
        };
        return (polygon, OcrBoundingBox.FromPoints(corners));
    }
}
