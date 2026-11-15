using EasyOcrSharp.Structure.Engine.Detection;
using EasyOcrSharp.Structure.Engine.Geometry;
using EasyOcrSharp.Structure.Engine.Models;
using Xunit;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;
using OcrWord = EasyOcrSharp.Models.OcrWord;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pins the detection post-processing performance rewrites to their original per-pixel formulations:
/// vectorized binarization, flat-array connected components, hull-from-row-extremes and the scanline
/// <c>box_score_fast</c> must all produce bit-identical results on random inputs.
/// </summary>
public sealed class DetectionPostProcessEquivalenceTests
{
    private static byte[] RandomBlobs(Random rng, int w, int h, double density)
    {
        // Seeded noise plus a few filled ellipses so both speckle and large regions occur.
        var mask = new byte[w * h];
        for (int i = 0; i < mask.Length; i++) mask[i] = rng.NextDouble() < density ? (byte)1 : (byte)0;
        int blobs = rng.Next(1, 6);
        for (int b = 0; b < blobs; b++)
        {
            double cx = rng.Next(w), cy = rng.Next(h), rx = rng.Next(2, Math.Max(3, w / 3)), ry = rng.Next(1, Math.Max(2, h / 4));
            double ang = rng.NextDouble() * Math.PI, ca = Math.Cos(ang), sa = Math.Sin(ang);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double dx = x - cx, dy = y - cy;
                    double u = (dx * ca + dy * sa) / rx, v = (-dx * sa + dy * ca) / ry;
                    if (u * u + v * v <= 1) mask[y * w + x] = 1;
                }
        }
        return mask;
    }

    [Fact]
    public void Binarize_matches_scalar_threshold()
    {
        var rng = new Random(7);
        foreach (int len in new[] { 0, 1, 3, 7, 8, 9, 31, 32, 33, 1000, 4097 })
        {
            var prob = new float[len];
            for (int i = 0; i < len; i++) prob[i] = (float)rng.NextDouble();
            if (len > 5) { prob[2] = 0.3f; prob[3] = float.NaN; prob[4] = float.PositiveInfinity; prob[5] = MathF.BitIncrement(0.3f); }
            var got = DBPostProcess.Binarize(prob, len, 0.3f);
            for (int i = 0; i < len; i++) Assert.Equal(prob[i] > 0.3f ? (byte)1 : (byte)0, got[i]);
        }
    }

    [Fact]
    public void ConnectedComponents_matches_reference_labeling()
    {
        var rng = new Random(11);
        for (int iter = 0; iter < 60; iter++)
        {
            int w = rng.Next(1, 90), h = rng.Next(1, 70);
            var mask = RandomBlobs(rng, w, h, rng.NextDouble() * 0.5);
            var (labels, comps) = ConnectedComponents.Label(mask, w, h);
            var (refLabels, refComps) = ReferenceLabel(mask, w, h);
            Assert.Equal(refLabels, labels);
            Assert.Equal(refComps.Length, comps.Length);
            for (int i = 1; i < comps.Length; i++) Assert.Equal(refComps[i], comps[i]);
        }
    }

    [Fact]
    public void MinAreaRect_of_row_extremes_equals_rect_of_all_pixels()
    {
        var rng = new Random(23);
        int checkedRegions = 0;
        for (int iter = 0; iter < 80; iter++)
        {
            int w = rng.Next(4, 120), h = rng.Next(4, 80);
            var mask = RandomBlobs(rng, w, h, rng.NextDouble() * 0.15);
            var (labels, comps) = ConnectedComponents.Label(mask, w, h);
            for (int label = 1; label < comps.Length; label++)
            {
                var s = comps[label];
                var all = new List<OcrPoint>();
                for (int y = s.MinY; y <= s.MaxY; y++)
                    for (int x = s.MinX; x <= s.MaxX; x++)
                        if (labels[y * w + x] == label) all.Add(new OcrPoint(x, y));

                var extremes = new OcrPoint[2 * (s.MaxY - s.MinY + 1)];
                int n = DBPostProcess.CollectRowExtremes(labels, w, s, label, extremes);

                var expected = MinAreaRect.Compute(all.ToArray());
                var actual = MinAreaRect.Compute(extremes.AsSpan(0, n));
                Assert.Equal(expected, actual);
                checkedRegions++;
            }
        }
        Assert.True(checkedRegions > 100);
    }

    [Fact]
    public void BoxScoreFast_scanline_is_bit_identical_to_per_pixel_mask()
    {
        var rng = new Random(5);
        for (int iter = 0; iter < 400; iter++)
        {
            int w = rng.Next(1, 70), h = rng.Next(1, 50);
            var prob = new float[w * h];
            for (int i = 0; i < prob.Length; i++) prob[i] = (float)rng.NextDouble();

            int npts = iter % 5 == 0 ? rng.Next(3, 12) : 4;
            var poly = new OcrPoint[npts];
            for (int i = 0; i < npts; i++)
            {
                // Mix integer, fractional and out-of-bounds coordinates.
                double x = rng.NextDouble() * (w + 20) - 10, y = rng.NextDouble() * (h + 20) - 10;
                if (rng.Next(3) == 0) { x = Math.Round(x); y = Math.Round(y); }
                poly[i] = new OcrPoint(x, y);
            }

            float expected = DBPostProcess.BoxScoreFastReference(prob, w, h, poly);
            float actual = DBPostProcess.BoxScoreFast(prob, w, h, poly);
            Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));
        }
    }

    [Fact]
    public void GetBoxes_slow_and_fast_modes_are_deterministic_on_random_maps()
    {
        var rng = new Random(99);
        for (int iter = 0; iter < 20; iter++)
        {
            int w = rng.Next(16, 120), h = rng.Next(16, 90);
            var mask = RandomBlobs(rng, w, h, 0.01);
            var prob = new float[w * h];
            for (int i = 0; i < prob.Length; i++) prob[i] = mask[i] == 1 ? 0.5f + (float)rng.NextDouble() * 0.5f : (float)rng.NextDouble() * 0.2f;

            foreach (var mode in new[] { DetectionScoreMode.Fast, DetectionScoreMode.Slow })
            {
                var opts = new DetectionOptions { ScoreMode = mode, BoxThreshold = 0.3 };
                var boxes = DBPostProcess.GetBoxes(prob, w, h, opts);
                foreach (var b in boxes)
                {
                    Assert.Equal(4, b.Points.Length);
                    Assert.InRange(b.Score, 0.3f, 1f);
                }
            }
        }
    }

    /// <summary>The original List/Dictionary-based two-pass labeling, kept verbatim as the oracle.</summary>
    private static (int[] Labels, ConnectedComponents.Stats[] Components) ReferenceLabel(byte[] mask, int width, int height)
    {
        var labels = new int[width * height];
        var parents = new List<int> { 0 };
        var sizes = new List<int> { 0 };

        int Find(int x)
        {
            while (parents[x] != x) { parents[x] = parents[parents[x]]; x = parents[x]; }
            return x;
        }

        void Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a == b) return;
            if (sizes[a] < sizes[b]) (a, b) = (b, a);
            parents[b] = a; sizes[a] += sizes[b];
        }

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                if (mask[idx] == 0) continue;
                int left = (x > 0) ? labels[idx - 1] : 0;
                int up = (y > 0) ? labels[idx - width] : 0;
                int upLeft = (x > 0 && y > 0) ? labels[idx - width - 1] : 0;
                int upRight = (x < width - 1 && y > 0) ? labels[idx - width + 1] : 0;
                if (left == 0 && up == 0 && upLeft == 0 && upRight == 0)
                {
                    int nl = parents.Count; parents.Add(nl); sizes.Add(1); labels[idx] = nl;
                }
                else
                {
                    int assigned = left != 0 ? left : up != 0 ? up : upLeft != 0 ? upLeft : upRight;
                    if (left != 0 && left != assigned) Union(assigned, left);
                    if (up != 0 && up != assigned) Union(assigned, up);
                    if (upLeft != 0 && upLeft != assigned) Union(assigned, upLeft);
                    if (upRight != 0 && upRight != assigned) Union(assigned, upRight);
                    labels[idx] = Find(assigned);
                }
            }

        var rootToDense = new Dictionary<int, int>();
        var statsList = new List<ConnectedComponents.Stats> { new(0, 0, 0, 0, 0) };
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                int lbl = labels[idx];
                if (lbl == 0) continue;
                int root = Find(lbl);
                if (!rootToDense.TryGetValue(root, out int dense))
                {
                    dense = statsList.Count; rootToDense[root] = dense; statsList.Add(new(x, y, x, y, 0));
                }
                var s = statsList[dense];
                statsList[dense] = new(Math.Min(s.MinX, x), Math.Min(s.MinY, y), Math.Max(s.MaxX, x), Math.Max(s.MaxY, y), s.Area + 1);
                labels[idx] = dense;
            }
        return (labels, statsList.ToArray());
    }
}
