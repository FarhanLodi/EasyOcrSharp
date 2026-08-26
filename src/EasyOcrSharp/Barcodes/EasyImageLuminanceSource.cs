using System;
using EasyOcrSharp.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using ZXing;

namespace EasyOcrSharp.Barcodes;

/// <summary>
/// Bridges an EasyImageSharp <see cref="Image{TPixel}"/> to the barcode decoder's greyscale input, without a
/// binding package and without ever touching <c>System.Drawing</c>. Pixels are read once, row-span at a
/// time, into a private luminance buffer; nothing afterwards holds a reference to the source image, so the
/// caller may dispose or mutate it while a scan is in flight.
/// </summary>
/// <remarks>
/// This type derives from a decoder-library base class and is therefore deliberately <c>internal</c> —
/// keeping the barcode engine out of EasyOcrSharp's public surface. Its lower-cased members
/// (<c>getRow</c>, <c>crop</c>, <c>invert</c>) are overrides of that base class's Java-derived naming and
/// cannot be renamed.
/// </remarks>
internal sealed class EasyImageLuminanceSource : LuminanceSource
{
    // Integer luminance weights matching the decoder's own RGB->grey conversion, so a symbol that the
    // library would have read through its stock sources reads identically through this one.
    private const int RedWeight = 19562;
    private const int GreenWeight = 38550;
    private const int BlueWeight = 7424;
    private const int WeightShift = 16;

    private readonly byte[] _luminances;

    private EasyImageLuminanceSource(byte[] luminances, int width, int height)
        : base(width, height)
        => _luminances = luminances;

    /// <summary>
    /// Reads a rectangular window of an image into a luminance source. The rectangle is assumed to have
    /// already been clamped to the image bounds (see <see cref="OcrRegion.Resolve"/>).
    /// </summary>
    internal static EasyImageLuminanceSource Create(Image<Rgb24> image, int left, int top, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        long pixels = (long)width * height;
        if (pixels > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(width), "Region is too large to scan for barcodes.");

        var luminances = new byte[(int)pixels];

        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = rows.GetRowSpan(top + y);
                int destBase = y * width;
                for (int x = 0; x < width; x++)
                {
                    var px = row[left + x];
                    luminances[destBase + x] =
                        (byte)(((RedWeight * px.R) + (GreenWeight * px.G) + (BlueWeight * px.B)) >> WeightShift);
                }
            }
        });

        return new EasyImageLuminanceSource(luminances, width, height);
    }

    /// <inheritdoc/>
    public override byte[] Matrix => _luminances;

    /// <inheritdoc/>
    public override byte[] getRow(int y, byte[]? row)
    {
        if (y < 0 || y >= Height)
            throw new ArgumentOutOfRangeException(nameof(y), y, $"Row is outside the image (height {Height}).");

        if (row is null || row.Length < Width)
            row = new byte[Width];

        Array.Copy(_luminances, y * Width, row, 0, Width);
        return row;
    }

    /// <inheritdoc/>
    public override bool CropSupported => true;

    /// <inheritdoc/>
    public override LuminanceSource crop(int left, int top, int width, int height)
    {
        // Clamped rather than validated: the multi-code reader walks the image with computed rectangles,
        // and a rounding error there should narrow the crop, not abort an otherwise successful scan.
        left = Math.Clamp(left, 0, Math.Max(0, Width - 1));
        top = Math.Clamp(top, 0, Math.Max(0, Height - 1));
        width = Math.Clamp(width, 1, Width - left);
        height = Math.Clamp(height, 1, Height - top);

        var cropped = new byte[width * height];
        for (int y = 0; y < height; y++)
            Array.Copy(_luminances, ((top + y) * Width) + left, cropped, y * width, width);

        return new EasyImageLuminanceSource(cropped, width, height);
    }

    /// <inheritdoc/>
    public override bool InversionSupported => true;

    /// <inheritdoc/>
    public override LuminanceSource invert()
    {
        var inverted = new byte[_luminances.Length];
        for (int i = 0; i < _luminances.Length; i++)
            inverted[i] = (byte)(255 - _luminances[i]);

        return new EasyImageLuminanceSource(inverted, Width, Height);
    }

    /// <inheritdoc/>
    public override bool RotateSupported => true;

    /// <inheritdoc/>
    public override LuminanceSource rotateCounterClockwise()
    {
        int newWidth = Height;
        int newHeight = Width;
        var rotated = new byte[_luminances.Length];

        for (int y = 0; y < Height; y++)
        {
            int srcBase = y * Width;
            for (int x = 0; x < Width; x++)
            {
                // (x, y) -> (y, Width - 1 - x)
                rotated[((newHeight - 1 - x) * newWidth) + y] = _luminances[srcBase + x];
            }
        }

        return new EasyImageLuminanceSource(rotated, newWidth, newHeight);
    }

    /// <summary>
    /// Maps a point reported in a frame that was rotated counter-clockwise <paramref name="rotationCount"/>
    /// times back into the un-rotated frame of a <paramref name="sourceWidth"/> ×
    /// <paramref name="sourceHeight"/> image. The decoder reports coordinates in whichever orientation it
    /// found the symbol, so this is what keeps <see cref="BarcodeResult.BoundingPolygon"/> honest under
    /// <see cref="BarcodeOptions.AutoRotate"/>.
    /// </summary>
    internal static OcrPoint Unrotate(double x, double y, int rotationCount, int sourceWidth, int sourceHeight)
    {
        for (int step = rotationCount - 1; step >= 0; step--)
        {
            // Frame `step` is the source rotated `step` times, so its width alternates.
            int width = (step % 2) == 0 ? sourceWidth : sourceHeight;
            (x, y) = (width - 1 - y, x);
        }

        return new OcrPoint(x, y);
    }
}
