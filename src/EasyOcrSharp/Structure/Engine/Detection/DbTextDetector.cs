using EasyOcrSharp.Structure.Engine.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Engine.Detection;

/// <summary>
/// PaddleOCR DB / DBNet text detector. Resizes the image to a multiple of 32 per
/// <see cref="DetectionOptions.LimitSideLen"/> / <see cref="DetectionOptions.MaxSideLimit"/>, runs the
/// segmentation model to get a per-pixel text probability map, binarizes it, extracts contours, computes
/// each contour's min-area rectangle, scores it against the probability map, and "unclips" (expands) the
/// polygon by <see cref="DetectionOptions.UnclipRatio"/> back to original-image coordinates.
/// <para>
/// Pre-processing matches PaddleOCR's <c>DetResizeForTest</c> (limit_type max or min, per
/// <see cref="DetectionOptions.LimitTypeMax"/>, longest side capped at
/// <see cref="DetectionOptions.MaxSideLimit"/>, tiny inputs zero-padded to ≥32×32) + ImageNet
/// normalization over BGR planes (the exported model's <c>DecodeImage img_mode</c> is BGR);
/// post-processing is delegated to <see cref="DBPostProcess"/>. Polygon unclip uses Clipper2
/// (Clipper2Lib). Reference: RapidOcrNet <c>TextDetector.cs</c> (Apache-2.0) and OnnxOCR
/// <c>db_postprocess.py</c>.
/// </para>
/// </summary>
internal sealed class DbTextDetector : ITextDetector
{
    // ImageNet mean/std (PaddleOCR det NormalizeImage), applied to pixel/255 in B,G,R plane order —
    // the mean/std index order stays [0.485,0.456,0.406]/[0.229,0.224,0.225] exactly as in Python,
    // where the image is already BGR when NormalizeImage runs.
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;

    /// <summary>
    /// Creates the detector over an already-built ONNX <see cref="InferenceSession"/>.
    /// </summary>
    /// <param name="session">The loaded DB detection model session (this instance takes ownership and disposes it).</param>
    /// <param name="inputName">Optional override for the model input tensor name; null = first input.</param>
    /// <param name="outputName">Optional override for the model output tensor name; null = first output.</param>
    public DbTextDetector(InferenceSession session, string? inputName = null, string? outputName = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _inputName = inputName ?? _session.InputMetadata.Keys.First();
        _outputName = outputName ?? _session.OutputMetadata.Keys.First();
    }

    /// <inheritdoc />
    public IReadOnlyList<TextQuad> Detect(Image<Rgb24> image, DetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);

        if (image.Width == 0 || image.Height == 0)
        {
            return Array.Empty<TextQuad>();
        }

        if (!options.EnhanceContrast && !options.TileLargeImages && options.MinTextHeight <= 0)
        {
            // Default path: a single pass, exactly as Python PaddleOCR.
            return DetectOnce(image, options);
        }

        // Opt-in passes. The enhanced copy feeds only the detector; coordinates are unchanged by it.
        using Image<Rgb24>? enhanced = options.EnhanceContrast
            ? image.Clone(ctx => ctx.BackgroundNormalize(0).ContrastStretch(0.5f, 99.5f))
            : null;
        var source = enhanced ?? image;

        if (options.TileLargeImages && DetectionTiling.ShouldTile(source.Width, source.Height, options, out int tileLength))
        {
            return DetectTiled(source, options, tileLength);
        }

        var quads = DetectOnce(source, options);
        if (options.MinTextHeight <= 0)
        {
            return quads;
        }

        int longSide = Math.Max(source.Width, source.Height);
        int shortSide = Math.Min(source.Width, source.Height);
        double factor = quads.Count == 0
            ? (longSide < DetectionTiling.EmptyRetryMaxSide
                ? Math.Min(2.0, (double)(options.MaxSideLimit > 0 ? options.MaxSideLimit : 4000) / longSide)
                : 1)
            : DetectionTiling.UpscaleFactor(DetectionTiling.MedianShortSide(quads), options.MinTextHeight, longSide, options.MaxSideLimit);
        if (factor <= 1.05)
        {
            return quads;
        }

