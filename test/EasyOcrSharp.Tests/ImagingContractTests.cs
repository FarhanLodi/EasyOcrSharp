using EasyOcrSharp.Internal;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Pins the exact imaging behaviour the OCR pipeline is built on. These are not tests of the imaging
/// library for its own sake: every assertion here corresponds to an inverse transform, a tensor layout
/// or a resource guard implemented elsewhere in this repository, so if the imaging contract ever moves
/// these fail before the accuracy does.
/// <list type="bullet">
/// <item><c>Rotate</c> turns clockwise, expands the canvas to the rotated bounding box and fills the
/// exposed area with transparent black — inverted by <see cref="PatchTransform"/> (per-box rotation) and
/// by <c>EasyOcrService.MapLinesToOriginalOrientation</c> (page orientation sweep).</item>
/// <item><c>Resize(w, 0)</c> derives the missing dimension from the aspect ratio — relied on by the
/// deskew downscale and the CRAFT canvas fit.</item>
/// <item><c>Grayscale()</c> writes the grey level to R, G and B — <c>ImageProcessing.PreprocessForCrnn</c>
/// reads only the red channel of the greyscaled crop.</item>
/// <item><c>Identify</c> reads headers without decoding pixels — the decompression-bomb guard depends on
/// learning the dimensions without paying for them.</item>
/// </list>
/// </summary>
public class ImagingContractTests
{
    private static readonly Rgb24 White = new(255, 255, 255);
    private static readonly Rgba32 OpaqueWhite = new(255, 255, 255, 255);
    private static readonly Rgba32 Marker = new(0, 0, 0, 255);

    /// <summary>
    /// A white canvas with one black marker pixel. Alpha-bearing on purpose: an arbitrary-angle rotation
    /// fills the exposed corners with transparent BLACK, which in an opaque format is indistinguishable
    /// from the marker — carrying alpha lets <see cref="DarkestPixel"/> ignore the fill.
    /// </summary>
    private static Image<Rgba32> WhiteWithMarker(int width, int height, int markerX, int markerY)
    {
        var image = new Image<Rgba32>(width, height, OpaqueWhite);
        image[markerX, markerY] = Marker;
        return image;
    }

    // ---- Rotate: right angles ----

    /// <summary>
    /// The right-angle rotations must map pixels exactly the way <c>MapLinesToOriginalOrientation</c>
    /// assumes when it maps detections from the rotated frame back onto the caller's image.
    /// <c>OrientationMappingTests</c> asserts that mapping is a self-consistent inverse; this test is what
    /// ties it to the pixels the detector actually saw.
    /// </summary>
    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Right_angle_rotation_moves_pixels_where_the_orientation_inverse_expects(int degrees)
    {
        const int W = 40, H = 25;
        const int MarkerX = 7, MarkerY = 3;

        using var source = WhiteWithMarker(W, H, MarkerX, MarkerY);
        using var rotated = source.Clone(c => c.Rotate(degrees));

        // Same forward transform OrientationMappingTests models, in discrete pixel coordinates.
        var (expectedX, expectedY) = degrees switch
        {
            90 => (H - 1 - MarkerY, MarkerX),
            180 => (W - 1 - MarkerX, H - 1 - MarkerY),
            _ => (MarkerY, W - 1 - MarkerX),
        };

        Assert.Equal(degrees == 180 ? W : H, rotated.Width);
        Assert.Equal(degrees == 180 ? H : W, rotated.Height);
        Assert.Equal(Marker, rotated[expectedX, expectedY]);
    }

    // ---- Rotate: arbitrary angles ----

    [Fact]
    public void Arbitrary_rotation_expands_the_canvas_to_the_rotated_bounding_box()
    {
        const int W = 100, H = 40;
        const float Degrees = 30f;

        using var source = new Image<Rgb24>(W, H, White);
        using var rotated = source.Clone(c => c.Rotate(Degrees));

        double radians = Degrees * Math.PI / 180.0;
        double cos = Math.Abs(Math.Cos(radians)), sin = Math.Abs(Math.Sin(radians));
        Assert.Equal((int)Math.Ceiling((W * cos) + (H * sin)), rotated.Width);
        Assert.Equal((int)Math.Ceiling((W * sin) + (H * cos)), rotated.Height);
    }

    /// <summary>
    /// <c>ImagePreprocessor</c> composites the rotated copy over a white canvas precisely because the
    /// corners come back transparent rather than white; if they came back opaque black the composite would
    /// paint black triangles into every deskewed page.
    /// </summary>
    [Fact]
    public void Arbitrary_rotation_fills_the_exposed_corners_with_transparent_black()
    {
        using var source = new Image<Rgba32>(60, 60, new Rgba32(10, 200, 30, 255));
        using var rotated = source.Clone(c => c.Rotate(20f));

        var corner = rotated[0, 0];
        Assert.Equal(0, corner.A);
        Assert.Equal(0, corner.R);
        Assert.Equal(0, corner.G);
        Assert.Equal(0, corner.B);

        // The centre still carries the original colour, so the fill really is only the exposed area.
        var centre = rotated[rotated.Width / 2, rotated.Height / 2];
        Assert.Equal(255, centre.A);
    }

    [Fact]
    public void Arbitrary_rotation_turns_clockwise_in_image_coordinates()
    {
        // A marker on the mid-right of the canvas must swing downwards for a clockwise turn (y grows down).
        const int Size = 81;
        using var source = WhiteWithMarker(Size, Size, Size - 6, Size / 2);
        using var rotated = source.Clone(c => c.Rotate(45f));

        var (x, y) = DarkestPixel(rotated);
        Assert.True(x > rotated.Width / 2, $"expected the marker right of centre, found x={x}");
        Assert.True(y > (rotated.Height / 2) + 5, $"expected the marker below centre, found y={y}");
    }

