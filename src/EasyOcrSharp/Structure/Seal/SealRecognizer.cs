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
/// <c>box_type='poly'</c>. Because seal lines bend around a circle, post-processing runs in polygon mode
/// (<see cref="DBPostProcess.GetPolygons"/>, the <c>polygons_from_bitmap</c> port): each curved line
/// keeps its free-form N-point outline instead of being flattened to a min-area quad. Each polygon is
/// then rectified by <see cref="AutoRectifier"/> (the <c>get_poly_rect_crop</c> port) — near-rectangular
/// polygons (IoU vs their min-area rect ≥ 0.7) take the plain <see cref="PerspectiveWarp"/> quad warp,
/// while genuinely curved arcs are straightened piecewise: the outline is split into top/bottom edge
/// chains, resampled, and every segment quad is warped to an upright strip, the strips stitched into one
/// straight line image for the recognizer.
/// </para>
/// <para>
/// Pre-processing (upscale the SHORT side to 736 — <c>limit_type=min</c>, capped at 4000 px on the long
/// side — rounded to a multiple of 32, then BGR ImageNet normalization) and DB post-processing mirror
/// <see cref="DbTextDetector"/>; refer to that file for the fully-documented DB pipeline this reuses.
/// Reference: PaddleOCR <c>PP-OCRv4 seal det</c> + <c>db_postprocess.py</c> (<c>box_type='poly'</c>).
/// </para>
/// </summary>
internal sealed class SealRecognizer : ISealRecognizer
{
    // ImageNet mean/std (PaddleOCR det normalization), applied to pixel/255 in index order over the B,G,R
    // planes — the seal det model, like the main DB detector, consumes BGR input (DecodeImage img_mode: BGR
    // in the exported inference config).
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    // PaddleX seal-recognition config caps the resized dims at 4000 px (max_side_limit) so the min-side
    // upscale below cannot blow up on elongated crops.
    private const int MaxSideLimit = 4000;