        // Re-detect upscaled: limit_type=min with the short side brought to factor × its size.
        var upscaled = DetectOnce(source, options with
        {
            LimitTypeMax = false,
            LimitSideLen = Math.Max(options.LimitTypeMax ? 64 : options.LimitSideLen, (int)(shortSide * factor)),
        });
        return upscaled;
    }

    /// <summary>
    /// <see cref="DetectionOptions.TileLargeImages"/>: detects overlapping tiles along the long axis at full
    /// resolution, offsets their quads into image coordinates, and merges the overlaps.
    /// </summary>
    private IReadOnlyList<TextQuad> DetectTiled(Image<Rgb24> image, DetectionOptions options, int tileLength)
    {
        bool vertical = image.Height >= image.Width;
        int longSide = vertical ? image.Height : image.Width;
        var tiles = DetectionTiling.PlanTiles(longSide, tileLength, DetectionTiling.TileOverlap);
        var candidates = new List<(TextQuad Quad, bool TouchesCut)>();
        const float edgeTolerance = 2f;

        for (int t = 0; t < tiles.Count; t++)
        {
            var (start, length) = tiles[t];
            var rect = vertical
                ? new Rectangle(0, start, image.Width, length)
                : new Rectangle(start, 0, length, image.Height);
            using var tile = image.Clone(ctx => ctx.Crop(rect));
            var quads = DetectOnce(tile, options);

            bool cutBefore = t > 0;
            bool cutAfter = t < tiles.Count - 1;
            foreach (var q in quads)
            {
                var b = q.ToAxisAlignedBounds();
                float lo = vertical ? b.Top : b.Left;
                float hi = vertical ? b.Bottom : b.Right;
                bool touches = (cutBefore && lo <= edgeTolerance) || (cutAfter && hi >= length - edgeTolerance);
                float dx = vertical ? 0 : start;
                float dy = vertical ? start : 0;
                var shifted = new TextQuad(
                    new PointF(q.P0.X + dx, q.P0.Y + dy),
                    new PointF(q.P1.X + dx, q.P1.Y + dy),
                    new PointF(q.P2.X + dx, q.P2.Y + dy),
                    new PointF(q.P3.X + dx, q.P3.Y + dy),
                    q.Score);
                candidates.Add((shifted, touches));
            }
        }

        return DetectionTiling.Merge(candidates, 0.5);
    }

    /// <summary>
    /// One detector pass: resize per <paramref name="options"/>, infer, post-process, map back.
    /// </summary>
    private IReadOnlyList<TextQuad> DetectOnce(Image<Rgb24> image, DetectionOptions options)
    {
        int origW = image.Width;
        int origH = image.Height;

        // DetResizeForTest zero-pads tiny inputs (h + w < 64) onto a ≥32×32 canvas before resizing;
        // boxes still map back against the original dimensions (Python scales by src_w / map_w).
        using Image<Rgb24>? padded = origW + origH < 64 ? PadTinyImage(image) : null;
        var source = padded ?? image;

        // PREPROCESS: compute the resized (multiple-of-32) dimensions from the (padded) input.
        var (resizeW, resizeH) = ComputeResize(source.Width, source.Height, options.LimitSideLen, options.LimitTypeMax, options.MaxSideLimit);
        double ratioW = (double)resizeW / origW;
        double ratioH = (double)resizeH / origH;

        // Build the [1,3,H,W] float32 input tensor: resize, ImageNet-normalize, BGR, CHW.
        var input = BuildInputTensor(source, resizeW, resizeH);

        // INFER: single output probability map [1,1,H,W] in [0,1].
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, input) };
        using var results = _session.Run(inputs, new[] { _outputName });

        var output = results.First().AsTensor<float>();
        var (prob, mapW, mapH) = ExtractProbabilityMap(output, resizeW, resizeH);

        // POSTPROCESS: binarize -> regions -> min-area quad -> score -> unclip (in resized space).
        var boxes = DBPostProcess.GetBoxes(prob, mapW, mapH, options);
        if (boxes.Count == 0)
        {
            return Array.Empty<TextQuad>();
        }

        // Rescale each quad back to original-image coordinates by dividing out the resize ratio, clamp.
        var quads = new List<TextQuad>(boxes.Count);
        foreach (var box in boxes)
        {
            var p0 = MapBack(box.Points[0], ratioW, ratioH, origW, origH);
            var p1 = MapBack(box.Points[1], ratioW, ratioH, origW, origH);
            var p2 = MapBack(box.Points[2], ratioW, ratioH, origW, origH);
            var p3 = MapBack(box.Points[3], ratioW, ratioH, origW, origH);
            quads.Add(new TextQuad(p0, p1, p2, p3, box.Score));
        }

        return quads;
    }

    /// <summary>
    /// <c>DetResizeForTest</c>'s <c>image_padding</c>: zero-pads an image whose width + height is below 64
    /// onto a black canvas of at least 32×32 (content anchored top-left).
    /// </summary>
    private static Image<Rgb24> PadTinyImage(Image<Rgb24> image)
    {
        var canvas = new Image<Rgb24>(Math.Max(32, image.Width), Math.Max(32, image.Height));
        canvas.Mutate(c => c.DrawImage(image, new Point(0, 0), 1f));
        return canvas;
    }

    /// <summary>
    /// PaddleOCR's <c>DetResizeForTest</c>: pick a uniform scale from <paramref name="limitSideLen"/> and
    /// the limit policy, truncate each scaled dimension to int, cap the longest side at
    /// <paramref name="maxSideLimit"/>, then round each dimension to the nearest multiple of 32 (min 32).
    /// With <paramref name="limitTypeMax"/> = <c>true</c> (<c>limit_type=max</c>) the longest side is
    /// capped at <paramref name="limitSideLen"/> (only ever scaling down); with <c>false</c>
    /// (<c>limit_type=min</c>) the shortest side is brought up to <paramref name="limitSideLen"/> (only
    /// ever scaling up). Returns the resized width/height.
    /// </summary>
    internal static (int Width, int Height) ComputeResize(int srcW, int srcH, int limitSideLen, bool limitTypeMax, int maxSideLimit)
    {
        if (limitSideLen <= 0)
        {
            limitSideLen = 64;
        }
        if (maxSideLimit <= 0)
        {
            maxSideLimit = 4000;
        }

        double ratio = 1.0;
        if (limitTypeMax)
        {
            // limit_type=max: scale down only when the longest side exceeds the limit.
            int maxSide = Math.Max(srcW, srcH);
            if (maxSide > limitSideLen)
            {
                ratio = (double)limitSideLen / maxSide;
            }
        }
        else
        {
            // limit_type=min: scale up only when the shortest side is below the limit.
            int minSide = Math.Min(srcW, srcH);
            if (minSide < limitSideLen)
            {
                ratio = (double)limitSideLen / minSide;
            }
        }

        // Python truncates w*ratio / h*ratio to int BEFORE the round-to-32 step.
        int resizeW = (int)(srcW * ratio);
        int resizeH = (int)(srcH * ratio);

        // max_side_limit: after the limit_type scaling, cap the longest side (matters with limit_type=min).
        int maxResized = Math.Max(resizeW, resizeH);
        if (maxResized > maxSideLimit)
        {
            double cap = (double)maxSideLimit / maxResized;
            resizeW = (int)(resizeW * cap);
            resizeH = (int)(resizeH * cap);
        }

        // Nearest multiple of 32; Math.Round's banker's rounding matches Python round().
        resizeW = Math.Max((int)Math.Round(resizeW / 32.0) * 32, 32);
        resizeH = Math.Max((int)Math.Round(resizeH / 32.0) * 32, 32);
        return (resizeW, resizeH);
    }

    /// <summary>
    /// Resizes <paramref name="image"/> to (<paramref name="resizeW"/>, <paramref name="resizeH"/>),
    /// ImageNet-normalizes <c>(pixel/255 - mean) / std</c> in BGR plane order (mean/std index order
    /// unchanged — the model was exported for BGR input), and packs CHW into a <c>[1,3,H,W]</c> float32
    /// tensor.
    /// </summary>
    private static DenseTensor<float> BuildInputTensor(Image<Rgb24> image, int resizeW, int resizeH)
    {
        // Checked in long before narrowing, and before the resize and the tensor allocation. On the
        // limit_type=min path a 10000x3 image scales to 3,200,000 x 960, and resizeH * resizeW overflows int
        // to a NEGATIVE value -- so the resize/allocation would fail with an arithmetic or out-of-memory error
        // rather than anything naming the real problem.
        long planeLong = (long)resizeH * resizeW;
        if (planeLong * 3 > Array.MaxLength)
        {
            throw new ImageTooLargeException(
                $"The image rescales to {resizeW}x{resizeH} for detection, needing a {planeLong * 3:N0}-element " +
                "tensor, which exceeds the maximum array length. Downscale the image, or use a less extreme " +
                "aspect ratio.");
        }

        using var resized = image.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(resizeW, resizeH),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Triangle, // bilinear (cv2.resize default INTER_LINEAR)
        }));

        var tensor = new DenseTensor<float>(new[] { 1, 3, resizeH, resizeW });
        int plane = (int)planeLong;
        Memory<float> bufferMem = tensor.Buffer;

        resized.DangerousTryGetSinglePixelMemory(out Memory<Rgb24> pixelMem);
        var lutB = NormalizationLut[0];
        var lutG = NormalizationLut[1];
        var lutR = NormalizationLut[2];

        // Rows are independent; each writes a disjoint slice of the three planes.
        Parallel.For(0, resizeH, y =>
        {
            var row = pixelMem.Span.Slice(y * resizeW, resizeW);
            var buffer = bufferMem.Span;
            int rowOffset = y * resizeW;
            var bPlane = buffer.Slice(rowOffset, resizeW);
            var gPlane = buffer.Slice(plane + rowOffset, resizeW);
            var rPlane = buffer.Slice(2 * plane + rowOffset, resizeW);
            for (int x = 0; x < row.Length; x++)
            {
                var px = row[x];
                bPlane[x] = lutB[px.B]; // B plane
                gPlane[x] = lutG[px.G]; // G plane
                rPlane[x] = lutR[px.R]; // R plane
            }
        });

        return tensor;
    }

    /// <summary>
    /// Per-channel lookup tables (B, G, R index order) holding <c>(v / 255f - Mean[c]) / Std[c]</c> for
    /// every byte value, built with exactly the per-pixel expression so the tensor is bit-identical.
    /// </summary>
    private static readonly float[][] NormalizationLut = BuildNormalizationLut();

    private static float[][] BuildNormalizationLut()
    {
        var luts = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            luts[c] = new float[256];
            for (int v = 0; v < 256; v++)
            {
                byte b = (byte)v;
                luts[c][v] = (b / 255f - Mean[c]) / Std[c];
            }
        }
        return luts;
    }

    /// <summary>
    /// Reads the model output (expected <c>[1,1,H,W]</c> probability map in [0,1]) into a flat row-major
    /// buffer. Falls back to the resized dimensions when the output rank does not expose H/W explicitly.
    /// </summary>
    private static (float[] Prob, int Width, int Height) ExtractProbabilityMap(Tensor<float> output, int resizeW, int resizeH)
    {
        int rank = output.Dimensions.Length;
        int mapH = rank >= 2 ? output.Dimensions[rank - 2] : resizeH;
        int mapW = rank >= 1 ? output.Dimensions[rank - 1] : resizeW;
        if (mapH <= 0) mapH = resizeH;
        if (mapW <= 0) mapW = resizeW;

        var prob = new float[mapW * mapH];
        if (output is DenseTensor<float> dense)
        {
            // Last plane is contiguous and equals the H*W map regardless of leading singleton dims.
            var span = dense.Buffer.Span;
            int offset = span.Length - prob.Length;
            if (offset < 0) offset = 0;
            span.Slice(offset, Math.Min(prob.Length, span.Length - offset)).CopyTo(prob);
        }
        else
        {
            // Generic fallback: index the final two dimensions.
            for (int y = 0; y < mapH; y++)
            {
                for (int x = 0; x < mapW; x++)
                {
                    prob[y * mapW + x] = rank == 4 ? output[0, 0, y, x]
                        : rank == 3 ? output[0, y, x]
                        : output[y, x];
                }
            }
        }

        return (prob, mapW, mapH);
    }

    /// <summary>
    /// Maps a point from resized-image space back to original-image space by dividing out the per-axis
    /// resize ratio, then clamps it to the original image bounds.
    /// </summary>
    private static PointF MapBack(OcrPoint p, double ratioW, double ratioH, int origW, int origH)
    {
        float x = (float)Math.Clamp(p.X / ratioW, 0, origW);
        float y = (float)Math.Clamp(p.Y / ratioH, 0, origH);
        return new PointF(x, y);
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}
