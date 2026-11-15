using EasyImageSharp;
using EasyOcrSharp.Structure.Engine;
using EasyOcrSharp.Structure.Engine.Detection;
using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure.Engine.Services;
using Xunit;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;
using OcrWord = EasyOcrSharp.Models.OcrWord;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Model-free tests for the opt-in detection passes' geometry (<see cref="DetectionTiling"/>) and the
/// new detection/runtime option defaults.
/// </summary>
public sealed class DetectionTilingTests
{
    private static TextQuad Rect(float x, float y, float w, float h, float score = 0.9f)
        => new(new PointF(x, y), new PointF(x + w, y), new PointF(x + w, y + h), new PointF(x, y + h), score);

    [Fact]
    public void New_detection_options_default_off()
    {
        var o = DetectionOptions.Default;
        Assert.Equal(0, o.MinTextHeight);
        Assert.False(o.TileLargeImages);
        Assert.False(o.EnhanceContrast);
    }

    [Theory]
    [InlineData(2000, 3000, false)]  // fits the 4000 cap
    [InlineData(1200, 4800, false)]  // cap scale 0.833 >= 0.75
    [InlineData(1200, 9000, true)]   // tall receipt: cap scale 0.44
    [InlineData(9000, 1200, true)]   // wide
    public void ShouldTile_triggers_only_when_cap_shrinks_below_threshold(int w, int h, bool expected)
    {
        Assert.Equal(expected, DetectionTiling.ShouldTile(w, h, DetectionOptions.Default, out int tileLength));
        if (expected) Assert.Equal(4000, tileLength);
    }

    [Fact]
    public void ShouldTile_never_triggers_for_limit_type_max_downscale()
    {
        var o = new DetectionOptions { LimitSideLen = 960, LimitTypeMax = true };
        Assert.False(DetectionTiling.ShouldTile(1200, 9000, o, out _));
    }

    [Theory]
    [InlineData(3000, 4000)]
    [InlineData(9000, 4000)]
    [InlineData(12345, 4000)]
    public void PlanTiles_covers_the_long_side_with_overlap(int longSide, int tileLength)
    {
        var tiles = DetectionTiling.PlanTiles(longSide, tileLength, DetectionTiling.TileOverlap);
        Assert.Equal(0, tiles[0].Start);
        Assert.Equal(longSide, tiles[^1].Start + tiles[^1].Length);
        for (int i = 0; i < tiles.Count; i++)
        {
            Assert.True(tiles[i].Length <= tileLength);
            if (i > 0)
            {
                int overlap = tiles[i - 1].Start + tiles[i - 1].Length - tiles[i].Start;
                Assert.True(overlap >= DetectionTiling.TileOverlap, $"overlap {overlap}");
            }
        }
    }

    [Fact]
    public void Merge_prefers_quads_clear_of_the_cut_and_suppresses_duplicates()
    {
        var clipped = Rect(10, 3990, 200, 12);   // touches the first tile's cut edge, slightly smaller
        var whole = Rect(10, 3988, 200, 20);     // same line seen whole in the next tile
        var other = Rect(10, 100, 300, 20);
        var merged = DetectionTiling.Merge(new[] { (clipped, true), (whole, false), (other, false) }, 0.5);
        Assert.Equal(2, merged.Count);
        Assert.Contains(whole, merged);
        Assert.Contains(other, merged);
        Assert.DoesNotContain(clipped, merged);
    }

    [Fact]
    public void MedianShortSide_uses_the_shorter_quad_side()
    {
        var quads = new[] { Rect(0, 0, 100, 10), Rect(0, 0, 100, 20), Rect(0, 0, 8, 90) };
        Assert.Equal(10, DetectionTiling.MedianShortSide(quads), 3);
        Assert.Equal(0, DetectionTiling.MedianShortSide(Array.Empty<TextQuad>()));
    }

    [Theory]
    [InlineData(12, 16, 1000, 4000, 2.0)]   // 24 / 12
    [InlineData(4, 16, 1000, 4000, 3.0)]    // capped at 3x
    [InlineData(4, 16, 2000, 4000, 2.0)]    // capped by the side limit
    [InlineData(20, 16, 1000, 4000, 1.0)]   // already tall enough
    [InlineData(12, 0, 1000, 4000, 1.0)]    // feature off
    [InlineData(23.5, 24, 1000, 4000, 1.0)] // below the 5% minimum gain
    public void UpscaleFactor_follows_target_and_caps(double median, int minHeight, int longSide, int limit, double expected)
    {
        Assert.Equal(expected, DetectionTiling.UpscaleFactor(median, minHeight, longSide, limit), 6);
    }

}
