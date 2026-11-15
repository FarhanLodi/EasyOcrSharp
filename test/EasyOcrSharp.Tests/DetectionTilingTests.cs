using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Model-free tests for <see cref="DetectionOptions.TileLargeImages"/>: when tiling triggers, how tiles are
/// planned, and how per-tile word boxes are merged back into one page — including a simulated detector that
/// sees only the part of each word inside a tile, which is exactly what CRAFT returns for a word cut by a seam.
/// </summary>
public class DetectionTilingTests
{
    private const int Canvas = 2560;

    [Fact]
    public void Defaults_leave_every_new_detection_pass_off()
    {
        var d = DetectionOptions.Default;
        Assert.False(d.TileLargeImages);
        Assert.False(d.EnhanceContrast);
        Assert.Equal(0, d.MinTextHeight);
        Assert.False(d.UsesExtraPasses);
        Assert.True((d with { TileLargeImages = true }).UsesExtraPasses);
        Assert.True((d with { EnhanceContrast = true }).UsesExtraPasses);
        Assert.True((d with { MinTextHeight = 16 }).UsesExtraPasses);
    }

    [Theory]
    [InlineData(1200, 9600, 1.0, true, 2560)]   // tall receipt: shrunk to 27 %
    [InlineData(2480, 3508, 1.0, true, 2560)]   // A4 @ 300 dpi: 2560 / 3508 = 0.73 < 0.75
    [InlineData(2480, 3300, 1.0, false, 0)]     // 0.776 ≥ 0.75: the shrink is mild, a single pass is fine
    [InlineData(1600, 1000, 1.0, false, 0)]     // fits the canvas
    [InlineData(1500, 1000, 2.0, false, 0)]     // MagRatio 2 asks 3000, cap gives 0.853 of it
    [InlineData(2000, 1000, 2.0, true, 1280)]   // MagRatio 2 asks 4000, cap gives 0.64 -> tiles of 1280 source px
    public void ShouldTile_triggers_only_when_the_canvas_cap_shrinks_substantially(int w, int h, double mag, bool expected, int expectedTile)
    {
        bool tile = DetectionTiling.ShouldTile(w, h, Canvas, mag, out int tileLength);
        Assert.Equal(expected, tile);
        if (expected) Assert.Equal(expectedTile, tileLength);
    }

    [Fact]
    public void ShouldTile_declines_when_a_tile_could_not_hold_two_overlaps()
    {
        // CanvasSize 700 -> tiles of 700 px, not more than 2 × 384: tiling would be all overlap.
        Assert.False(DetectionTiling.ShouldTile(1000, 8000, 700, 1.0, out _));
    }

    [Theory]
    [InlineData(9600, 2560, 384)]
    [InlineData(2561, 2560, 384)]
    [InlineData(7000, 1280, 384)]
    [InlineData(2560, 2560, 384)]
    [InlineData(100, 2560, 384)]
    public void PlanTiles_covers_the_span_with_at_least_the_overlap(int length, int tileLength, int overlap)
    {
        var tiles = DetectionTiling.PlanTiles(length, tileLength, overlap);

        Assert.Equal(0, tiles[0].Start);
        Assert.Equal(length, tiles[^1].Start + tiles[^1].Length);
        Assert.All(tiles, t => Assert.True(t.Length <= tileLength && t.Length > 0));
        for (int i = 1; i < tiles.Count; i++)
        {
            int shared = tiles[i - 1].Start + tiles[i - 1].Length - tiles[i].Start;
            Assert.True(shared >= overlap, $"tiles {i - 1}/{i} share only {shared}px");
            Assert.True(tiles[i].Start > tiles[i - 1].Start);
        }
    }

    [Fact]
    public void PlanGrid_uses_strips_for_a_tall_receipt_and_marks_only_the_cut_sides()
    {
        var tiles = DetectionTiling.PlanGrid(1200, 9600, 2560, DetectionTiling.TileOverlap);

        Assert.Equal(5, tiles.Count);
        Assert.All(tiles, t => Assert.Equal(1200, t.Width));
        Assert.All(tiles, t => Assert.False(t.CutLeft || t.CutRight));
        Assert.False(tiles[0].CutTop);
        Assert.True(tiles[0].CutBottom);
        Assert.True(tiles[^1].CutTop);
        Assert.False(tiles[^1].CutBottom);
    }

