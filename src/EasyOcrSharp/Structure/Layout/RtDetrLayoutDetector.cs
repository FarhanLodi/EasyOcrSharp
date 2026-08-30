using EasyOcrSharp.Structure.Engine.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Structure.Layout;

/// <summary>
/// PP-DocLayoutV3 / PP-DocLayout_plus-L layout detector (RT-DETR backbone). Resizes the page to the model's
/// fixed input (PP-DocLayoutV3 = 800×800), runs the RT-DETR graph (whose decoder emits a fixed top-k of 300
/// boxes — there is no NMS), score-thresholds the predictions, and maps each raw class index onto a
/// <see cref="StructureBlockType"/> via the supplied <see cref="_classMap"/>. Owns and disposes the ONNX
/// session.
/// <para>
/// The decode is fused into the graph: the output (<c>fetch_name_0</c>) is already-decoded detections shaped
/// <c>[300, 7]</c> as <c>[class_id, score, x1, y1, x2, y2, order_index]</c> — the leading six columns are the
/// detection, the seventh is the model's own predicted reading-order index, surfaced on each region as
/// <see cref="LayoutRegion.OrderIndex"/> and used downstream to sequence the blocks. A companion <c>boxes_num</c> tensor
/// (<c>fetch_name_1</c>, <c>int32[1]</c>) gives the valid row count, and <c>fetch_name_2</c>
/// (<c>int32[300,200,200]</c>) is an auxiliary instance mask that box detection ignores. Boxes come out in
/// <b>absolute source-image pixels</b> (the graph un-scales them using the supplied <c>scale_factor</c> and
/// <c>im_shape</c>): verified by running the model — e.g. a full-page table on a 527×1122 image returns the
/// box <c>(-0.1, -0.1, 526.9, 1121.9)</c>. This detector performs no decode and no NMS — it only
/// pre-processes, runs the session, keeps rows scoring above the threshold, and maps the class id.
/// </para>
/// <para>
/// Pre-processing (verified against the real ONNX): stretch-resize to the 800×800 square (keep_ratio = false)
/// and feed <c>pixel / 255</c> float pixels — there is <b>no</b> mean/std normalization — in RGB CHW order.
/// (Feeding raw 0–255 pixels collapses every score to ~0.01; the <c>/255</c> scaling restores them, e.g.
/// 0.94 for the body text on <c>ocr_test1.png</c>.) The graph takes three inputs: <c>image</c>
/// <c>[1,3,800,800]</c>, <c>im_shape</c> <c>[1,2]</c> = <c>[inputH, inputW]</c> = <c>[800, 800]</c>, and
/// <c>scale_factor</c> <c>[1,2]</c> = <c>[inputH/origH, inputW/origW]</c> (scale_y first — the other order
/// yields wrong boxes). Because the input is a stretch-resize to a square, both scales equal
/// inputEdge/origEdge per axis. Reference: PaddleX <c>DetResize</c> (keep_ratio = false) + <c>NormalizeImage</c>
/// (scale 1/255, no mean/std) + <c>RTDETRPostProcess</c>.
/// </para>
/// </summary>
internal sealed class RtDetrLayoutDetector : ILayoutDetector
{
    /// <summary>
    /// Fallback square input edge when the graph declares a dynamic spatial dimension (PP-DocLayout_plus-L default).
    /// </summary>
    private const int DefaultInputSize = 800;

    private readonly InferenceSession _session;
    private readonly IReadOnlyDictionary<int, StructureBlockType> _classMap;
    private readonly IReadOnlyDictionary<int, string>? _labelNames;

    // Resolved once from the graph metadata: the image input name, the model's fixed input H/W, and the
    // auxiliary "im_shape" / "scale_factor" input names (RT-DETR exports always declare both).
    private readonly string _imageInputName;
    private readonly string? _imShapeInputName;
    private readonly string? _scaleFactorInputName;
    private readonly int _inputHeight;
    private readonly int _inputWidth;

    // Only the detection tensor and its boxes_num companion are fetched. PP-DocLayoutV3 also emits an
    // int32[300,200,200] instance mask that box detection ignores; naming the outputs keeps ~48 MB per
    // page from being computed and marshalled. Null = graph metadata was inconclusive, fetch everything.
    private readonly IReadOnlyList<string>? _outputNames;

    /// <summary>
    /// Creates the detector over a built RT-DETR ONNX session and a raw-class-id → block-type map (parsed
    /// from the model's label .yml / class list).
    /// </summary>
    /// <param name="session">The loaded RT-DETR layout model session (this instance takes ownership).</param>
    /// <param name="classMap">Maps the model's raw class indices to <see cref="StructureBlockType"/>.</param>
    /// <param name="labelNames">
    /// Optional class-id → raw label name map from the same sidecar. Carried onto every
    /// <see cref="LayoutRegion.RawLabel"/> so <see cref="LayoutPostProcessor"/> can tell apart labels that
    /// <see cref="StructureBlockType"/> collapses (<c>reference</c> vs <c>reference_content</c>,
    /// <c>inline_formula</c> vs <c>display_formula</c>).
    /// </param>
    public RtDetrLayoutDetector(
        InferenceSession session,
        IReadOnlyDictionary<int, StructureBlockType> classMap,
        IReadOnlyDictionary<int, string>? labelNames = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _classMap = classMap ?? throw new ArgumentNullException(nameof(classMap));
        _labelNames = labelNames;

        // RT-DETR layout graphs expose three inputs: the 4-D image plus 2-D "im_shape" and "scale_factor".
        _imageInputName = LayoutGraph.ResolveImageInput(_session, out _inputHeight, out _inputWidth, DefaultInputSize);
        _imShapeInputName = LayoutGraph.FindInput(_session, "im_shape");
        _scaleFactorInputName = LayoutGraph.FindInput(_session, "scale_factor");
        _outputNames = LayoutGraph.ResolveDetectionOutputs(_session);
    }

