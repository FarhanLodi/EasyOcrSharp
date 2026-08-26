using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EasyOcrSharp.Structure.Engine.Detection;
using EasyOcrSharp.Structure.Engine.Geometry;
using EasyOcrSharp.Structure.Engine.Recognition;
using EasyOcrSharp.Structure.Engine.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Seal;

/// <summary>
/// Seal-text recognizer. Runs the PP-OCRv4 seal (curved-text) detector over the seal crop to locate the
/// curved text lines, rectifies each into an upright strip, and recognizes it with the shared
/// <see cref="ITextRecognizer"/>. Owns and disposes the seal-detector session; the injected text
/// recognizer is owned by the engine and is NOT disposed here.
/// <para>
/// The seal detector is a DB (Differentiable Binarization) segmentation network — the same family as the
/// main <see cref="DbTextDetector"/>, trained on curved/circular seal text and exported with PaddleOCR's
/// <c>box_type='poly'</c>. Because seal lines bend around a circle, PaddleOCR emits a free-form polygon per
/// line; here we <b>approximate each curved line with its minimum-area quad</b> (via
/// <see cref="DBPostProcess.GetBoxes"/>, which already fits a min-area quad to every connected region) so
/// the existing <see cref="PerspectiveWarp"/> + <see cref="ITextRecognizer"/> path can be reused unchanged.
/// This is a pragmatic simplification: a tightly-curved seal arc is straightened only approximately by a
/// single quad, but for the small, near-horizontal text blocks typical of seals (company name top arc,
/// code bottom arc, centred star/title) it recovers the text well. A future improvement would unroll the
/// arc into a strip via per-segment sampling (PaddleOCR's <c>sorted_boxes</c> + polar unwarp); see TODO.
/// </para>
/// <para>
/// Pre-processing (resize to a multiple of 32 within the detector's side-length cap + ImageNet
/// normalization) and DB post-processing mirror <see cref="DbTextDetector"/>; refer to that file for the
/// fully-documented DB pipeline this reuses. Reference: PaddleOCR <c>PP-OCRv4 seal det</c> +
/// <c>db_postprocess.py</c> (<c>box_type='poly'</c>).
/// </para>
/// </summary>
internal sealed class SealRecognizer : ISealRecognizer
{
    // ImageNet mean/std (PaddleOCR det normalization), applied to pixel/255 in RGB channel order — identical
    // to DbTextDetector. Seal det uses the same input normalization as the main DB detector.
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    // Seal text is small and curved, so we relax the box-score floor relative to the main detector's 0.6 to
    // avoid dropping faint arc segments; the other DB knobs keep their PaddleOCR defaults. det_db_unclip is a
    // little larger than the main detector's 1.5 because seal glyphs sit in a thin ring and benefit from a
    // slightly more generous expansion. These are the values PaddleOCR ships in its seal-recognition config.
    private static readonly DetectionOptions SealDetectionOptions = new()
    {
        LimitSideLen = 736,     // PaddleOCR seal det det_limit_side_len
        DetThreshold = 0.2,     // det_db_thresh (seal)
        BoxThreshold = 0.6,     // det_db_box_thresh
        UnclipRatio = 0.5,      // det_db_unclip_ratio (seal arcs are thin; a smaller ratio avoids merging arcs)
        MinSize = 3,
    };

    private readonly InferenceSession _sealDetector;
    private readonly ITextRecognizer _textRecognizer;
    private readonly string _inputName;
    private readonly string _outputName;

    /// <summary>
    /// Creates the recognizer over a built seal-detector ONNX session and the shared text recognizer.
    /// </summary>
    /// <param name="sealDetector">The loaded seal (curved-text) detector session (this instance takes ownership).</param>
    /// <param name="textRecognizer">The shared text recognizer (owned by the engine; NOT disposed here).</param>
    public SealRecognizer(InferenceSession sealDetector, ITextRecognizer textRecognizer)
    {
        _sealDetector = sealDetector ?? throw new ArgumentNullException(nameof(sealDetector));
        _textRecognizer = textRecognizer ?? throw new ArgumentNullException(nameof(textRecognizer));

        // Seal det has one input and one output, like the main DB detector; resolve their names once.
        _inputName = _sealDetector.InputMetadata.Keys.First();
        _outputName = _sealDetector.OutputMetadata.Keys.First();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Pipeline: (1) ImageNet-normalize the seal crop and run the DB seal detector to a probability map;
    /// (2) DB-postprocess that map into scored min-area quads (one per curved line, approximated);
    /// (3) rectify each quad to an upright strip via <see cref="PerspectiveWarp"/>;
    /// (4) batch-recognize the strips through the shared <see cref="ITextRecognizer"/>;
    /// (5) emit one <see cref="OcrLine"/> per non-empty reading, with its polygon in the crop's pixel space.
    /// </remarks>
    public IReadOnlyList<OcrLine> Recognize(Image<Rgb24> sealCrop)
    {
        ArgumentNullException.ThrowIfNull(sealCrop);

        int origW = sealCrop.Width;
        int origH = sealCrop.Height;
        if (origW == 0 || origH == 0)
        {
            return Array.Empty<OcrLine>();
        }

        // --- (1) DETECT: resize to a multiple-of-32 within the side cap, ImageNet-normalize, run seal det.
        var (resizeW, resizeH, ratioW, ratioH) = ComputeResize(origW, origH, SealDetectionOptions.LimitSideLen);
        var input = BuildInputTensor(sealCrop, resizeW, resizeH);

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, input) };
        using var results = _sealDetector.Run(inputs, new[] { _outputName });

