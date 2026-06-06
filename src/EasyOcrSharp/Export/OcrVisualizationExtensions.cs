using EasyOcrSharp.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace EasyOcrSharp.Export;

/// <summary>
/// Debug/visualization helpers that draw detected/recognized region outlines onto a copy of the
/// source image. Implemented with simple pixel drawing so the core package needs no extra
/// dependency. The input image is never modified — a new annotated image is returned.
/// </summary>
public static class OcrVisualizationExtensions
{
    private static readonly Rgb24 DefaultColor = new(255, 0, 0);

    /// <summary>Returns a copy of <paramref name="image"/> with each recognized line's polygon outlined.</summary>
    public static Image<Rgb24> DrawAnnotations(this Image<Rgb24> image, OcrResult result, Rgb24? color = null, int thickness = 2)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(result);
        return Draw(image, result.Lines.Select(l => l.BoundingPolygon), color ?? DefaultColor, thickness);
    }

    /// <summary>Returns a copy of <paramref name="image"/> with each detected region's polygon outlined.</summary>
    public static Image<Rgb24> DrawAnnotations(this Image<Rgb24> image, IEnumerable<DetectedRegion> regions, Rgb24? color = null, int thickness = 2)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(regions);
        return Draw(image, regions.Select(r => r.BoundingPolygon), color ?? DefaultColor, thickness);
    }

    private static Image<Rgb24> Draw(Image<Rgb24> image, IEnumerable<IReadOnlyList<OcrPoint>> polygons, Rgb24 color, int thickness)
    {
        var clone = image.Clone();
        int t = Math.Max(1, thickness);
        foreach (var poly in polygons)
        {
            if (poly.Count < 2) continue;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count]; // close the polygon
                DrawLine(clone, (int)Math.Round(a.X), (int)Math.Round(a.Y), (int)Math.Round(b.X), (int)Math.Round(b.Y), color, t);
            }
        }
        return clone;
    }

    /// <summary>Bresenham line with a square brush of side <paramref name="thickness"/>.</summary>
    private static void DrawLine(Image<Rgb24> img, int x0, int y0, int x1, int y1, Rgb24 color, int thickness)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            Plot(img, x0, y0, color, thickness);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    private static void Plot(Image<Rgb24> img, int cx, int cy, Rgb24 color, int thickness)
    {
        int r = thickness / 2;
        for (int y = cy - r; y <= cy + r; y++)
        {
            if (y < 0 || y >= img.Height) continue;
            for (int x = cx - r; x <= cx + r; x++)
            {
                if (x < 0 || x >= img.Width) continue;
                img[x, y] = color;
            }
        }
    }
}
