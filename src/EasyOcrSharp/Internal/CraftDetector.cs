using EasyOcrSharp.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Runs EasyOCR's CRAFT text-detection model and converts the output heatmaps to
/// quadrilateral text-region boxes in original-image coordinates.
/// </summary>
internal sealed class CraftDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;

    public CraftDetector(string modelPath, SessionOptions? sessionOptions = null)
    {
        _session = new InferenceSession(modelPath, sessionOptions ?? new SessionOptions());
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    /// <summary>
    /// Returns one polygon per detected text region using the supplied <see cref="DetectionOptions"/>. With
    /// none of the opt-in passes enabled (<see cref="DetectionOptions.TileLargeImages"/>,
    /// <see cref="DetectionOptions.MinTextHeight"/>, <see cref="DetectionOptions.EnhanceContrast"/>) this is
    /// exactly EasyOCR's single pass; otherwise see <see cref="DetectWithPasses"/>. Polygons are always in
    /// <paramref name="image"/>'s coordinates.
    /// </summary>
    public IReadOnlyList<OcrPoint[]> Detect(Image<Rgb24> image, DetectionOptions options, CancellationToken cancellationToken = default)
        => options.UsesExtraPasses
            ? DetectWithPasses(image, options, cancellationToken)
            : DetectOnce(image, options, options.MagRatio);

    /// <summary>One EasyOCR detection pass at <paramref name="magRatio"/> with the other thresholds from <paramref name="options"/>.</summary>
    private IReadOnlyList<OcrPoint[]> DetectOnce(Image<Rgb24> image, DetectionOptions options, double magRatio)
        => Detect(image, options.CanvasSize, magRatio, options.TextThreshold,
                  options.LinkThreshold, options.LowText, options.MinSize);

    /// <summary>
    /// The opt-in passes. The contrast-enhanced copy (when requested) feeds every pass but only the detector —
    /// it is a per-pixel transform, so coordinates are unchanged by it. A page the canvas cap would shrink
    /// substantially is detected in tiles when tiling is on (the small-text rescue does not apply to it: the
    /// tiles already run at full magnification). Otherwise one normal pass runs, followed by a magnified
    /// re-detection when the typical text is shorter than <see cref="DetectionOptions.MinTextHeight"/>, whose
    /// boxes replace the first pass's unless it found nothing.
    /// </summary>
    private IReadOnlyList<OcrPoint[]> DetectWithPasses(Image<Rgb24> image, DetectionOptions options, CancellationToken cancellationToken)
    {
        using Image<Rgb24>? enhanced = options.EnhanceContrast ? CreateContrastEnhancedCopy(image) : null;
        var source = enhanced ?? image;

        if (options.TileLargeImages
            && DetectionTiling.ShouldTile(source.Width, source.Height, options.CanvasSize, options.MagRatio, out int tileLength))
        {
            return DetectTiled(source, options, tileLength, cancellationToken);
        }

        var polygons = DetectOnce(source, options, options.MagRatio);
        if (options.MinTextHeight <= 0) return polygons;

        int longSide = Math.Max(source.Width, source.Height);
        double magnification = polygons.Count == 0
            ? DetectionTiling.EmptyRetryMagnification(longSide, options.CanvasSize, options.MagRatio)
            : DetectionTiling.RescueMagnification(
                DetectionTiling.MedianShortSide(polygons), options.MinTextHeight, longSide, options.CanvasSize, options.MagRatio);
        if (magnification <= 0) return polygons;

        cancellationToken.ThrowIfCancellationRequested();
        var rescued = DetectOnce(source, options, magnification);
        return rescued.Count > 0 ? rescued : polygons;
    }

    /// <summary>
    /// <see cref="DetectionOptions.TileLargeImages"/>: detects each tile of the grid at full magnification,
    /// shifts its boxes into page coordinates and merges the overlaps (<see cref="DetectionTiling.Merge"/>).
    /// </summary>
    private IReadOnlyList<OcrPoint[]> DetectTiled(Image<Rgb24> image, DetectionOptions options, int tileLength, CancellationToken cancellationToken)
    {
        var tiles = DetectionTiling.PlanGrid(image.Width, image.Height, tileLength, DetectionTiling.TileOverlap);
        var candidates = new List<DetectionTiling.TileCandidate>();
        for (int t = 0; t < tiles.Count; t++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tile = tiles[t];
            using var crop = image.Clone(ctx => ctx.Crop(new Rectangle(tile.X, tile.Y, tile.Width, tile.Height)));
            foreach (var polygon in DetectOnce(crop, options, options.MagRatio))
            {
                bool touchesCut = DetectionTiling.TouchesCut(OcrBoundingBox.FromPoints(polygon), tile);
                candidates.Add(new DetectionTiling.TileCandidate(DetectionTiling.Offset(polygon, tile.X, tile.Y), t, touchesCut));
            }
        }
        return DetectionTiling.Merge(candidates);
    }

    /// <summary>
    /// <see cref="DetectionOptions.EnhanceContrast"/>: a background-normalized (auto radius), 0.5–99.5
    /// percentile contrast-stretched copy of <paramref name="image"/>, same size. The caller owns it.
    /// </summary>
    internal static Image<Rgb24> CreateContrastEnhancedCopy(Image<Rgb24> image)
        => image.Clone(ctx => ctx.BackgroundNormalize(0).ContrastStretch(0.5f, 99.5f));

    /// <summary>
    /// Returns one polygon per detected text region. Each polygon has 4 corners
    /// in clockwise order starting from the visual top-left.
    /// </summary>
    public IReadOnlyList<OcrPoint[]> Detect(
        Image<Rgb24> image,
        int canvasSize = 2560,
        double magRatio = 1.0,
        double textThreshold = 0.7,
        double linkThreshold = 0.4,
        double lowText = 0.4,
        int minSize = 10)
    {
        var pre = ImageProcessing.PreprocessForCraft(image, canvasSize, magRatio);

        var input = new DenseTensor<float>(pre.Tensor, new[] { 1, 3, pre.Height, pre.Width });
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) };

        using var results = _session.Run(inputs);
        // CRAFT exports as shape (1, H/2, W/2, 2) where channel 0 = text, channel 1 = link.
        var output = results.First(o => o.Name == _outputName).AsTensor<float>();

        int hh = output.Dimensions[1];
        int hw = output.Dimensions[2];

        // The output is row-major contiguous with the two channels interleaved (…, text, link, text, …).
        // Drain the buffer in one linear pass instead of the strided 4-D indexer (which recomputes the
        // offset per element); splitting even/odd into the two heatmaps.
        int plane = hh * hw;
        var textMap = new float[plane];
        var linkMap = new float[plane];
        var data = output is DenseTensor<float> dense ? dense.Buffer.Span : output.ToArray();
        for (int p = 0; p < plane; p++)
        {
            textMap[p] = data[p * 2];
            linkMap[p] = data[p * 2 + 1];
        }

        return ExtractBoxes(textMap, linkMap, hw, hh,
            textThreshold, linkThreshold, lowText, minSize,
            pre.HeatmapToSourceX, pre.HeatmapToSourceY,
            image.Width, image.Height);
    }

    /// <summary>
    /// Threshold heatmaps → combined mask → connected components → min-area rect per region.
    /// Matches EasyOCR's craft_utils.getDetBoxes_core flow.
    /// </summary>
    private static List<OcrPoint[]> ExtractBoxes(
        float[] textMap, float[] linkMap, int w, int h,
        double textThr, double linkThr, double lowText, int minSize,
        double scaleX, double scaleY,
        int sourceW, int sourceH)
    {
        var combined = new byte[w * h];
        var textBin = new byte[w * h];
        var linkBin = new byte[w * h];

        for (int i = 0; i < combined.Length; i++)
        {
            textBin[i] = textMap[i] > lowText ? (byte)1 : (byte)0;
            linkBin[i] = linkMap[i] > linkThr ? (byte)1 : (byte)0;
            // union of low-text and link; CC will be filtered later by max textMap >= textThr.
            combined[i] = (textBin[i] | linkBin[i]) != 0 ? (byte)1 : (byte)0;
        }

        var (labels, stats) = ConnectedComponents.Label(combined, w, h);
        var boxes = new List<OcrPoint[]>();

        // stats[0] is background placeholder.
        for (int k = 1; k < stats.Length; k++)
        {
            var s = stats[k];
            if (s.Area < minSize) continue;

            // Require at least one pixel inside the component with textMap >= textThreshold.
            double maxText = 0;
            for (int yy = s.MinY; yy <= s.MaxY; yy++)
            {
                int rowStart = yy * w;
                for (int xx = s.MinX; xx <= s.MaxX; xx++)
                {
                    int idx = rowStart + xx;
                    if (labels[idx] != k) continue;
                    if (textMap[idx] > maxText) maxText = textMap[idx];
                }
            }
            if (maxText < textThr) continue;

            // EasyOCR getDetBoxes_core dilates the segmentation before fitting the box, which
            // recovers the full glyph extent (thin vertical strokes the lowText threshold eroded).
            // Without this, boxes are too tight and clip edge characters (E→=, d→o).
            int boxW = s.MaxX - s.MinX + 1;
            int boxH = s.MaxY - s.MinY + 1;
            int niter = (int)(Math.Sqrt((double)s.Area * Math.Min(boxW, boxH) / (boxW * boxH)) * 2);

            // Local segmap over the component's bbox expanded by niter (EasyOCR's sx/sy/ex/ey window).
            int sx = Math.Max(0, s.MinX - niter);
            int sy = Math.Max(0, s.MinY - niter);
            int ex = Math.Min(w - 1, s.MaxX + niter + 1);
            int ey = Math.Min(h - 1, s.MaxY + niter + 1);
            int lw = ex - sx + 1;
            int lh = ey - sy + 1;

            var seg = new byte[lw * lh];
            for (int yy = s.MinY; yy <= s.MaxY; yy++)
            {
                int rowStart = yy * w;
                for (int xx = s.MinX; xx <= s.MaxX; xx++)
                {
                    int idx = rowStart + xx;
                    if (labels[idx] != k) continue;
                    // Drop link-only pixels (link active AND text inactive) — bridges between regions.
                    if (linkBin[idx] == 1 && textBin[idx] == 0) continue;
                    seg[(yy - sy) * lw + (xx - sx)] = 1;
                }
            }

            if (niter > 0) seg = Dilate(seg, lw, lh, 1 + niter);

            var pts = new List<OcrPoint>(lw * lh);
            for (int yy = 0; yy < lh; yy++)
                for (int xx = 0; xx < lw; xx++)
                    if (seg[yy * lw + xx] != 0)
                        pts.Add(new OcrPoint(xx + sx, yy + sy));
            if (pts.Count < 3) continue;

            var rect = MinAreaRect.Compute(pts.ToArray());

            // Project from heatmap coordinates back to original image coordinates.
            for (int i = 0; i < rect.Length; i++)
            {
                double px = rect[i].X * scaleX;
                double py = rect[i].Y * scaleY;
                // Clamp to image bounds.
                if (px < 0) px = 0;
                if (py < 0) py = 0;
                if (px > sourceW) px = sourceW;
                if (py > sourceH) py = sourceH;
                rect[i] = new OcrPoint(px, py);
            }

            boxes.Add(rect);
        }

        return boxes;
    }

    /// <summary>
    /// Binary morphological dilation with a square structuring element of side <paramref name="ksize"/>,
    /// center-anchored (matching OpenCV's <c>MORPH_RECT</c> default). Separable: dilate along X then Y.
    /// </summary>
    private static byte[] Dilate(byte[] src, int w, int h, int ksize)
    {
        int anchor = ksize / 2;
        var tmp = new byte[src.Length];
        // Horizontal pass.
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                byte v = 0;
                for (int i = 0; i < ksize; i++)
                {
                    int xx = x - anchor + i;
                    if (xx < 0 || xx >= w) continue;
                    if (src[row + xx] != 0) { v = 1; break; }
                }
                tmp[row + x] = v;
            }
        }
        // Vertical pass.
        var dst = new byte[src.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte v = 0;
                for (int i = 0; i < ksize; i++)
                {
                    int yy = y - anchor + i;
                    if (yy < 0 || yy >= h) continue;
                    if (tmp[yy * w + x] != 0) { v = 1; break; }
                }
                dst[y * w + x] = v;
            }
        }
        return dst;
    }

    public void Dispose() => _session.Dispose();
}
