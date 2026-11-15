using EasyOcrSharp.Structure.Engine.Models;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Engine.Geometry;

/// <summary>
/// How a rectified crop produced by <see cref="PerspectiveWarp.Rectify(EasyImageSharp.Image{EasyImageSharp.PixelFormats.Rgb24}, OcrPoint[], bool, out CropGeometry)"/>
/// relates to the source image, so positions measured on the crop (e.g. word intervals) can be mapped back.
/// The upright crop is <see cref="Width"/>×<see cref="Height"/> pixels whose corners <c>(0,0)</c>,
/// <c>(W,0)</c>, <c>(W,H)</c>, <c>(0,H)</c> correspond to the four source corners; when
/// <see cref="RotatedVertical"/> is set the returned crop was additionally rotated 90° counter-clockwise.
/// </summary>
/// <param name="TopLeft">Source-image point of the upright crop's <c>(0,0)</c> corner.</param>
/// <param name="TopRight">Source-image point of the upright crop's <c>(W,0)</c> corner.</param>
/// <param name="BottomRight">Source-image point of the upright crop's <c>(W,H)</c> corner.</param>
/// <param name="BottomLeft">Source-image point of the upright crop's <c>(0,H)</c> corner.</param>
/// <param name="Width">Upright crop width in pixels (before any vertical-text rotation).</param>
/// <param name="Height">Upright crop height in pixels (before any vertical-text rotation).</param>
/// <param name="RotatedVertical">True when the crop was rotated 90° CCW (PaddleOCR's <c>np.rot90</c> for tall lines).</param>
internal readonly record struct CropGeometry(
    OcrPoint TopLeft, OcrPoint TopRight, OcrPoint BottomRight, OcrPoint BottomLeft,
    int Width, int Height, bool RotatedVertical)
{
    /// <summary>
    /// Maps a point of the upright crop (<c>u</c> across, <c>v</c> down) to source-image coordinates by
    /// interpolating along the quad's top and bottom edges by <c>u/W</c>, then between them by <c>v/H</c>
    /// (exact for the rectangles and parallelograms the detector produces).
    /// </summary>
    /// <param name="u">Horizontal position in the upright crop, 0..<see cref="Width"/>.</param>
    /// <param name="v">Vertical position in the upright crop, 0..<see cref="Height"/>.</param>
    /// <returns>The corresponding source-image point.</returns>
    public OcrPoint MapUpright(double u, double v)
    {
        double fu = Width > 0 ? u / Width : 0;
        double fv = Height > 0 ? v / Height : 0;
        double topX = TopLeft.X + (TopRight.X - TopLeft.X) * fu;
        double topY = TopLeft.Y + (TopRight.Y - TopLeft.Y) * fu;
        double bottomX = BottomLeft.X + (BottomRight.X - BottomLeft.X) * fu;
        double bottomY = BottomLeft.Y + (BottomRight.Y - BottomLeft.Y) * fu;
        return new OcrPoint(topX + (bottomX - topX) * fv, topY + (bottomY - topY) * fv);
    }

    /// <summary>
    /// Maps a point of the crop exactly as <see cref="PerspectiveWarp.Rectify(EasyImageSharp.Image{EasyImageSharp.PixelFormats.Rgb24}, OcrPoint[], bool, out CropGeometry)"/>
    /// returned it (after the optional 90° CCW rotation) to source-image coordinates. A 90° CCW rotation
    /// sends upright <c>(u, v)</c> to crop <c>(v, W − u)</c>, so the inverse is applied first.
    /// </summary>
    /// <param name="x">Horizontal position in the returned crop.</param>
    /// <param name="y">Vertical position in the returned crop.</param>
    /// <returns>The corresponding source-image point.</returns>
    public OcrPoint MapCrop(double x, double y)
        => RotatedVertical ? MapUpright(Width - y, x) : MapUpright(x, y);
}
