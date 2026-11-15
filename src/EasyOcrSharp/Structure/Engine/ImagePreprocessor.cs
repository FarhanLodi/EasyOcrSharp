using EasyOcrSharp.Structure.Engine.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// Scanned-document clean-up: optional denoise, adaptive binarization, and deskew. Orientation
/// (90°/180°/270°) detection is handled at the service level. Returns a new image; the caller owns and
/// disposes it. When deskew rotates the page the working image is an enlarged canvas, and recognized
/// coordinates must be mapped back with <see cref="MapFromRotatedCanvas"/>.
/// </summary>
internal static class ImagePreprocessor
{
    /// <summary>Largest skew (degrees, either direction) the deskew estimator searches.</summary>
    internal const float MaxSkewDegrees = 15f;

    /// <summary>Skews at or below this (degrees) are left alone — not worth a resampling pass.</summary>
    internal const float MinDeskewDegrees = 0.1f;

    /// <summary>
    /// Applies denoise → deskew → binarize (in that order) per <paramref name="options"/>.
    /// Always returns a fresh image (a clone even when nothing is enabled) so the caller can dispose
    /// uniformly without touching the original.
    /// </summary>
    public static Image<Rgb24> Apply(Image<Rgb24> source, PreprocessingOptions options)
        => Apply(source, options, out _);

