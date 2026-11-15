using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Structure.Preprocess;

/// <summary>
/// Packs <see cref="Rgb24"/> pixels into planar CHW float buffers for the structure models through
/// per-channel 256-entry lookup tables. Each table is built with exactly the per-pixel expression the
/// models were previously fed (<c>(v / 255f - mean) / std</c> or <c>v / 255f</c>), evaluated once per
/// byte value in the same single-precision arithmetic, so the packed tensors are bit-identical to the
/// per-pixel loops they replace. Large images are packed row-parallel.
/// </summary>
internal static class PlanarTensorPacker
{
    // ImageNet mean/std in channel-slot order — the statistics every ImageNet-normalized structure model
    // (doc-ori, PicoDet layout, SLANet/SLANeXt, table classifier, seal det) applies.
    private static readonly float[] ImageNetMean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] ImageNetStd = { 0.229f, 0.224f, 0.225f };

    /// <summary><c>(v / 255f - 0.485f) / 0.229f</c> — ImageNet normalization for channel slot 0.</summary>
    public static readonly float[] ImageNet0 = BuildImageNet(0);

    /// <summary><c>(v / 255f - 0.456f) / 0.224f</c> — ImageNet normalization for channel slot 1.</summary>
    public static readonly float[] ImageNet1 = BuildImageNet(1);

    /// <summary><c>(v / 255f - 0.406f) / 0.225f</c> — ImageNet normalization for channel slot 2.</summary>
    public static readonly float[] ImageNet2 = BuildImageNet(2);

    /// <summary><c>v / 255f</c> — plain [0,1] rescale with no mean/std.</summary>
    public static readonly float[] Scale01 = BuildScale01();

    /// <summary>Pixel count at or above which packing splits rows across threads.</summary>
    private const int ParallelPixelThreshold = 256 * 256;

    private static float[] BuildImageNet(int slot)
    {
        var lut = new float[256];
        for (int v = 0; v < 256; v++)
        {
            lut[v] = (v / 255f - ImageNetMean[slot]) / ImageNetStd[slot];
        }
        return lut;
    }

    private static float[] BuildScale01()
    {
        var lut = new float[256];
        for (int v = 0; v < 256; v++)
        {
            lut[v] = v / 255f;
        }
        return lut;
    }

    /// <summary>
    /// Writes the <paramref name="width"/>×<paramref name="height"/> window of <paramref name="image"/>
    /// starting at (<paramref name="srcX"/>, <paramref name="srcY"/>) into three planes of
    /// <paramref name="destination"/>: plane <c>k</c> starts at <c>k * planeSize</c> and each row is
    /// <paramref name="destStride"/> wide, so destination pixel <c>(x, y)</c> sits at
    /// <c>k * planeSize + y * destStride + x</c>. Plane 0 receives <paramref name="lut0"/> of R (or B when
    /// <paramref name="bgr"/>), plane 1 <paramref name="lut1"/> of G, plane 2 <paramref name="lut2"/> of B
    /// (or R when <paramref name="bgr"/>). Destination cells outside the window are left untouched.
    /// </summary>
    public static void Pack(
        Image<Rgb24> image, int srcX, int srcY, int width, int height,
        Memory<float> destination, int destStride, int planeSize,
        float[] lut0, float[] lut1, float[] lut2, bool bgr)
    {
        if (width <= 0 || height <= 0) return;

        image.DangerousTryGetSinglePixelMemory(out Memory<Rgb24> pixels);
        int imageWidth = image.Width;

        if (width * height < ParallelPixelThreshold || Environment.ProcessorCount <= 1)
        {
            for (int y = 0; y < height; y++)
            {
                PackRow(pixels.Span, imageWidth, srcX, srcY + y, width, destination.Span, y * destStride, planeSize, lut0, lut1, lut2, bgr);
            }
            return;
        }

        Parallel.For(0, height, y =>
            PackRow(pixels.Span, imageWidth, srcX, srcY + y, width, destination.Span, y * destStride, planeSize, lut0, lut1, lut2, bgr));
    }

    private static void PackRow(
        Span<Rgb24> pixels, int imageWidth, int srcX, int srcRow, int width,
        Span<float> dest, int rowOffset, int planeSize,
        float[] lut0, float[] lut1, float[] lut2, bool bgr)
    {
        var row = pixels.Slice(srcRow * imageWidth + srcX, width);
        var plane0 = dest.Slice(rowOffset, width);
        var plane1 = dest.Slice(planeSize + rowOffset, width);
        var plane2 = dest.Slice(2 * planeSize + rowOffset, width);

        if (bgr)
        {
            for (int x = 0; x < row.Length; x++)
            {
                var px = row[x];
                plane0[x] = lut0[px.B];
                plane1[x] = lut1[px.G];
                plane2[x] = lut2[px.R];
            }
        }
        else
        {
            for (int x = 0; x < row.Length; x++)
            {
                var px = row[x];
                plane0[x] = lut0[px.R];
                plane1[x] = lut1[px.G];
                plane2[x] = lut2[px.B];
            }
        }
    }
}
