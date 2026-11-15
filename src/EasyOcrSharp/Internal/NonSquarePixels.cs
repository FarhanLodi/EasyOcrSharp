using EasyOcrSharp.Models;
using EasyImageSharp;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Internal;

/// <summary>
/// <see cref="PreprocessingOptions.CorrectNonSquarePixels"/>: corrects images whose metadata declares
/// non-square pixels — typically fax pages scanned at 204×98 DPI. CRAFT and the CRNN assume square pixels, so
/// such a page reaches them squashed to half height (glyphs twice as wide as they should be). The image is
/// resampled so both axes share the higher resolution, OCR runs on that copy, and every coordinate is scaled
/// back onto the caller's pixel grid with <see cref="SquarePixelMapping"/>.
/// </summary>
/// <remarks>
/// An image whose resolution metadata is absent, unreadable, or square within <see cref="Tolerance"/> gets no
/// copy (<see cref="CreateSquarePixelCopy"/> returns null) and the pipeline runs on the caller's image exactly
/// as it does with the option off. A declared aspect beyond <see cref="MaxCorrectionRatio"/> is treated as
/// bogus metadata and ignored, so a hostile header cannot make the pipeline allocate an arbitrarily large copy.
/// </remarks>
internal static class NonSquarePixels
{
    /// <summary>
    /// Relative difference between the horizontal and vertical resolution (|x/y − 1|) above which the pixels
    /// are treated as non-square. Small differences (rounding, 72 vs 75 DPI, 204×196 "fine" fax) are left alone.
    /// </summary>
    internal const double Tolerance = 0.15;

    /// <summary>
    /// Largest resolution ratio corrected. Real non-square scans are about 2:1 (204×98 DPI fax); a larger
    /// declared ratio is far more likely broken metadata than a real scan, and correcting it would multiply the
    /// pixel count by that ratio.
    /// </summary>
    internal const double MaxCorrectionRatio = 4.0;

    /// <summary>
    /// The resolution of <paramref name="image"/>: its root frame's EXIF/TIFF resolution tags when both are
    /// present (a multi-page TIFF carries them per page, and a frame cloned out of one keeps them), otherwise
    /// the image-level metadata. Only the ratio matters, so the unit is irrelevant.
    /// </summary>
    internal static (double X, double Y) GetResolution(Image<Rgb24> image)
    {
        if (image.Frames.RootFrame.Metadata.ExifProfile is { } exif
            && exif.TryGetValue(ExifTag.XResolution, out var xr) && xr is not null
            && exif.TryGetValue(ExifTag.YResolution, out var yr) && yr is not null)
        {
            double x = xr.Value.ToDouble(), y = yr.Value.ToDouble();
            if (x > 0 && y > 0 && double.IsFinite(x) && double.IsFinite(y)) return (x, y);
        }
        return (image.Metadata.HorizontalResolution, image.Metadata.VerticalResolution);
    }

    /// <summary>
    /// Computes the per-axis upscale that makes the pixels square — the lower-resolution axis is brought up
    /// to the higher one, so no detail is thrown away — or returns false when the resolutions are unknown,
    /// invalid, square within <see cref="Tolerance"/>, or further apart than <see cref="MaxCorrectionRatio"/>.
    /// </summary>
    internal static bool TryGetScale(double xResolution, double yResolution, out double scaleX, out double scaleY)
    {
        scaleX = scaleY = 1.0;
        if (!(xResolution > 0) || !(yResolution > 0) || !double.IsFinite(xResolution) || !double.IsFinite(yResolution))
            return false;

        double ratio = xResolution / yResolution;
        if (Math.Abs(ratio - 1.0) <= Tolerance) return false;
        if (Math.Max(ratio, 1.0 / ratio) > MaxCorrectionRatio) return false;

        double target = Math.Max(xResolution, yResolution);
        scaleX = target / xResolution;
        scaleY = target / yResolution;
        return true;
    }

    /// <summary>The size of the square-pixel copy of a <paramref name="width"/>×<paramref name="height"/> image.</summary>
    internal static (int Width, int Height) SquaredSize(int width, int height, double scaleX, double scaleY)
        => (Math.Max(1, (int)Math.Round(width * scaleX)), Math.Max(1, (int)Math.Round(height * scaleY)));

    /// <summary>
    /// Returns a square-pixel copy of <paramref name="image"/> (bilinear), or null when its pixels are already
    /// square or its metadata gives no usable resolution. The caller owns the copy.
    /// </summary>
    internal static Image<Rgb24>? CreateSquarePixelCopy(Image<Rgb24> image)
    {
        var (xRes, yRes) = GetResolution(image);
        if (!TryGetScale(xRes, yRes, out double sx, out double sy)) return null;

        var (width, height) = SquaredSize(image.Width, image.Height, sx, sy);
        if (width == image.Width && height == image.Height) return null;
        return image.Clone(c => c.Resize(width, height, KnownResamplers.Triangle));
    }
}

/// <summary>
/// The way back from a square-pixel copy to the caller's pixel grid for one coordinate frame: <c>x · FactorX</c>,
/// <c>y · FactorY</c>, where a factor is the caller's size over the copy's size along that axis of the frame.
/// </summary>
/// <param name="FactorX">Caller-grid pixels per working pixel along the reported frame's x axis.</param>
/// <param name="FactorY">Caller-grid pixels per working pixel along the reported frame's y axis.</param>
internal readonly record struct SquarePixelMapping(double FactorX, double FactorY)
{
    /// <summary>
    /// The mapping for a pipeline that resampled a <paramref name="sourceWidth"/>×<paramref name="sourceHeight"/>
    /// image into a <paramref name="squaredWidth"/>×<paramref name="squaredHeight"/> copy and then (document
    /// orientation) rotated the page by <paramref name="rotationDegrees"/> clockwise: a quarter
    /// turn swaps which source axis the reported x and y run along, so the factors swap with it.
    /// </summary>
    internal static SquarePixelMapping For(int sourceWidth, int sourceHeight, int squaredWidth, int squaredHeight, int rotationDegrees = 0)
    {
        double fx = (double)sourceWidth / squaredWidth;
        double fy = (double)sourceHeight / squaredHeight;
        int quarter = ((rotationDegrees % 360) + 360) % 360;
        return quarter is 90 or 270 ? new SquarePixelMapping(fy, fx) : new SquarePixelMapping(fx, fy);
    }

    /// <summary>Maps a point from the square-pixel frame onto the caller's grid.</summary>
    internal OcrPoint ToSource(OcrPoint p) => new(p.X * FactorX, p.Y * FactorY);

    /// <summary>A working-frame width expressed on the caller's grid.</summary>
    internal int SourceWidth(int workingWidth) => Math.Max(1, (int)Math.Round(workingWidth * FactorX));

    /// <summary>A working-frame height expressed on the caller's grid.</summary>
    internal int SourceHeight(int workingHeight) => Math.Max(1, (int)Math.Round(workingHeight * FactorY));

    /// <summary>
    /// Re-expresses a caller's pixel region on the square-pixel frame. Fractional regions are
    /// resolution-independent and returned unchanged.
    /// </summary>
    internal OcrRegion RegionToSquare(OcrRegion region)
        => region.Normalized
            ? region
            : region with
            {
                X = region.X / FactorX,
                Y = region.Y / FactorY,
                Width = region.Width / FactorX,
                Height = region.Height / FactorY,
            };
}