    /// <summary>
    /// The per-box recognizer rotation is undone analytically by <see cref="PatchTransform"/> rather than
    /// by rotating pixels back, so its matrix has to match what the rotation actually did. Rotate a crop
    /// with a known marker, feed the marker's position in the rotated patch through the inverse, and it
    /// must land back on the marker in the crop.
    /// </summary>
    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(-25)]
    [InlineData(90)]
    public void PatchTransform_inverts_the_rotation_the_recognizer_applied(double degrees)
    {
        const int CropW = 61, CropH = 31;
        const int MarkerX = 48, MarkerY = 9;

        using var crop = WhiteWithMarker(CropW, CropH, MarkerX, MarkerY);
        using var patch = crop.Clone(c => c.Rotate((float)degrees));

        var (px, py) = DarkestPixel(patch);
        var transform = PatchTransform.Rotated(CropW, CropH, patch.Width, patch.Height, degrees);
        var (u, v) = transform.ToCrop((px + 0.5) / patch.Width, (py + 0.5) / patch.Height);

        // One pixel of slack for the marker's bilinear smear plus the half-pixel sampling convention.
        Assert.InRange(u * CropW, MarkerX + 0.5 - 1.5, MarkerX + 0.5 + 1.5);
        Assert.InRange(v * CropH, MarkerY + 0.5 - 1.5, MarkerY + 0.5 + 1.5);
    }

    // ---- Resize ----

    [Theory]
    [InlineData(800, 0)]
    [InlineData(0, 300)]
    public void A_zero_dimension_is_derived_from_the_aspect_ratio(int width, int height)
    {
        const int W = 1234, H = 567;
        using var source = new Image<Rgb24>(W, H, White);
        using var resized = source.Clone(c => c.Resize(width, height));

        int expectedW = width > 0 ? width : (int)Math.Round((double)W * height / H);
        int expectedH = height > 0 ? height : (int)Math.Round((double)H * width / W);
        Assert.Equal(expectedW, resized.Width);
        Assert.Equal(expectedH, resized.Height);

        // The aspect ratio survives to within the rounding of a single pixel.
        Assert.InRange(Math.Abs(((double)resized.Width / resized.Height) - ((double)W / H)), 0, 0.01);
    }

    // ---- Grayscale ----

    /// <summary>
    /// <c>ImageProcessing.PreprocessForCrnn</c> greyscales the crop and then reads <c>row[x].R</c> only, so
    /// the grey level has to be present in the red channel — and the three channels must agree, or the
    /// recognizer would see a red-channel image instead of a luminance image.
    /// </summary>
    [Fact]
    public void Grayscale_writes_the_same_level_to_r_g_and_b()
    {
        using var image = new Image<Rgb24>(8, 4, new Rgb24(200, 60, 10));
        image[0, 0] = new Rgb24(0, 0, 0);
        image[1, 0] = new Rgb24(255, 255, 255);
        image[2, 0] = new Rgb24(12, 240, 130);

        image.Mutate(c => c.Grayscale());

        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    Assert.Equal(row[x].R, row[x].G);
                    Assert.Equal(row[x].R, row[x].B);
                }
            }
        });

        Assert.Equal(0, image[0, 0].R);
        Assert.Equal(255, image[1, 0].R);

        // BT.709 luminance of the mixed pixel, which is what the tensor normalization assumes.
        int expected = (int)((12 * 0.2126f) + (240 * 0.7152f) + (130 * 0.0722f) + 0.5f);
        Assert.InRange(image[2, 0].R, expected - 1, expected + 1);
    }

    // ---- Identify ----

    /// <summary>
    /// The decompression-bomb guard calls Identify first and only then decodes. That is only a guard if
    /// Identify never inflates the pixel data — so a file whose pixel stream is truncated must still report
    /// its declared dimensions, while decoding it fails.
    /// </summary>
    [Fact]
    public void Identify_reads_the_header_without_decoding_the_pixels()
    {
        using var source = new Image<Rgb24>(640, 480, new Rgb24(3, 5, 7));
        using var buffer = new MemoryStream();
        source.SaveAsPng(buffer);
        byte[] full = buffer.ToArray();

        // Keep the signature and IHDR (8 + 25 bytes) and only the first few bytes of the pixel stream.
        byte[] truncated = full.AsSpan(0, 8 + 25 + 20).ToArray();

        var info = Image.Identify(truncated);
        Assert.Equal(640, info.Width);
        Assert.Equal(480, info.Height);
        Assert.Equal(1, info.FrameCount);

        Assert.ThrowsAny<Exception>(() => Image.Load<Rgb24>(truncated));
    }

    // ---- helpers ----

    /// <summary>The darkest fully-opaque pixel, i.e. ignoring the transparent fill a rotation leaves behind.</summary>
    private static (int X, int Y) DarkestPixel(Image<Rgba32> image)
    {
        int bestX = 0, bestY = 0, best = int.MaxValue;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                var rgba = image[x, y];
                if (rgba.A < 250) continue;
                int level = rgba.R + rgba.G + rgba.B;
                if (level < best)
                {
                    best = level;
                    bestX = x;
                    bestY = y;
                }
            }
        }
        return (bestX, bestY);
    }
}