        var output = results.First().AsTensor<float>();
        var (prob, mapW, mapH) = ExtractProbabilityMap(output, resizeW, resizeH);

        // --- (2) POSTPROCESS: binarize -> connected regions -> min-area quad (the 'poly' approximation) ->
        // score -> unclip, all in resized space. Curved seal lines are approximated by their min-area quad.
        var boxes = DBPostProcess.GetBoxes(prob, mapW, mapH, SealDetectionOptions);
        if (boxes.Count == 0)
        {
            return Array.Empty<OcrLine>();
        }

        // Map each quad back to the seal-crop's own pixel space (the caller positions the crop in the page).
        var quads = new List<OcrPoint[]>(boxes.Count);
        foreach (var box in boxes)
        {
            quads.Add(new[]
            {
                MapBack(box.Points[0], ratioW, ratioH, origW, origH),
                MapBack(box.Points[1], ratioW, ratioH, origW, origH),
                MapBack(box.Points[2], ratioW, ratioH, origW, origH),
                MapBack(box.Points[3], ratioW, ratioH, origW, origH),
            });
        }

        // --- (3) RECTIFY: warp each quad to an upright strip. A null crop means the polygon was degenerate
        // (sub-2px after rectification); skip it but keep the quad->crop index mapping so recognition results
        // line back up with their source polygons.
        var crops = new List<Image<Rgb24>>(quads.Count);
        var cropQuads = new List<OcrPoint[]>(quads.Count);
        try
        {
            foreach (var quad in quads)
            {
                var crop = PerspectiveWarp.Rectify(sealCrop, quad);
                if (crop is null) continue;
                crops.Add(crop);
                cropQuads.Add(quad);
            }

            if (crops.Count == 0) return Array.Empty<OcrLine>();

            // --- (4) RECOGNIZE: one batched ONNX pass through the shared text recognizer.
            var readings = _textRecognizer.Recognize(crops);

            // --- (5) EMIT: keep non-empty readings, each paired with its source polygon in crop coordinates.
            var lines = new List<OcrLine>(crops.Count);
            for (int i = 0; i < crops.Count && i < readings.Count; i++)
            {
                var (text, confidence) = readings[i];
                if (string.IsNullOrWhiteSpace(text)) continue;

                var quad = cropQuads[i];
                lines.Add(new OcrLine
                {
                    Text = text,
                    Confidence = confidence,
                    BoundingPolygon = quad,
                    BoundingBox = OcrBoundingBox.FromPoints(quad),
                });
            }

            return lines;
        }
        finally
        {
            foreach (var crop in crops) crop.Dispose();
        }
    }

    /// <summary>
    /// PaddleOCR's <c>DetResizeForTest</c> (limit_type=max): scale so the longest side is at most
    /// <paramref name="limitSideLen"/>, then round each dimension to the nearest multiple of 32 (min 32).
    /// Returns the resized width/height and the per-axis resize ratios (resized / original). Mirrors
    /// <see cref="DbTextDetector"/>'s resize so the shared DB post-processing maps back identically.
    /// </summary>
    private static (int Width, int Height, double RatioW, double RatioH) ComputeResize(int origW, int origH, int limitSideLen)
    {
        if (limitSideLen <= 0)
        {
            limitSideLen = 736;
        }

        double ratio = 1.0;
        int maxSide = Math.Max(origW, origH);
        if (maxSide > limitSideLen)
        {
            ratio = (double)limitSideLen / maxSide;
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
    /// Resizes the seal crop to (<paramref name="resizeW"/>, <paramref name="resizeH"/>),
    /// ImageNet-normalizes <c>(pixel/255 - mean) / std</c> in RGB order, and packs CHW into a
    /// <c>[1,3,H,W]</c> float32 tensor — identical to <see cref="DbTextDetector"/>'s input building.
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
    /// Reads the seal-detector output (expected <c>[1,1,H,W]</c> probability map in [0,1]) into a flat
    /// row-major buffer. Falls back to the resized dimensions when the output rank does not expose H/W.
    /// Mirrors <see cref="DbTextDetector"/>'s extraction.
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
    /// Maps a point from resized space back to the seal-crop's pixel space by dividing out the per-axis
    /// resize ratio, then clamps it to the crop bounds.
    /// </summary>
    private static OcrPoint MapBack(OcrPoint p, double ratioW, double ratioH, int origW, int origH)
    {
        double x = Math.Clamp(p.X / ratioW, 0, origW);
        double y = Math.Clamp(p.Y / ratioH, 0, origH);
        return new OcrPoint(x, y);
    }

    /// <inheritdoc />
    public void Dispose() => _sealDetector.Dispose();
}
