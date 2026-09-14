using EasyOcrSharp.Structure.Engine.Detection;
using EasyOcrSharp.Structure.Seal;
using Xunit;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model download, CI-safe) pinning the <c>DetResizeForTest</c> math against
/// PaddleX <c>text_detection/processors.py resize_image_type0</c>: int-truncation of the scaled
/// dimensions BEFORE the round-to-32, the <c>max_side_limit</c> (4000) cap between those two steps, the
/// <c>limit_type=min</c> upscale-only policy, and the seal pipeline's dedicated <c>min=736</c> resize.
/// </summary>
public class DetResizeTests
{
    // ---- DbTextDetector.ComputeResize (main det path) ---------------------------------------------

    [Fact]
    public void Min_policy_keeps_native_resolution_when_short_side_is_large_enough()
    {
        // 800x600, limit min=64: no scaling; 600 rounds to the nearest multiple of 32 (18.75 -> 19).
        var (w, h) = DbTextDetector.ComputeResize(800, 600, 64, limitTypeMax: false, maxSideLimit: 4000);
        Assert.Equal((800, 608), (w, h));
    }

    [Fact]
    public void Min_policy_upscales_only_when_short_side_is_below_the_limit()
    {
        // 100x40, limit min=64: ratio 64/40 = 1.6 -> int(160) x int(64), already multiples of 32.
        var (w, h) = DbTextDetector.ComputeResize(100, 40, 64, limitTypeMax: false, maxSideLimit: 4000);
        Assert.Equal((160, 64), (w, h));
    }

    [Fact]
    public void Tiny_image_is_brought_up_to_the_32px_floor()
    {
        var (w, h) = DbTextDetector.ComputeResize(10, 10, 64, limitTypeMax: false, maxSideLimit: 4000);
        Assert.Equal((64, 64), (w, h));
    }

    [Fact]
    public void MaxSideLimit_caps_the_long_side_after_the_min_policy()
    {
        // 8000x1000 stays native under min=64, then the 4000 cap halves it: 4000 x int(500) -> 500
        // rounds to 512.
        var (w, h) = DbTextDetector.ComputeResize(8000, 1000, 64, limitTypeMax: false, maxSideLimit: 4000);
        Assert.Equal((4000, 512), (w, h));
    }

    [Fact]
    public void MaxSideLimit_non_positive_falls_back_to_4000()
    {
        var (w, h) = DbTextDetector.ComputeResize(8000, 1000, 64, limitTypeMax: false, maxSideLimit: 0);
        Assert.Equal((4000, 512), (w, h));
    }

    [Fact]
    public void Max_policy_downscales_the_long_side_to_the_limit()
    {
        // Legacy limit_type=max at 960: 1920x1080 -> ratio 0.5 -> 960 x int(540); 540 rounds to 544.
        var (w, h) = DbTextDetector.ComputeResize(1920, 1080, 960, limitTypeMax: true, maxSideLimit: 4000);
        Assert.Equal((960, 544), (w, h));
    }

    [Fact]
    public void Resized_dimensions_never_exceed_the_cap_and_stay_multiples_of_32()
    {
        foreach (var (srcW, srcH) in new[] { (33, 5000), (12000, 250), (4001, 4001), (640, 480) })
        {
            var (w, h) = DbTextDetector.ComputeResize(srcW, srcH, 64, limitTypeMax: false, maxSideLimit: 4000);
            Assert.True(w <= 4000 && h <= 4000, $"{srcW}x{srcH} resized to {w}x{h}, above the 4000 cap");
            Assert.True(w % 32 == 0 && h % 32 == 0, $"{srcW}x{srcH} resized to {w}x{h}, not multiples of 32");
            Assert.True(w >= 32 && h >= 32);
        }
    }

    // ---- SealRecognizer.ComputeResize (limit_type=min at 736) -------------------------------------

    [Fact]
    public void Seal_resize_upscales_the_short_side_to_736()
    {
        // 500x400: ratio 736/400 = 1.84 -> int(920) x int(736); 920 rounds to 928, 736 stays.
        var (w, h, ratioW, ratioH) = SealRecognizer.ComputeResize(500, 400, 736);

        Assert.Equal(928, w);
        Assert.Equal(736, h);
        Assert.Equal(928 / 500.0, ratioW, 12);
        Assert.Equal(736 / 400.0, ratioH, 12);
    }

    [Fact]
    public void Seal_resize_never_downscales_when_short_side_already_clears_736()
    {
        // 800x900 keeps its scale; only the round-to-32 nudges the sides (900 -> 896).
        var (w, h, _, _) = SealRecognizer.ComputeResize(800, 900, 736);
        Assert.Equal((800, 896), (w, h));
    }

    [Fact]
    public void Seal_resize_caps_the_long_side_at_4000()
    {
        // 1000x8000: min side already >= 736, cap halves to int(500) x 4000; 500 rounds to 512.
        var (w, h, _, _) = SealRecognizer.ComputeResize(1000, 8000, 736);
        Assert.Equal((512, 4000), (w, h));
    }
}