    // Seal text is small and curved, so PaddleOCR's seal config relaxes the pixel threshold to 0.2 and
    // shrinks det_db_unclip_ratio to 0.5 (seal arcs are thin; a smaller ratio avoids merging adjacent arcs).
    // These are the values PaddleX ships in its seal-recognition config (limit_side_len 736, limit_type min).
    private static readonly DetectionOptions SealDetectionOptions = new()
    {
        LimitSideLen = 736,     // PaddleOCR seal det det_limit_side_len
        LimitTypeMax = false,   // limit_type=min: the SHORT side is scaled UP to 736 (ComputeResize below)
        DetThreshold = 0.2,     // det_db_thresh (seal)
        BoxThreshold = 0.6,     // det_db_box_thresh
        UnclipRatio = 0.5,      // det_db_unclip_ratio (seal arcs are thin; a smaller ratio avoids merging arcs)
        MinSize = 3,
        BoxType = DetectionBoxType.Poly, // det_box_type='poly': keep curved outlines (GetPolygons below)
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
    /// (2) DB-postprocess that map in polygon mode (<see cref="DBPostProcess.GetPolygons"/>,
    /// <c>box_type='poly'</c>) into scored N-point outlines already mapped back to the crop's pixel space;
    /// (3) rectify each polygon via <see cref="AutoRectifier.GetPolyRectCrop"/> — quad warp for straight
    /// lines, piecewise curved-text unwarp for arcs;
    /// (4) batch-recognize the strips through the shared <see cref="ITextRecognizer"/>;
    /// (5) emit one <see cref="OcrLine"/> per non-empty reading, with its polygon in the crop's pixel space.
    /// An empty result lets the engine fall back to plain OCR over the whole seal crop (the 2.0.4 safety
    /// net in <c>StructureEngine</c>).
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

        // --- (1) DETECT: upscale the short side to 736 (multiple of 32, long side capped at 4000),
        // BGR ImageNet-normalize, run seal det. (GetPolygons divides the resize back out itself, so the
        // per-axis ratios are not needed here.)
        var (resizeW, resizeH, _, _) = ComputeResize(origW, origH, SealDetectionOptions.LimitSideLen);
        var input = BuildInputTensor(sealCrop, resizeW, resizeH);

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, input) };
        using var results = _sealDetector.Run(inputs, new[] { _outputName });

        var output = results.First().AsTensor<float>();
        var (prob, mapW, mapH) = ExtractProbabilityMap(output, resizeW, resizeH);

        // --- (2) POSTPROCESS in polygon mode (polygons_from_bitmap): binarize -> outer contours ->
        // approxPolyDP -> score -> unclip -> N-point polygons, already mapped back (with clamping) to the
        // seal-crop's own pixel space (the caller positions the crop in the page).
        var polygons = DBPostProcess.GetPolygons(prob, mapW, mapH, SealDetectionOptions, origW, origH);
        if (polygons.Count == 0)
        {
            return Array.Empty<OcrLine>();
        }

        // --- (3) RECTIFY: straighten each polygon via get_poly_rect_crop — the quad warp when the outline
        // is near-rectangular, the piecewise curved-text unwarp when it is a genuine arc. A null crop means
        // even the quad fallback was degenerate (sub-2px); skip it but keep the polygon->crop index mapping
        // so recognition results line back up with their source polygons.
        var crops = new List<Image<Rgb24>>(polygons.Count);
        var cropPolygons = new List<OcrPoint[]>(polygons.Count);
        try
        {
            foreach (var polygon in polygons)
            {
                var crop = AutoRectifier.GetPolyRectCrop(sealCrop, polygon.Points);
                if (crop is null) continue;
                crops.Add(crop);
                cropPolygons.Add(polygon.Points);
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

                var polygon = cropPolygons[i];
                lines.Add(new OcrLine
                {
                    Text = text,
                    Confidence = confidence,
                    BoundingPolygon = polygon,
                    BoundingBox = OcrBoundingBox.FromPoints(polygon),
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
    /// PaddleOCR's <c>DetResizeForTest</c> with the seal pipeline's <c>limit_type=min</c>: scale UP so the
    /// shortest side reaches <paramref name="limitSideLen"/> (736 — small seal crops must be enlarged for
    /// DB to see the thin arcs; never scaled down here), cap the resulting longest side at
    /// <see cref="MaxSideLimit"/> (4000), then round each dimension to the nearest multiple of 32 (min 32).
    /// Returns the resized width/height and the per-axis resize ratios (resized / original). Mirrors
    /// PaddleX <c>text_detection/processors.py resize_image_type0</c> exactly, including the int
    /// truncations before the round-to-32, so the shared DB post-processing maps back identically.
    /// </summary>
    internal static (int Width, int Height, double RatioW, double RatioH) ComputeResize(int origW, int origH, int limitSideLen)
    {
        if (limitSideLen <= 0)
        {
            limitSideLen = 736;
        }

        // limit_type=min: scale up only when the shortest side is below the limit.
        double ratio = 1.0;
        int minSide = Math.Min(origW, origH);
        if (minSide < limitSideLen)
        {
            ratio = (double)limitSideLen / minSide;
        }

        int resizeW = (int)(origW * ratio);
        int resizeH = (int)(origH * ratio);

        // max_side_limit: re-shrink when the upscale pushed the longest side past the cap.
        int maxSide = Math.Max(resizeW, resizeH);
        if (maxSide > MaxSideLimit)
        {
            double cap = (double)MaxSideLimit / maxSide;
            resizeW = (int)(resizeW * cap);
            resizeH = (int)(resizeH * cap);
        }

        resizeW = Math.Max((int)Math.Round(resizeW / 32.0) * 32, 32);
        resizeH = Math.Max((int)Math.Round(resizeH / 32.0) * 32, 32);

        double ratioW = (double)resizeW / origW;
        double ratioH = (double)resizeH / origH;
        return (resizeW, resizeH, ratioW, ratioH);
    }

    /// <summary>
    /// Resizes the seal crop to (<paramref name="resizeW"/>, <paramref name="resizeH"/>),
    /// ImageNet-normalizes <c>(pixel/255 - mean) / std</c> in index order over the <b>B,G,R</b> planes
    /// (the seal det model consumes BGR, like the main DB detector), and packs CHW into a
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
                    buffer[idx] = (px.B / 255f - Mean[0]) / Std[0];            // B plane (BGR input)
                    buffer[plane + idx] = (px.G / 255f - Mean[1]) / Std[1];     // G plane
                    buffer[2 * plane + idx] = (px.R / 255f - Mean[2]) / Std[2]; // R plane
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

    /// <inheritdoc />
    public void Dispose() => _sealDetector.Dispose();
}