    [Fact]
    public void PlanGrid_uses_a_full_grid_when_both_sides_exceed_a_tile()
    {
        var tiles = DetectionTiling.PlanGrid(6000, 5000, 2560, DetectionTiling.TileOverlap);

        Assert.Equal(3 * 3, tiles.Count);
        // Every pixel is inside at least one tile.
        foreach (var (x, y) in new[] { (0, 0), (5999, 4999), (2400, 2600), (5999, 0), (0, 4999) })
        {
            Assert.Contains(tiles, t => x >= t.X && x < t.X + t.Width && y >= t.Y && y < t.Y + t.Height);
        }
    }

    [Fact]
    public void TouchesCut_ignores_page_edges_and_flags_cut_edges()
    {
        var tile = new DetectionTiling.Tile(0, 2176, 1200, 2560, CutLeft: false, CutTop: true, CutRight: false, CutBottom: true);

        Assert.True(DetectionTiling.TouchesCut(new OcrBoundingBox(100, 0, 300, 20), tile));
        Assert.True(DetectionTiling.TouchesCut(new OcrBoundingBox(100, 2540, 300, 2560), tile));
        Assert.False(DetectionTiling.TouchesCut(new OcrBoundingBox(0, 500, 1200, 530), tile));   // page edges only
        Assert.False(DetectionTiling.TouchesCut(new OcrBoundingBox(100, 500, 300, 530), tile));
    }

    [Fact]
    public void Merge_keeps_one_copy_of_a_word_seen_whole_in_both_tiles()
    {
        var word = Rect(100, 2300, 300, 2330);
        var merged = DetectionTiling.Merge(new[]
        {
            new DetectionTiling.TileCandidate(word, 0, false),
            new DetectionTiling.TileCandidate(Rect(101, 2301, 299, 2331), 1, false),
        });

        Assert.Single(merged);
    }

    [Fact]
    public void Merge_drops_the_cut_piece_when_the_other_tile_saw_the_word_whole()
    {
        var merged = DetectionTiling.Merge(new[]
        {
            new DetectionTiling.TileCandidate(Rect(100, 2545, 300, 2560), 0, true),   // top half, cut by tile 0's bottom edge
            new DetectionTiling.TileCandidate(Rect(100, 2545, 300, 2575), 1, false),  // whole in tile 1
        });

        var box = OcrBoundingBox.FromPoints(Assert.Single(merged));
        Assert.Equal(2575, box.MaxY, 3);
    }

