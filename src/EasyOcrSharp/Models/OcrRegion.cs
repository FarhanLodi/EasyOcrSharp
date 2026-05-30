namespace EasyOcrSharp.Models;

/// <summary>
/// A rectangular region of an image to restrict OCR to. Use <see cref="Pixels"/> for absolute
/// pixel coordinates or <see cref="Fraction"/> for resolution-independent fractions (0–1) of the
/// image size. Recognized bounding boxes are reported back in the original image's coordinate space.
/// </summary>
public readonly record struct OcrRegion
{
    /// <summary>Left edge (pixels, or 0–1 fraction when <see cref="Normalized"/> is true).</summary>
    public double X { get; init; }

    /// <summary>Top edge (pixels, or 0–1 fraction when <see cref="Normalized"/> is true).</summary>
    public double Y { get; init; }

    /// <summary>Width (pixels, or 0–1 fraction when <see cref="Normalized"/> is true).</summary>
    public double Width { get; init; }

    /// <summary>Height (pixels, or 0–1 fraction when <see cref="Normalized"/> is true).</summary>
    public double Height { get; init; }

    /// <summary>When true, X/Y/Width/Height are fractions (0–1) of the image size.</summary>
    public bool Normalized { get; init; }

    /// <summary>A region in absolute pixel coordinates.</summary>
    public static OcrRegion Pixels(double x, double y, double width, double height)
        => new() { X = x, Y = y, Width = width, Height = height, Normalized = false };

    /// <summary>
    /// A region as fractions (0–1) of the image size — e.g. <c>Fraction(0, 0.5, 1, 0.5)</c> is the
    /// bottom half. Resolution-independent.
    /// </summary>
    public static OcrRegion Fraction(double x, double y, double width, double height)
        => new() { X = x, Y = y, Width = width, Height = height, Normalized = true };

    /// <summary>
    /// Resolves this region to an integer pixel rectangle clamped to the given image bounds.
    /// Returns (x, y, width, height); width/height are 0 if the region is empty after clamping.
    /// </summary>
    internal (int X, int Y, int Width, int Height) Resolve(int imageWidth, int imageHeight)
    {
        double rx = Normalized ? X * imageWidth : X;
        double ry = Normalized ? Y * imageHeight : Y;
        double rw = Normalized ? Width * imageWidth : Width;
        double rh = Normalized ? Height * imageHeight : Height;

        int x = (int)Math.Round(Math.Clamp(rx, 0, imageWidth));
        int y = (int)Math.Round(Math.Clamp(ry, 0, imageHeight));
        int w = (int)Math.Round(Math.Clamp(rw, 0, imageWidth - x));
        int h = (int)Math.Round(Math.Clamp(rh, 0, imageHeight - y));
        return (x, y, w, h);
    }
}
