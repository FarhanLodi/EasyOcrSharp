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
/// PaddleOCR DB / DBNet text detector. Resizes the image to a multiple of 32 within
/// <see cref="DetectionOptions.LimitSideLen"/>, runs the segmentation model to get a per-pixel text
/// probability map, binarizes it, extracts contours, computes each contour's min-area rectangle, scores
/// it against the probability map, and "unclips" (expands) the polygon by
/// <see cref="DetectionOptions.UnclipRatio"/> back to original-image coordinates.
/// <para>
/// Pre-processing matches PaddleOCR's <c>DetResizeForTest</c> (limit_type max or min, per
/// <see cref="DetectionOptions.LimitTypeMax"/>) + ImageNet normalization; post-processing is delegated to
/// <see cref="DBPostProcess"/>. Polygon unclip uses Clipper2 (Clipper2Lib).
/// Reference: RapidOcrNet <c>TextDetector.cs</c> (Apache-2.0) and OnnxOCR <c>db_postprocess.py</c>.
/// </para>
/// </summary>
internal sealed class DbTextDetector : ITextDetector
{
    // ImageNet mean/std (PaddleOCR det normalization), applied to pixel/255 in RGB channel order.
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

        int origW = image.Width;
        int origH = image.Height;
        if (origW == 0 || origH == 0)
        {
            return Array.Empty<TextQuad>();
        }

        // PREPROCESS: compute the resized (multiple-of-32) dimensions and the resize ratios per axis.
        var (resizeW, resizeH, ratioW, ratioH) = ComputeResize(origW, origH, options.LimitSideLen, options.LimitTypeMax);

        // Build the [1,3,H,W] float32 input tensor: resize, ImageNet-normalize, RGB, CHW.
        var input = BuildInputTensor(image, resizeW, resizeH);

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
    /// PaddleOCR's <c>DetResizeForTest</c>: pick a uniform scale from <paramref name="limitSideLen"/> and
    /// the limit policy, then round each dimension to the nearest multiple of 32 (min 32). With
    /// <paramref name="limitTypeMax"/> = <c>true</c> (<c>limit_type=max</c>) the longest side is capped at
    /// <paramref name="limitSideLen"/> (only ever scaling down); with <c>false</c> (<c>limit_type=min</c>)
    /// the shortest side is brought up to <paramref name="limitSideLen"/> (only ever scaling up). Returns
    /// the resized width/height and the per-axis resize ratios (resized / original).
    /// </summary>
    private static (int Width, int Height, double RatioW, double RatioH) ComputeResize(int origW, int origH, int limitSideLen, bool limitTypeMax)
    {
        if (limitSideLen <= 0)
        {
            limitSideLen = 960;
        }

        double ratio = 1.0;
        if (limitTypeMax)
        {
            // limit_type=max: scale down only when the longest side exceeds the limit.
            int maxSide = Math.Max(origW, origH);
            if (maxSide > limitSideLen)
            {
                ratio = (double)limitSideLen / maxSide;
            }
        }
        else
        {
            // limit_type=min: scale up only when the shortest side is below the limit.
            int minSide = Math.Min(origW, origH);
            if (minSide < limitSideLen)
            {
                ratio = (double)limitSideLen / minSide;
            }
        }

        int resizeW = (int)Math.Round(origW * ratio / 32.0) * 32;
        int resizeH = (int)Math.Round(origH * ratio / 32.0) * 32;
        resizeW = Math.Max(32, resizeW);
        resizeH = Math.Max(32, resizeH);

        double ratioW = (double)resizeW / origW;
        double ratioH = (double)resizeH / origH;
        return (resizeW, resizeH, ratioW, ratioH);
    }

    /// <summary>
    /// Resizes <paramref name="image"/> to (<paramref name="resizeW"/>, <paramref name="resizeH"/>),
    /// ImageNet-normalizes <c>(pixel/255 - mean) / std</c> in RGB order, and packs CHW into a
    /// <c>[1,3,H,W]</c> float32 tensor.
    /// </summary>
    private static DenseTensor<float> BuildInputTensor(Image<Rgb24> image, int resizeW, int resizeH)
    {
        using var resized = image.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(resizeW, resizeH),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic,
        }));

        var tensor = new DenseTensor<float>(new[] { 1, 3, resizeH, resizeW });
        int plane = resizeH * resizeW;
        Memory<float> bufferMem = tensor.Buffer;

        resized.ProcessPixelRows(accessor =>
        {
            var buffer = bufferMem.Span;
            for (int y = 0; y < resizeH; y++)
            {
                var row = accessor.GetRowSpan(y);
                int rowOffset = y * resizeW;
                for (int x = 0; x < resizeW; x++)
                {
                    var px = row[x];
                    int idx = rowOffset + x;
                    buffer[idx] = (px.R / 255f - Mean[0]) / Std[0];            // R channel
                    buffer[plane + idx] = (px.G / 255f - Mean[1]) / Std[1];     // G channel
                    buffer[2 * plane + idx] = (px.B / 255f - Mean[2]) / Std[2]; // B channel
                }
            }
        });

        return tensor;
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