    [Fact]
    public void Merge_never_suppresses_overlapping_boxes_from_the_same_tile()
    {
        var merged = DetectionTiling.Merge(new[]
        {
            new DetectionTiling.TileCandidate(Rect(100, 100, 300, 130), 0, false),
            new DetectionTiling.TileCandidate(Rect(110, 102, 290, 128), 0, false),
        });

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_joins_the_two_pieces_of_a_box_larger_than_the_overlap()
    {
        // A 600 px tall glyph crossing the seam between tile 0 [0, 2560) and tile 1 [2176, 4736): neither tile
        // sees it whole, each piece touches its tile's cut.
        var merged = DetectionTiling.Merge(new[]
        {
            new DetectionTiling.TileCandidate(Rect(100, 2000, 500, 2560), 0, true),
            new DetectionTiling.TileCandidate(Rect(100, 2176, 500, 2600), 1, true),
        });

        var box = OcrBoundingBox.FromPoints(Assert.Single(merged));
        Assert.Equal(2000, box.MinY, 3);
        Assert.Equal(2600, box.MaxY, 3);
        Assert.Equal(100, box.MinX, 3);
        Assert.Equal(500, box.MaxX, 3);
    }

    [Fact]
    public void Merge_returns_boxes_top_to_bottom_then_left_to_right()
    {
        var merged = DetectionTiling.Merge(new[]
        {
            new DetectionTiling.TileCandidate(Rect(500, 900, 600, 920), 1, false),
            new DetectionTiling.TileCandidate(Rect(10, 10, 60, 30), 0, false),
            new DetectionTiling.TileCandidate(Rect(100, 10, 160, 30), 0, false),
        });

        Assert.Equal(new[] { 10.0, 100.0, 500.0 }, merged.Select(p => OcrBoundingBox.FromPoints(p).MinX));
    }

    /// <summary>
    /// End-to-end over the geometry: words laid out down a 1200×9600 page, a simulated detector that returns
    /// each word clipped to every tile it intersects (a cut word comes back as a fragment touching the cut),
    /// then the merge. Every word must come back exactly once, whole.
    /// </summary>
    [Theory]
    [InlineData(1200, 9600)]
    [InlineData(6000, 5000)]
    public void Simulated_tiled_detection_recovers_every_word_exactly_once(int width, int height)
    {
        var words = new List<OcrBoundingBox>();
        for (double y = 20; y + 30 < height; y += 47)
        {
            for (double x = 15; x + 160 < width; x += 233)
            {
                words.Add(new OcrBoundingBox(x, y, x + 160, y + 28));
            }
        }

        Assert.True(DetectionTiling.ShouldTile(width, height, Canvas, 1.0, out int tileLength));
        var tiles = DetectionTiling.PlanGrid(width, height, tileLength, DetectionTiling.TileOverlap);
        var candidates = new List<DetectionTiling.TileCandidate>();
        for (int t = 0; t < tiles.Count; t++)
        {
            var tile = tiles[t];
            foreach (var w in words)
            {
                // What the tile sees of the word, in tile coordinates.
                double minX = Math.Max(w.MinX, tile.X) - tile.X, maxX = Math.Min(w.MaxX, tile.X + tile.Width) - tile.X;
                double minY = Math.Max(w.MinY, tile.Y) - tile.Y, maxY = Math.Min(w.MaxY, tile.Y + tile.Height) - tile.Y;
                if (maxX - minX < 3 || maxY - minY < 3) continue;

                var inTile = new OcrBoundingBox(minX, minY, maxX, maxY);
                candidates.Add(new DetectionTiling.TileCandidate(
                    DetectionTiling.Offset(Rect(minX, minY, maxX, maxY), tile.X, tile.Y), t, DetectionTiling.TouchesCut(inTile, tile)));
            }
        }

        var merged = DetectionTiling.Merge(candidates).Select(OcrBoundingBox.FromPoints).ToList();

        Assert.Equal(words.Count, merged.Count);
        foreach (var w in words)
        {
            Assert.Contains(merged, m => Math.Abs(m.MinX - w.MinX) < 1e-6 && Math.Abs(m.MinY - w.MinY) < 1e-6
                                      && Math.Abs(m.MaxX - w.MaxX) < 1e-6 && Math.Abs(m.MaxY - w.MaxY) < 1e-6);
        }
    }

    [Theory]
    [InlineData(1200, 9600, 1.0)]
    [InlineData(1280, 900, 1.0)]
    [InlineData(4000, 3000, 1.0)]
    [InlineData(600, 200, 2.5)]
    [InlineData(1000, 1000, 3.0)]
    public void DetectionScale_matches_the_craft_preprocessing(int w, int h, double mag)
    {
        using var image = new Image<Rgb24>(w, h, new Rgb24(255, 255, 255));
        var pre = ImageProcessing.PreprocessForCraft(image, Canvas, mag);

        // PreprocessForCraft reports heatmap(=input/2) -> source; the input scale is 2 / that factor.
        double expected = 2.0 / pre.HeatmapToSourceX;
        Assert.Equal(expected, DetectionTiling.DetectionScale(Math.Max(w, h), Canvas, mag), 9);
    }

    internal static OcrPoint[] Rect(double minX, double minY, double maxX, double maxY)
        => new[] { new OcrPoint(minX, minY), new OcrPoint(maxX, minY), new OcrPoint(maxX, maxY), new OcrPoint(minX, maxY) };
}
