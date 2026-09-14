using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Model-free tests for <see cref="DocPreprocessor"/>: the whole-page doc-orientation input (RGB, ImageNet
/// normalized), the BGR UVDoc plane order, and the UVDoc memory-cap + stride-padding + crop math. The UVDoc
/// fixes mirror PaddleOcrNet 2.1.0; its orientation center crop is deliberately not adopted.
/// </summary>
public class DocPreprocessorGeometryTests
{
    // ---- doc orientation: whole-page stretch to 224 (kept over PaddleX's center crop) -----------------

    [Fact]
    public void Orientation_tensor_is_imagenet_normalized_rgb_planes()
    {
        using var img = new Image<Rgb24>(300, 500, new Rgb24(255, 128, 0));
        var t = DocPreprocessor.BuildOrientationTensor(img);

        Assert.Equal(new[] { 1, 3, 224, 224 }, t.Dimensions.ToArray());
        Assert.Equal((1f - 0.485f) / 0.229f, t[0, 0, 100, 100], 3);          // plane 0 = R
        Assert.Equal((128 / 255f - 0.456f) / 0.224f, t[0, 1, 100, 100], 3);  // plane 1 = G
        Assert.Equal((0f - 0.406f) / 0.225f, t[0, 2, 100, 100], 3);          // plane 2 = B
    }

    [Fact]
    public void Orientation_tensor_sees_the_whole_page_edge_to_edge()
    {
        // 1600x1000: red left fifth, blue right fifth, white middle. The classifier corrects whole-page
        // rotation, so both edge bands must reach the tensor. PaddleX's center crop (x ~363..1237) would drop
        // them; that crop was measured to mis-classify multi-column pages and is deliberately not used.
        using var img = new Image<Rgb24>(1600, 1000, new Rgb24(255, 255, 255));
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < 320; x++) row[x] = new Rgb24(255, 0, 0);
                for (int x = 1280; x < 1600; x++) row[x] = new Rgb24(0, 0, 255);
            }
        });

        var t = DocPreprocessor.BuildOrientationTensor(img);

        float redR = (1f - 0.485f) / 0.229f, redB = (0f - 0.406f) / 0.225f;
        float blueR = (0f - 0.485f) / 0.229f, blueB = (1f - 0.406f) / 0.225f;
        Assert.Equal(redR, t[0, 0, 112, 5], 2);
        Assert.Equal(redB, t[0, 2, 112, 5], 2);
        Assert.Equal(blueR, t[0, 0, 112, 218], 2);
        Assert.Equal(blueB, t[0, 2, 112, 218], 2);
    }

    // ---- UVDoc: memory cap, stride padding, BGR planes, crop -------------------------------------------

    [Theory]
    // width, height, workW, workH, padW, padH, downscaled
    [InlineData(1600, 1000, 1600, 1000, 1600, 1024, false)]
    [InlineData(1200, 1600, 1200, 1600, 1216, 1600, false)]
    [InlineData(2000, 1, 2000, 1, 2016, 32, false)]
    [InlineData(4000, 3000, 2000, 1500, 2016, 1504, true)]
    [InlineData(3000, 4001, 1500, 2000, 1504, 2016, true)]
    [InlineData(33, 1, 33, 1, 64, 32, false)]
    public void Unwarp_geometry_keeps_page_resolution_under_the_cap_and_pads_to_stride(
        int w, int h, int ww, int wh, int pw, int ph, bool downscaled)
    {
        var g = DocPreprocessor.ComputeUnwarpGeometry(w, h);

        Assert.Equal((ww, wh, pw, ph), (g.WorkWidth, g.WorkHeight, g.PaddedWidth, g.PaddedHeight));
        Assert.Equal(downscaled, g.Downscaled(w, h));
        Assert.Equal(0, g.PaddedWidth % DocPreprocessor.UnwarpStride);
        Assert.Equal(0, g.PaddedHeight % DocPreprocessor.UnwarpStride);
        Assert.InRange(g.PaddedWidth - g.WorkWidth, 0, DocPreprocessor.UnwarpStride - 1);
        Assert.InRange(g.PaddedHeight - g.WorkHeight, 0, DocPreprocessor.UnwarpStride - 1);
        Assert.True(Math.Max(g.WorkWidth, g.WorkHeight) <= DocPreprocessor.UnwarpMaxSide);
    }

    [Fact]
    public void Unwarp_tensor_is_bgr_planes_scaled_to_unit_with_edge_replicated_padding()
    {
        using var img = new Image<Rgb24>(3, 2);
        img[0, 0] = new Rgb24(10, 20, 30);
        img[2, 0] = new Rgb24(40, 50, 60);
        img[0, 1] = new Rgb24(70, 80, 90);
        img[2, 1] = new Rgb24(100, 110, 120);

        var g = DocPreprocessor.ComputeUnwarpGeometry(img.Width, img.Height);
        var t = DocPreprocessor.BuildUnwarpTensor(img, g);

        Assert.Equal(new[] { 1, 3, 32, 32 }, t.Dimensions.ToArray());
        // Plane 0 is BLUE, plane 2 is RED — the model's training order.
        Assert.Equal(30 / 255f, t[0, 0, 0, 0], 5);
        Assert.Equal(20 / 255f, t[0, 1, 0, 0], 5);
        Assert.Equal(10 / 255f, t[0, 2, 0, 0], 5);
        // Right padding replicates the last column, bottom padding the last row, corner the last pixel.
        Assert.Equal(60 / 255f, t[0, 0, 0, 31], 5);
        Assert.Equal(90 / 255f, t[0, 0, 31, 0], 5);
        Assert.Equal(120 / 255f, t[0, 0, 31, 31], 5);
        Assert.Equal(100 / 255f, t[0, 2, 31, 31], 5);
    }

    [Fact]
    public void Unwarp_tensor_downscales_only_when_the_cap_applies()
    {
        using var img = new Image<Rgb24>(4000, 100, new Rgb24(0, 0, 255));
        var g = DocPreprocessor.ComputeUnwarpGeometry(img.Width, img.Height);
        var t = DocPreprocessor.BuildUnwarpTensor(img, g);

        Assert.Equal(new[] { 1, 3, g.PaddedHeight, g.PaddedWidth }, t.Dimensions.ToArray());
        Assert.Equal(1f, t[0, 0, 10, 10], 3);  // pure blue lands in plane 0
        Assert.Equal(0f, t[0, 2, 10, 10], 3);
    }

    [Fact]
    public void Unwarp_output_decode_flips_bgr_to_rgb_and_crops_the_padding()
    {
        const int w = 5, h = 4;
        var output = new DenseTensor<float>(new[] { 1, 3, h, w });
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                output[0, 0, y, x] = 1f;    // B
                output[0, 1, y, x] = 0.5f;  // G
                output[0, 2, y, x] = 0f;    // R
            }
        }
        output[0, 2, 1, 2] = 2f;  // out-of-range red is clamped
        output[0, 0, 1, 2] = -1f; // negative blue is clamped

        using var img = DocPreprocessor.DecodeUnwarpOutput(output, w, h, cropW: 3, cropH: 2);

        Assert.Equal(3, img.Width);
        Assert.Equal(2, img.Height);
        Assert.Equal(new Rgb24(0, 128, 255), img[0, 0]);
        Assert.Equal(new Rgb24(255, 128, 0), img[2, 1]);
    }

    // ---- orientation mapback (DetectOrientation sweep) vs PaddleOcrNet's OrientationMapper vectors -----

    [Fact]
    public void MapBack_180_matches_PaddleOcrNet_orientation_mapper_vector()
    {
        // PaddleOcrNet DocOrientationMapbackTests.MapLinesToOriginalFrame_180_restores_original_quad.
        var quad = new[] { new OcrPoint(10, 20), new OcrPoint(60, 20), new OcrPoint(60, 40), new OcrPoint(10, 40) };
        var line = new OcrLine { Text = "hello", BoundingPolygon = quad, BoundingBox = OcrBoundingBox.FromPoints(quad) };

        var mapped = EasyOcrService.MapLinesToOriginalOrientation(new[] { line }, 180, 200, 100)[0];

        Assert.Equal(new[] { new OcrPoint(190, 80), new OcrPoint(140, 80), new OcrPoint(140, 60), new OcrPoint(190, 60) },
            mapped.BoundingPolygon.ToArray());
    }

    [Theory]
    // applied CW rotation, working point (x,y), expected original-frame point — from PaddleOcrNet's
    // OrientationMapper: inverse = RotatePoint(p, 360 - applied) over the 200x300 working frame of a 300x200 page.
    [InlineData(90, 15, 25, 25, 185)]
    [InlineData(270, 15, 25, 275, 15)]
    public void MapBack_90_and_270_match_PaddleOcrNet_orientation_mapper(int applied, double x, double y, double ex, double ey)
    {
        var quad = new[] { new OcrPoint(x, y), new OcrPoint(x, y), new OcrPoint(x, y), new OcrPoint(x, y) };
        var line = new OcrLine { Text = "q", BoundingPolygon = quad, BoundingBox = OcrBoundingBox.FromPoints(quad) };

        var p = EasyOcrService.MapLinesToOriginalOrientation(new[] { line }, applied, 300, 200)[0].BoundingPolygon[0];

        Assert.Equal(ex, p.X, 9);
        Assert.Equal(ey, p.Y, 9);
    }

    // ---- real model (skips unless PP-LCNet_x1_0_doc_ori is already in the default cache) ---------------

    [SkippableFact]
    public void Real_doc_ori_model_uprights_a_rotated_sample_page()
    {
        var page = TestAssets.Image("sample.png");
        Skip.If(page is null, "assets/sample.png not present.");
        var model = Path.Combine(ModelDownloadManager.ModelCacheRootPath, DocPreprocessModelRegistry.DocOrientationClassifier.FileName);
        Skip.IfNot(File.Exists(model), "PP-LCNet_x1_0_doc_ori.onnx is not in the model cache (this test never downloads).");

        using var session = new InferenceSession(model);
        var pre = new DocPreprocessor(session, unwarp: null);
        using var img = Image.Load<Rgb24>(page!);

        foreach (var (mode, degrees) in new[] { (RotateMode.None, 0), (RotateMode.Rotate90, 90), (RotateMode.Rotate180, 180), (RotateMode.Rotate270, 270) })
        {
            using var rotated = img.Clone(c => c.Rotate(mode));
            var (fixedPage, applied) = pre.Apply(rotated, useOrientation: true, useUnwarp: false);
            using (fixedPage)
            {
                Assert.Equal((360 - degrees) % 360, applied);
                Assert.Equal((img.Width, img.Height), (fixedPage.Width, fixedPage.Height));
            }
        }
    }

    [Fact]
    public void Apply_observes_cancellation_before_any_work()
    {
        using var img = new Image<Rgb24>(10, 10);
        using var pre = new DocPreprocessor(orientation: null, unwarp: null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => pre.Apply(img, true, true, cts.Token));
    }
}
