using EasyOcrSharp.Structure.Engine.Models;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Engine.Geometry;

/// <summary>
/// Pure coordinate transforms between an image's frame and the frame of that image rotated by a
/// right-angle multiple. Used by the OCR pipeline to report boxes in the <b>original</b> image's
/// orientation after the document-orientation classifier uprighted the page: recognition runs on the
/// rotated (upright) page, then every quad is inverse-rotated back here.
/// <para>
/// Convention: continuous "corner" coordinates — the image occupies <c>[0,W]×[0,H]</c> and a clockwise
/// 90° rotation maps <c>(x,y) → (H−y, x)</c> (so corners map to corners, no ±1 pixel-index bias).
/// </para>
/// </summary>
internal static class OrientationMapper
{
    /// <summary>
    /// Maps a point from the frame of a <paramref name="srcWidth"/>×<paramref name="srcHeight"/> image
    /// into the frame of the same image rotated <paramref name="degreesCw"/>° clockwise
    /// (0/90/180/270 only; any multiple of 90 is normalized into that range).
    /// </summary>
    internal static OcrPoint RotatePoint(OcrPoint p, int degreesCw, int srcWidth, int srcHeight)
    {
        int deg = ((degreesCw % 360) + 360) % 360;
        return deg switch
        {
            0 => p,
            90 => new OcrPoint(srcHeight - p.Y, p.X),
            180 => new OcrPoint(srcWidth - p.X, srcHeight - p.Y),
            270 => new OcrPoint(p.Y, srcWidth - p.X),
            _ => throw new ArgumentOutOfRangeException(nameof(degreesCw), degreesCw,
                "Rotation must be a multiple of 90 degrees."),
        };
    }

    /// <summary>
    /// Maps every point of a polygon from the source frame into the frame of the source image rotated
    /// <paramref name="degreesCw"/>° clockwise. See <see cref="RotatePoint"/> for the convention.
    /// </summary>
    internal static OcrPoint[] RotatePolygon(IReadOnlyList<OcrPoint> points, int degreesCw, int srcWidth, int srcHeight)
    {
        var result = new OcrPoint[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            result[i] = RotatePoint(points[i], degreesCw, srcWidth, srcHeight);
        }
        return result;
    }

    /// <summary>
    /// Maps recognized lines from the <b>working</b> (uprighted) frame back into the <b>original</b>
    /// image's frame, given the clockwise rotation that was applied to the original to produce the
    /// working image. The inverse rotation is <c>(360 − applied) % 360</c> clockwise, performed with the
    /// working image's dimensions as the source frame. Point <i>order</i> within each quad is preserved
    /// (the quad stays a valid ring; its first point is no longer necessarily the visual top-left in the
    /// original frame — by design, since the text reads in the working frame's direction).
    /// </summary>
    /// <param name="lines">Lines whose polygons are in the working (rotated) frame.</param>
    /// <param name="appliedRotationCw">Clockwise degrees the original was rotated by to make the working image.</param>
    /// <param name="workingWidth">Working (rotated) image width.</param>
    /// <param name="workingHeight">Working (rotated) image height.</param>
    internal static IReadOnlyList<OcrLine> MapLinesToOriginalFrame(
        IReadOnlyList<OcrLine> lines, int appliedRotationCw, int workingWidth, int workingHeight)
    {
        int inverse = ((360 - appliedRotationCw) % 360 + 360) % 360;
        if (inverse == 0 || lines.Count == 0) return lines;

        var mapped = new List<OcrLine>(lines.Count);
        foreach (var line in lines)
        {
            var poly = RotatePolygon(line.BoundingPolygon, inverse, workingWidth, workingHeight);
            mapped.Add(line with
            {
                BoundingPolygon = poly,
                BoundingBox = OcrBoundingBox.FromPoints(poly),
                Words = Recognition.WordBoxBuilder.Transform(line.Words, p => RotatePoint(p, inverse, workingWidth, workingHeight)),
            });
        }
        return mapped;
    }
}
