using System.Text;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Internal;

/// <summary>
/// The quadrilateral of a detected text region, with its corners named along the text direction. Corner
/// naming reproduces <see cref="PerspectiveWarp"/>'s <c>OrderCorners</c> exactly (top-left = min x+y,
/// bottom-right = max x+y, top-right = min y−x, bottom-left = max y−x), so a normalized position derived
/// here lands on the same pixel the recognizer actually saw.
/// </summary>
internal readonly record struct TextQuad(OcrPoint TopLeft, OcrPoint TopRight, OcrPoint BottomRight, OcrPoint BottomLeft)
{
    /// <summary>
    /// Orders a detection polygon's corners. Polygons with fewer than four points degrade to the
    /// axis-aligned bounding box, which is what <see cref="PerspectiveWarp"/> falls back to as well.
    /// </summary>
    public static TextQuad FromPolygon(IReadOnlyList<OcrPoint> polygon)
    {
        if (polygon.Count < 4)
        {
            var b = OcrBoundingBox.FromPoints(polygon);
            return new TextQuad(
                new OcrPoint(b.MinX, b.MinY), new OcrPoint(b.MaxX, b.MinY),
                new OcrPoint(b.MaxX, b.MaxY), new OcrPoint(b.MinX, b.MaxY));
        }

        OcrPoint tl = polygon[0], br = polygon[0], tr = polygon[0], bl = polygon[0];
        double minSum = double.MaxValue, maxSum = double.MinValue, minDiff = double.MaxValue, maxDiff = double.MinValue;
        foreach (var p in polygon)
        {
            double sum = p.X + p.Y, diff = p.Y - p.X;
            if (sum < minSum) { minSum = sum; tl = p; }
            if (sum > maxSum) { maxSum = sum; br = p; }
            if (diff < minDiff) { minDiff = diff; tr = p; }
            if (diff > maxDiff) { maxDiff = diff; bl = p; }
        }
        return new TextQuad(tl, tr, br, bl);
    }

    /// <summary>
    /// Bilinearly maps normalized quad coordinates — <paramref name="u"/> along the text direction,
    /// <paramref name="v"/> across it — onto source-image pixels. Because the result is a convex
    /// combination of the four corners, every point produced lies inside the region's own polygon.
    /// </summary>
    public OcrPoint Map(double u, double v)
    {
        double topX = TopLeft.X + ((TopRight.X - TopLeft.X) * u);
        double topY = TopLeft.Y + ((TopRight.Y - TopLeft.Y) * u);
        double botX = BottomLeft.X + ((BottomRight.X - BottomLeft.X) * u);
        double botY = BottomLeft.Y + ((BottomRight.Y - BottomLeft.Y) * u);
        return new OcrPoint(topX + ((botX - topX) * v), topY + ((botY - topY) * v));
    }
}

/// <summary>
/// Undoes the per-box rotation of <see cref="CrnnRunOptions.RotationInfo"/>: converts a normalized
/// coordinate of the patch that was actually fed to the recognizer back into a normalized coordinate of
/// the rectified crop. For the upright pass this is the identity; for a rotated pass it inverts
/// ImageSharp's expand-canvas rotation about the image centre, which is what
/// <c>Clone(ctx =&gt; ctx.Rotate(angle))</c> performs.
/// </summary>
internal readonly record struct PatchTransform(
    double CropWidth,
    double CropHeight,
    double PatchWidth,
    double PatchHeight,
    double AngleDegrees)
{
    /// <summary>The identity transform — the patch is the rectified crop itself.</summary>
    public static PatchTransform Upright(int cropWidth, int cropHeight)
        => new(cropWidth, cropHeight, cropWidth, cropHeight, 0);

    /// <summary>
    /// The transform for a crop rotated clockwise by <paramref name="angleDegrees"/> onto an expanded
    /// canvas of <paramref name="patchWidth"/>×<paramref name="patchHeight"/>.
    /// </summary>
    public static PatchTransform Rotated(int cropWidth, int cropHeight, int patchWidth, int patchHeight, double angleDegrees)
        => new(cropWidth, cropHeight, patchWidth, patchHeight, angleDegrees);

    /// <summary>
    /// Maps normalized patch coordinates to normalized crop coordinates, clamped to the crop so a
    /// rotated canvas's empty corners can never push geometry outside the detected region.
    /// </summary>
    public (double U, double V) ToCrop(double u, double v)
    {
        double angle = AngleDegrees % 360.0;
        if (angle == 0 || CropWidth <= 0 || CropHeight <= 0) return (u, v);

        // Forward (what ImageSharp did): p' = R(θ)·(p − cropCentre) + patchCentre, with
        // R(θ) = [[cos, −sin], [sin, cos]] which is a clockwise turn in y-down image space.
        // Inverse: p = R(−θ)·(p' − patchCentre) + cropCentre.
        double theta = angle * Math.PI / 180.0;
        double cos = Math.Cos(theta), sin = Math.Sin(theta);
        double dx = (u * PatchWidth) - (PatchWidth / 2.0);
        double dy = (v * PatchHeight) - (PatchHeight / 2.0);
        double x = (dx * cos) + (dy * sin) + (CropWidth / 2.0);
        double y = (-dx * sin) + (dy * cos) + (CropHeight / 2.0);
        return (Clamp01(x / CropWidth), Clamp01(y / CropHeight));
    }

    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
}

/// <summary>
/// Everything needed to turn a CTC alignment into pixels: the alignment itself, the number of timesteps
/// it spans, the width of the real (unpadded) content inside the recognizer input, the padded width that
/// was actually fed to the model, and the patch → crop transform of the winning pass.
/// </summary>
internal sealed record PatchAlignment(
    IReadOnlyList<CtcCharAlignment> Alignment,
    int Steps,
    int ContentWidth,
    int PaddedWidth,
    PatchTransform Patch);