    /// <summary>
    /// Applies denoise → deskew → binarize and reports, in <paramref name="deskewRotation"/>, the clockwise
    /// rotation (degrees) the deskew step applied — 0 when the page was not rotated. A non-zero value means
    /// the returned image is the rotated, enlarged canvas of <see cref="RotateWithWhiteBackground"/>.
    /// </summary>
    public static Image<Rgb24> Apply(Image<Rgb24> source, PreprocessingOptions options, out float deskewRotation)
    {
        deskewRotation = 0f;
        var img = source.Clone();
        try
        {
            if (options.Denoise)
            {
                img.Mutate(c => c.GaussianBlur(0.6f));
            }

            if (options.Deskew)
            {
                float rotation = EstimateDeskewRotation(img);
                if (rotation != 0f)
                {
                    var rotated = RotateWithWhiteBackground(img, rotation);
                    img.Dispose();
                    img = rotated;
                    deskewRotation = rotation;
                }
            }

            if (options.Binarize)
            {
                img.Mutate(c => c.AdaptiveThreshold());
            }

            return img;
        }
        catch
        {
            img.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Rotates by an exact multiple of 90° (lossless, no fill needed). Returns a new image.
    /// </summary>
    public static Image<Rgb24> RotateRightAngle(Image<Rgb24> source, int degrees)
        => source.Clone(c => c.Rotate(degrees));

    /// <summary>
    /// The clockwise rotation (degrees) that straightens the page, or 0 when the skew is negligible.
    /// Uses EasyImageSharp's Hough estimator on background-normalized text baselines (one pass over a
    /// downscaled luminance plane), which reports the content's clockwise skew; the correction is its
    /// negation.
    /// </summary>
    internal static float EstimateDeskewRotation(Image<Rgb24> image)
    {
        float skew = image.DetectSkew(MaxSkewDegrees);
        return float.IsFinite(skew) && Math.Abs(skew) > MinDeskewDegrees ? -skew : 0f;
    }

    /// <summary>
    /// Rotates clockwise by an arbitrary angle onto an enlarged canvas, filling the exposed corners with
    /// white (so binarization and detection don't see black triangles). Composites the transparent-corner
    /// rotation over a white canvas.
    /// </summary>
    internal static Image<Rgb24> RotateWithWhiteBackground(Image<Rgb24> source, float degrees)
    {
        using var rgba = source.CloneAs<Rgba32>();
        rgba.Mutate(c => c.Rotate(degrees)); // exposed area is transparent
        var result = new Image<Rgb24>(rgba.Width, rgba.Height, new Rgb24(255, 255, 255));
        result.Mutate(c => c.DrawImage(rgba, 1f));
        return result;
    }

    /// <summary>
    /// Maps a point from the canvas produced by <see cref="RotateWithWhiteBackground"/> back into the
    /// source image: <c>p_src = R(θ)·(p − c_canvas) + c_src</c>, the inverse of the clockwise rotation about
    /// the image centres (continuous pixel-corner coordinates, matching the rotation's own sampling grid).
    /// </summary>
    internal static OcrPoint MapPointFromRotatedCanvas(
        OcrPoint p, float rotationDegrees, int canvasWidth, int canvasHeight, int sourceWidth, int sourceHeight)
    {
        double rad = rotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double tx = p.X - (canvasWidth / 2.0);
        double ty = p.Y - (canvasHeight / 2.0);
        return new OcrPoint(
            (cos * tx) + (sin * ty) + (sourceWidth / 2.0),
            (-sin * tx) + (cos * ty) + (sourceHeight / 2.0));
    }

    /// <summary>
    /// The inverse of <see cref="MapPointFromRotatedCanvas"/>: maps a point of the source image onto the canvas
    /// produced by <see cref="RotateWithWhiteBackground"/>, <c>p_canvas = R(−θ)·(p − c_src) + c_canvas</c>.
    /// </summary>
    internal static OcrPoint MapPointToRotatedCanvas(
        OcrPoint p, float rotationDegrees, int canvasWidth, int canvasHeight, int sourceWidth, int sourceHeight)
    {
        double rad = rotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double tx = p.X - (sourceWidth / 2.0);
        double ty = p.Y - (sourceHeight / 2.0);
        return new OcrPoint(
            (cos * tx) - (sin * ty) + (canvasWidth / 2.0),
            (sin * tx) + (cos * ty) + (canvasHeight / 2.0));
    }

    /// <summary>
    /// Re-expresses a caller's <see cref="OcrRegion"/> (given against the source image) on the deskewed canvas:
    /// the axis-aligned bounds of the region's rotated corners, in canvas pixels. The rotated rectangle is
    /// slightly larger than the original, so text just outside a corner of the region can be read too.
    /// </summary>
    internal static OcrRegion MapRegionToRotatedCanvas(
        OcrRegion region, float rotationDegrees, int canvasWidth, int canvasHeight, int sourceWidth, int sourceHeight)
    {
        if (rotationDegrees == 0f) return region;

        var (x, y, w, h) = region.Resolve(sourceWidth, sourceHeight);
        var box = OcrBoundingBox.FromPoints(new[]
        {
            new OcrPoint(x, y), new OcrPoint(x + w, y), new OcrPoint(x + w, y + h), new OcrPoint(x, y + h),
        }.Select(p => MapPointToRotatedCanvas(p, rotationDegrees, canvasWidth, canvasHeight, sourceWidth, sourceHeight)));
        return OcrRegion.Pixels(box.MinX, box.MinY, box.MaxX - box.MinX, box.MaxY - box.MinY);
    }

    /// <summary>
    /// Maps recognized lines — and their word boxes — from the deskewed canvas back onto the original
    /// (unrotated) source image.
    /// </summary>
    internal static IReadOnlyList<OcrLine> MapFromRotatedCanvas(
        IReadOnlyList<OcrLine> lines, float rotationDegrees, int canvasWidth, int canvasHeight, int sourceWidth, int sourceHeight)
    {
        if (rotationDegrees == 0f || lines.Count == 0) return lines;

        OcrPoint Map(OcrPoint p) => MapPointFromRotatedCanvas(p, rotationDegrees, canvasWidth, canvasHeight, sourceWidth, sourceHeight);

        var mapped = new List<OcrLine>(lines.Count);
        foreach (var line in lines)
        {
            var poly = TextSkew.CornersOf(line).Select(Map).ToArray();
            mapped.Add(line with
            {
                BoundingPolygon = poly,
                BoundingBox = OcrBoundingBox.FromPoints(poly),
                Words = Recognition.WordBoxBuilder.Transform(line.Words, Map),
            });
        }
        return mapped;
    }
}