    /// <inheritdoc />
    public IReadOnlyList<LayoutRegion> Detect(Image<Rgb24> image, float scoreThreshold)
    {
        ArgumentNullException.ThrowIfNull(image);

        int origW = image.Width;
        int origH = image.Height;
        if (origW == 0 || origH == 0)
        {
            return Array.Empty<LayoutRegion>();
        }

        // PREPROCESS: stretch-resize to the model's square input and pack pixel/255 float pixels into
        // [1,3,H,W] RGB CHW. NOTE: /255 scaling but NO mean/std — confirmed by running the real ONNX (raw
        // 0-255 collapses all scores to ~0.01; /255 restores them).
        //
        // This matches the canonical PP-DocLayoutV3 preprocessing on every point that affects the scores:
        // the reference image processor declares size 800x800, mean [0,0,0] and std [1,1,1] (i.e. rescale
        // only, no mean/std shift) and a plain stretch resize with no aspect-ratio padding. Measured against
        // the real graph, the remaining variables are noise: bicubic instead of bilinear moves the top score
        // by ~0.003, and BGR/RGB and keep-ratio padding change it by less. So a low score is the model's
        // genuine confidence, not a preprocessing defect — magnitude is content-dependent (a clean report
        // page scores ~0.87 on average with several regions above 0.9, while a dense page of small text
        // lines averages ~0.74, and a tight crop of a single line can top out below 0.5 and detect nothing).
        // Callers that need those faint regions lower StructureOptions.LayoutScoreThreshold rather than
        // expecting the scores themselves to rise.
        var input = BuildInputTensor(image);

        // im_shape is the network input size [inputH, inputW] = [800, 800]; scale_factor is
        // [inputH/origH, inputW/origW] (scale_y FIRST). The graph uses these to emit boxes in absolute
        // *source-image* pixels.
        float scaleY = _inputHeight / (float)origH;
        float scaleX = _inputWidth / (float)origW;

        var imShape = new DenseTensor<float>(new[] { 1, 2 });
        imShape[0, 0] = _inputHeight;
        imShape[0, 1] = _inputWidth;

        var scaleFactor = new DenseTensor<float>(new[] { 1, 2 });
        scaleFactor[0, 0] = scaleY;
        scaleFactor[0, 1] = scaleX;

        var inputs = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor(_imageInputName, input),
        };
        if (_imShapeInputName is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(_imShapeInputName, imShape));
        }
        if (_scaleFactorInputName is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(_scaleFactorInputName, scaleFactor));
        }

        using var results = _outputNames is null ? _session.Run(inputs) : _session.Run(inputs, _outputNames);

        // POSTPROCESS: read the fused-decode detections [300,7] = [class_id, score, x1,y1,x2,y2, order_index]
        // in absolute source pixels (the trailing column is the model's reading-order index, carried through
        // onto the region), then threshold and map the class id. No NMS — the RT-DETR decoder is NMS-free.
        var detections = LayoutGraph.ReadDetections(results);
        if (detections.Rows == 0)
        {
            return Array.Empty<LayoutRegion>();
        }

        // Boxes are already in source-image pixels (scale_factor un-scaled them in-graph), so no extra scale.
        var regions = LayoutGraph.BuildRegions(
            detections, _classMap, scoreThreshold, scaleX: 1f, scaleY: 1f, origW, origH, _labelNames);
        return regions;
    }

    /// <summary>
    /// Stretch-resizes <paramref name="image"/> to the model's square input (keep_ratio = false) and packs
    /// the <c>channel / 255</c> values (as floats, with no mean/std normalization) in RGB CHW order into a
    /// <c>[1, 3, H, W]</c> float32 tensor — the RT-DETR pre-processing the graph expects (verified against
    /// the real PP-DocLayoutV3 ONNX).
    /// </summary>
    private DenseTensor<float> BuildInputTensor(Image<Rgb24> image)
    {
        int w = _inputWidth;
        int h = _inputHeight;

        using var resized = image.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(w, h),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic,
        }));

        var tensor = new DenseTensor<float>(new[] { 1, 3, h, w });
        int plane = h * w;
        Memory<float> bufferMem = tensor.Buffer;

        resized.ProcessPixelRows(accessor =>
        {
            var buffer = bufferMem.Span;
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                int rowOffset = y * w;
                for (int x = 0; x < w; x++)
                {
                    var px = row[x];
                    int idx = rowOffset + x;
                    // pixel/255 — NO mean/std. RGB CHW.
                    buffer[idx] = px.R / 255f;             // R channel
                    buffer[plane + idx] = px.G / 255f;     // G channel
                    buffer[2 * plane + idx] = px.B / 255f; // B channel
                }
            }
        });

        return tensor;
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}