/// <summary>
/// Projects a CTC alignment onto source-image geometry: timestep spans become normalized positions along
/// the text direction, those positions are mapped through the winning pass's rotation back into the
/// rectified crop, and finally interpolated across the detected region's quadrilateral — independently
/// along its top and bottom edges, so a skewed line yields genuinely skewed character quads instead of
/// axis-aligned rectangles.
/// </summary>
internal static class WordGeometry
{
    /// <summary>
    /// The width of the real image content inside a recognizer input, i.e. the resized width before
    /// <see cref="ImageProcessing.PreprocessForCrnn"/>'s minimum-width edge padding. Mirrors that
    /// method's own arithmetic; timestep→pixel mapping must divide by this and never by the padded width,
    /// or the padding columns would stretch the map.
    /// </summary>
    public static int ContentWidth(int cropWidth, int cropHeight, int targetHeight, int maxWidth)
    {
        if (cropWidth <= 0 || cropHeight <= 0) return 0;
        int contentWidth = (int)Math.Ceiling(targetHeight * ((double)cropWidth / cropHeight));
        if (contentWidth < 1) contentWidth = 1;
        if (contentWidth > maxWidth) contentWidth = maxWidth;
        return contentWidth;
    }

    /// <summary>
    /// Builds the per-word (and, for <see cref="WordLevelDetail.Characters"/>, per-character) geometry of
    /// one recognized region. Returns two empty lists when <paramref name="detail"/> is
    /// <see cref="WordLevelDetail.None"/> or the alignment carries nothing usable, so callers can hand the
    /// result straight to <see cref="OcrLine"/>.
    /// </summary>
    public static (IReadOnlyList<OcrWord> Words, IReadOnlyList<OcrChar> Characters) Build(
        IReadOnlyList<OcrPoint> polygon,
        PatchAlignment patch,
        WordLevelDetail detail)
    {
        int count = patch.Alignment.Count;
        if (detail == WordLevelDetail.None || count == 0 || patch.Steps <= 0 || patch.ContentWidth <= 0 || polygon.Count < 3)
        {
            return (Array.Empty<OcrWord>(), Array.Empty<OcrChar>());
        }

        var quad = TextQuad.FromPolygon(polygon);

        // Timesteps cover the padded input, positions are wanted relative to the content: a step's
        // fraction of the padded width, rescaled by padded/content, is its fraction of the content.
        double scale = patch.PaddedWidth > patch.ContentWidth
            ? (double)patch.PaddedWidth / patch.ContentWidth
            : 1.0;
        double perStep = scale / patch.Steps;

        var words = new List<OcrWord>();
        var characters = detail == WordLevelDetail.Characters ? new List<OcrChar>(count) : null;

        var text = new StringBuilder();
        double wordStart = 0, wordEnd = 0, confidenceSum = 0;
        int inWord = 0;

        for (int i = 0; i < count; i++)
        {
            var entry = patch.Alignment[i];
            double u0 = Clamp01(entry.StartStep * perStep);
            double u1 = Clamp01((entry.EndStep + 1) * perStep);
            if (u1 < u0) u1 = u0;

            if (characters is not null)
            {
                var polygonPoints = SpanPolygon(quad, patch.Patch, u0, u1);
                characters.Add(new OcrChar
                {
                    Value = entry.Value.ToString(),
                    Confidence = entry.Confidence,
                    BoundingPolygon = polygonPoints,
                    BoundingBox = OcrBoundingBox.FromPoints(polygonPoints),
                });
            }

            if (char.IsWhiteSpace(entry.Value))
            {
                Flush();
                continue;
            }

            if (inWord == 0) wordStart = u0;
            wordEnd = u1;
            confidenceSum += entry.Confidence;
            inWord++;
            text.Append(entry.Value);
        }
        Flush();

        IReadOnlyList<OcrChar> characterList = Array.Empty<OcrChar>();
        if (characters is not null) characterList = characters;
        return (words, characterList);

        // Closes the word being accumulated: its span is the hull of its characters' spans along the
        // text direction and its confidence their arithmetic mean, exactly as OcrWord documents.
        void Flush()
        {
            if (inWord == 0) return;
            var polygonPoints = SpanPolygon(quad, patch.Patch, wordStart, wordEnd);
            words.Add(new OcrWord
            {
                Text = text.ToString(),
                Confidence = confidenceSum / inWord,
                BoundingPolygon = polygonPoints,
                BoundingBox = OcrBoundingBox.FromPoints(polygonPoints),
            });
            text.Clear();
            confidenceSum = 0;
            inWord = 0;
        }
    }

    /// <summary>
    /// The source-image quadrilateral of the patch slice <c>[u0, u1]</c> across the full patch height,
    /// ordered top-left, top-right, bottom-right, bottom-left along the text direction.
    /// </summary>
    private static OcrPoint[] SpanPolygon(in TextQuad quad, in PatchTransform patch, double u0, double u1)
    {
        var topLeft = patch.ToCrop(u0, 0);
        var topRight = patch.ToCrop(u1, 0);
        var bottomRight = patch.ToCrop(u1, 1);
        var bottomLeft = patch.ToCrop(u0, 1);
        return
        [
            quad.Map(topLeft.U, topLeft.V),
            quad.Map(topRight.U, topRight.V),
            quad.Map(bottomRight.U, bottomRight.V),
            quad.Map(bottomLeft.U, bottomLeft.V),
        ];
    }

    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
}
