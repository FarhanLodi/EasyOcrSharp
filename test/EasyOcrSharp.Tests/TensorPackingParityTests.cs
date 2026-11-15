using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyOcrSharp.Internal;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// The model-input packers moved onto EasyImageSharp 1.2.0's <c>WriteTensor</c> / fused <c>ResizeToTensor</c>
/// must produce the <b>exact</b> tensors the previous implementations did — every float compared bit for bit
/// (<see cref="BitConverter.SingleToInt32Bits"/>), not within a tolerance — so detection and recognition see
/// byte-identical inputs and OCR output cannot move. Each previous implementation is kept beside its
/// replacement as a <c>…Reference</c> method and serves as the oracle here.
/// </summary>
/// <remarks>
/// Each packer has its own tests, so a packer whose fused path ever diverges can be rolled back on its own by
/// pointing its production method back at its reference. Inputs vary the size (1×1, odd, extreme aspect,
/// identity, up- and down-scales), the content (seeded noise, hard edges, gradients) and the per-packer knobs.
/// </remarks>
public class TensorPackingParityTests
{
    // ------------------------------------------------------------------ inputs

    /// <summary>
    /// A deterministic page-like image: a smooth gradient background, dark rectangular "glyphs" with hard
    /// edges, and per-pixel noise, so every resampling tap and every byte value of the normalization table
    /// is exercised.
    /// </summary>
    internal static Image<Rgb24> MakeImage(int width, int height, int seed)
    {
        var rng = new Random(seed);
        var image = new Image<Rgb24>(width, height);
        var glyphs = new List<(int X, int Y, int W, int H, byte Ink)>();
        int count = Math.Clamp(width * height / 2000, 1, 4000);
        for (int i = 0; i < count; i++)
        {
            glyphs.Add((rng.Next(width), rng.Next(height), 1 + rng.Next(Math.Clamp(width / 12, 1, 40)),
                1 + rng.Next(Math.Clamp(height / 20, 1, 24)), (byte)rng.Next(0, 90)));
        }

        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    int r = 160 + (x * 80 / Math.Max(1, width)) + rng.Next(-12, 13);
                    int g = 170 + (y * 70 / Math.Max(1, height)) + rng.Next(-12, 13);
                    int b = 200 - (x * 60 / Math.Max(1, width)) + rng.Next(-12, 13);
                    row[x] = new Rgb24(Clamp(r), Clamp(g), Clamp(b));
                }
            }

            foreach (var (gx, gy, gw, gh, ink) in glyphs)
            {
                for (int y = gy; y < Math.Min(height, gy + gh); y++)
                {
                    var row = rows.GetRowSpan(y);
                    for (int x = gx; x < Math.Min(width, gx + gw); x++)
                    {
                        row[x] = new Rgb24(ink, (byte)Math.Min(255, ink + 10), (byte)Math.Min(255, ink + 20));
                    }
                }
            }

            // A few pixels at the extremes so 0 and 255 always reach the tables.
            rows.GetRowSpan(0)[0] = new Rgb24(0, 0, 0);
            rows.GetRowSpan(height - 1)[width - 1] = new Rgb24(255, 255, 255);
        });
        return image;

        static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
    }

    /// <summary>Asserts two float sequences are identical bit for bit, reporting the first mismatch and the worst one.</summary>
    internal static void AssertBitIdentical(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual, string label)
    {
        Assert.True(expected.Length == actual.Length, $"{label}: length {actual.Length} != reference {expected.Length}");
        int mismatches = 0, first = -1;
        double maxAbs = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(expected[i]) == BitConverter.SingleToInt32Bits(actual[i])) continue;
            mismatches++;
            if (first < 0) first = i;
            maxAbs = Math.Max(maxAbs, Math.Abs((double)expected[i] - actual[i]));
        }

        Assert.True(mismatches == 0,
            $"{label}: {mismatches} of {expected.Length} floats differ from the reference; first at {first} " +
            $"(reference {(first >= 0 ? expected[first] : 0):R}, fused {(first >= 0 ? actual[first] : 0):R}); max abs diff {maxAbs:E3}.");
    }

    // ------------------------------------------------------------------ CRAFT detector

    public static TheoryData<int, int, int, double> CraftCases => new()
    {
        { 1, 1, 2560, 1.0 },          // degenerate
        { 3, 7, 2560, 1.0 },          // tiny, odd
        { 31, 33, 2560, 1.0 },        // just around one 32-px tile
        { 640, 480, 2560, 1.0 },      // identity resize (the default for pages under the canvas)
        { 1237, 1749, 2560, 1.0 },    // identity, odd page size
        { 2600, 1800, 2560, 1.0 },    // downscale to the canvas
        { 2200, 2900, 1280, 1.0 },    // heavier downscale, smaller canvas
        { 257, 1023, 2560, 1.5 },     // upscale via MagRatio
        { 400, 300, 2560, 2.37 },     // non-integer upscale
        { 5000, 2, 2560, 1.0 },       // extreme aspect: most of the canvas is padding
        { 2, 3000, 2560, 1.0 },       // extreme aspect, vertical
        { 100, 40, 16, 1.0 },         // canvas clamped up to 32
    };

    [Theory]
    [MemberData(nameof(CraftCases))]
    public void Craft_fused_tensor_is_bit_identical_to_the_reference(int width, int height, int canvas, double mag)
    {
        using var image = MakeImage(width, height, seed: width * 31 + height);

        var expected = ImageProcessing.PreprocessForCraft(image, canvas, mag);
        var actual = ImageProcessing.PreprocessForCraftFused(image, canvas, mag);

        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.HeatmapToSourceX, actual.HeatmapToSourceX);
        Assert.Equal(expected.HeatmapToSourceY, actual.HeatmapToSourceY);
        AssertBitIdentical(expected.Tensor, actual.Tensor, $"CRAFT {width}x{height} canvas {canvas} mag {mag}");
    }

    [Fact]
    public void Craft_fused_tensor_ignores_extra_frames_like_the_reference()
    {
        // A multi-page TIFF reaching the single-image API decodes with all its frames; only the root frame
        // is detected, and the fused path must not read the others.
        using var image = MakeImage(300, 200, seed: 7);
        using var second = MakeImage(300, 200, seed: 8);
        image.Frames.AddFrame(second.Frames.RootFrame);

        var expected = ImageProcessing.PreprocessForCraft(image, 2560, 1.3);
        var actual = ImageProcessing.PreprocessForCraftFused(image, 2560, 1.3);
        AssertBitIdentical(expected.Tensor, actual.Tensor, "CRAFT multi-frame");
    }

    // ------------------------------------------------------------------ CRNN recognizer

    public static TheoryData<int, int, int> CrnnCases => new()
    {
        { 1, 1, 4096 },        // degenerate sliver, edge-padded to the minimum width
        { 3, 40, 4096 },       // narrow: contentW < 16, replicated right edge
        { 10, 64, 4096 },      // exactly 10 px of content, padded
        { 64, 64, 4096 },      // square, identity height
        { 37, 90, 4096 },      // downscale in height
        { 200, 30, 4096 },     // upscale in height (bicubic overshoot near edges)
        { 500, 64, 4096 },     // identity resize
        { 1200, 48, 512 },     // width capped by maxWidth
        { 30, 50, 8 },         // maxWidth below the minimum width
        { 4097, 7, 4096 },     // extreme aspect, capped
    };

    [Theory]
    [MemberData(nameof(CrnnCases))]
    public void Crnn_fused_tensor_is_bit_identical_to_the_reference(int width, int height, int maxWidth)
    {
        using var crop = MakeImage(width, height, seed: width * 7 + height);

        var expected = ImageProcessing.PreprocessForCrnn(crop, 64, maxWidth, adjustContrast: false, out int expectedWidth);
        var actual = ImageProcessing.PreprocessForCrnnFused(crop, 64, maxWidth, adjustContrast: false, out int actualWidth);

        Assert.Equal(expectedWidth, actualWidth);
        AssertBitIdentical(expected, actual, $"CRNN {width}x{height} max {maxWidth}");
    }

    [Theory]
    [InlineData(120, 32)]
    [InlineData(9, 40)]
    public void Crnn_contrast_path_still_matches_the_reference(int width, int height)
    {
        // The contrast stretch edits the grey bytes, so that path keeps the reference implementation.
        using var crop = MakeImage(width, height, seed: 99);
        var expected = ImageProcessing.PreprocessForCrnn(crop, 64, 4096, adjustContrast: true, out int ew, contrastTarget: 0.9);
        var actual = ImageProcessing.PreprocessForCrnnFused(crop, 64, 4096, adjustContrast: true, out int aw, contrastTarget: 0.9);

        Assert.Equal(ew, aw);
        AssertBitIdentical(expected, actual, $"CRNN contrast {width}x{height}");
    }

    [Fact]
    public void Crnn_fused_tensor_matches_on_a_colored_crop()
    {
        // Strongly colored ink: the greyscale must happen before the resize on both paths.
        using var crop = new Image<Rgb24>(90, 33, new Rgb24(250, 240, 10));
        crop.ProcessPixelRows(rows =>
        {
            for (int y = 8; y < 25; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 10; x < 80; x += 3) row[x] = new Rgb24(200, 0, 40);
            }
        });

        var expected = ImageProcessing.PreprocessForCrnn(crop, 64, 4096, false, out int ew);
        var actual = ImageProcessing.PreprocessForCrnnFused(crop, 64, 4096, false, out int aw);
        Assert.Equal(ew, aw);
        AssertBitIdentical(expected, actual, "CRNN colored");
    }

    // ------------------------------------------------------------------ TrOCR handwriting

    [Theory]
    [InlineData(600, 80, 384, 0.5f, 0.5f)]          // the stock processor
    [InlineData(384, 384, 384, 0.5f, 0.5f)]         // identity resize
    [InlineData(7, 3, 384, 0.5f, 0.5f)]             // tiny crop, upscale
    [InlineData(1500, 61, 384, 0.485f, 0.229f)]     // other normalization
    [InlineData(250, 90, 32, 0.5f, 0.5f)]           // small model input
    public void TrOcr_fused_tensor_is_bit_identical_to_the_reference(int width, int height, int size, float mean, float std)
    {
        using var crop = MakeImage(width, height, seed: width + height);

        var expected = TrOcrRecognizer.PreprocessReference(crop, size, mean, std);
        var actual = TrOcrRecognizer.Preprocess(crop, size, mean, std);
        AssertBitIdentical(expected, actual, $"TrOCR {width}x{height} -> {size} ({mean}, {std})");
    }

    [Theory]
    [InlineData(0.5f, -0.5f)]                  // allowed by HandwritingOptions (non-zero), rejected by WriteTensor
    [InlineData(float.NaN, 0.5f)]
    public void TrOcr_falls_back_to_the_reference_for_values_EasyImageSharp_rejects(float mean, float std)
    {
        using var crop = MakeImage(120, 40, seed: 5);

        var expected = TrOcrRecognizer.PreprocessReference(crop, 64, mean, std);
        var actual = TrOcrRecognizer.Preprocess(crop, 64, mean, std);
        AssertBitIdentical(expected, actual, $"TrOCR fallback ({mean}, {std})");
    }

    // ------------------------------------------------------------------ document orientation (PP-LCNet)

    [Theory]
    [InlineData(224, 224)]     // identity
    [InlineData(1700, 2200)]   // a page
    [InlineData(50, 30)]       // upscale
    [InlineData(3001, 997)]    // odd, wide
    public void Orientation_fused_tensor_is_bit_identical_to_the_reference(int width, int height)
    {
        using var image = MakeImage(width, height, seed: width ^ height);

        var expected = DocPreprocessor.BuildOrientationTensorReference(image);
        var actual = DocPreprocessor.BuildOrientationTensor(image);

        Assert.Equal(expected.Dimensions.ToArray(), actual.Dimensions.ToArray());
        AssertBitIdentical(expected.Buffer.Span, actual.Buffer.Span, $"orientation {width}x{height}");
    }

    // ------------------------------------------------------------------ document unwarp (UVDoc)

    [Theory]
    [InlineData(100, 100, DocPreprocessor.UnwarpMaxSide, DocPreprocessor.UnwarpStride)]   // stride padding both ways
    [InlineData(33, 47, DocPreprocessor.UnwarpMaxSide, DocPreprocessor.UnwarpStride)]     // odd, tiny
    [InlineData(64, 96, DocPreprocessor.UnwarpMaxSide, DocPreprocessor.UnwarpStride)]     // already stride-aligned
    [InlineData(1, 1, DocPreprocessor.UnwarpMaxSide, DocPreprocessor.UnwarpStride)]       // degenerate
    [InlineData(1700, 2200, 600, DocPreprocessor.UnwarpStride)]                           // memory cap: resize branch
    [InlineData(901, 333, 500, 32)]                                                       // capped, wide, odd
    [InlineData(400, 300, 400, 7)]                                                        // unusual stride, no resize
    public void Unwarp_tensor_is_bit_identical_to_the_reference(int width, int height, int maxSide, int stride)
    {
        using var image = MakeImage(width, height, seed: width * 3 + height * 5);
        var g = DocPreprocessor.ComputeUnwarpGeometry(width, height, maxSide, stride);

        var expected = DocPreprocessor.BuildUnwarpTensorReference(image, g);
        var actual = DocPreprocessor.BuildUnwarpTensor(image, g);

        Assert.Equal(expected.Dimensions.ToArray(), actual.Dimensions.ToArray());
        AssertBitIdentical(expected.Buffer.Span, actual.Buffer.Span, $"unwarp {width}x{height} cap {maxSide} stride {stride}");
    }
}
