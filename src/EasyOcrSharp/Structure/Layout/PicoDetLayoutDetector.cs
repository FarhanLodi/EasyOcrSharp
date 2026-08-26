using EasyOcrSharp.Structure.Engine.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Structure.Layout;

/// <summary>
/// PP-DocLayout-S / PP-DocLayout-M layout detector (PicoDet backbone). Resizes the page to the model's
/// fixed input (PP-DocLayout-S = 480×480, PP-DocLayout-M = 640×640), runs the PicoDet graph, and maps each
/// raw class index onto a <see cref="StructureBlockType"/> via the supplied <see cref="_classMap"/>. Owns
/// and disposes the ONNX session.
/// <para>
/// The PaddleX / PaddleOCR layout ONNX exports <b>fuse GFL decode + NMS into the graph</b>: the network's
/// output is already-decoded, already-NMS'd detections whose leading columns are
/// <c>[class_id, score, x1, y1, x2, y2]</c> (plus a <c>boxes_num</c> tensor giving the per-image row
/// count), batch = 1. (The detection-row stride is detected by the shared <see cref="LayoutGraph"/> parser,
/// which handles both the 6-wide PicoDet rows and the 7-wide PP-DocLayoutV3 RT-DETR rows.) This detector
/// therefore performs <i>no</i> anchor decode, distribution-focal-loss decode, or NMS — it only
/// pre-processes, runs the session, score-thresholds the rows, maps the class id, and scales the boxes back
/// to source-image pixels.
/// <para>
/// Note: the re-hosted layout model shipped with this package is the RT-DETR PP-DocLayoutV3 driven by
/// <see cref="RtDetrLayoutDetector"/>; no PicoDet layout ONNX is present, so this detector is currently
/// unused. It is retained for the PicoDet PP-DocLayout-S/M exports and shares the same output parser.
/// </para>
/// </para>
/// <para>
/// Pre-processing matches PaddleX's PicoDet layout config: stretch-resize (keep_ratio = <c>false</c>) to
/// the model's square input, ImageNet normalization <c>(x/255 − mean) / std</c> with mean
/// (0.485, 0.456, 0.406) / std (0.229, 0.224, 0.225) in RGB channel order, packed CHW. When the graph also
/// declares a <c>scale_factor</c> input it is supplied as <c>[scale_y, scale_x]</c> (resized / original) so
/// the in-graph decode emits source-pixel boxes directly; otherwise the output boxes are in input-pixel
/// space and are scaled here by <c>orig / inputSize</c>.
/// Reference: PaddleX <c>PicoDetPostProcess</c> / <c>DetResize</c> + <c>NormalizeImage</c>.
/// </para>
/// </summary>
internal sealed class PicoDetLayoutDetector : ILayoutDetector
{
    /// <summary>
    /// ImageNet mean, applied to <c>pixel/255</c> in RGB channel order (PaddleX <c>NormalizeImage</c>).
    /// </summary>
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };

    /// <summary>
    /// ImageNet std, applied after mean-subtraction in RGB channel order.
    /// </summary>
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    /// <summary>
    /// Detections scoring at or below this confidence are discarded (PaddleX layout <c>threshold</c>).
    /// </summary>
    private const float ScoreThreshold = 0.5f;

    /// <summary>
    /// Fallback square input edge when the graph declares a dynamic spatial dimension (PP-DocLayout-S default).
    /// </summary>
    private const int DefaultInputSize = 480;

    private readonly InferenceSession _session;
    private readonly IReadOnlyDictionary<int, StructureBlockType> _classMap;

    // Resolved once from the graph metadata: the single image input name, the model's fixed input H/W, and
    // the (optional) scale_factor input name. The PicoDet export's first output is the [N,6] detections.
    private readonly string _imageInputName;
    private readonly string? _scaleFactorInputName;
    private readonly int _inputHeight;
    private readonly int _inputWidth;

    /// <summary>
    /// Creates the detector over a built PicoDet ONNX session and a raw-class-id → block-type map (parsed
    /// from the model's label .yml / class list).
    /// </summary>
    /// <param name="session">The loaded PicoDet layout model session (this instance takes ownership).</param>
    /// <param name="classMap">Maps the model's raw class indices to <see cref="StructureBlockType"/>.</param>
    public PicoDetLayoutDetector(InferenceSession session, IReadOnlyDictionary<int, StructureBlockType> classMap)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _classMap = classMap ?? throw new ArgumentNullException(nameof(classMap));

        // PicoDet layout graphs expose the page as a 4-D image input plus, on some exports, a 2-D
        // "scale_factor" input. Pick the 4-D tensor as the image and remember the scale_factor name if present.
        _imageInputName = LayoutGraph.ResolveImageInput(_session, out _inputHeight, out _inputWidth, DefaultInputSize);
        _scaleFactorInputName = LayoutGraph.FindInput(_session, "scale_factor");
    }

    /// <inheritdoc />
    public IReadOnlyList<LayoutRegion> Detect(Image<Rgb24> image)
    {
        ArgumentNullException.ThrowIfNull(image);

        int origW = image.Width;
        int origH = image.Height;
        if (origW == 0 || origH == 0)
        {
            return Array.Empty<LayoutRegion>();
        }

        // PREPROCESS: stretch-resize to the model's square input + ImageNet-normalize into [1,3,H,W] RGB CHW.
        var input = BuildInputTensor(image);

        // The in-graph decode maps boxes back to source pixels when given scale_factor = resized/original
        // (scale_y first). We feed the *stretch* ratios so x1..y2 come out directly in source coordinates;
        // if the graph has no scale_factor input we instead rescale the output boxes ourselves below.
        float scaleY = _inputHeight / (float)origH;
        float scaleX = _inputWidth / (float)origW;

        var inputs = new List<NamedOnnxValue>(2)
        {
            NamedOnnxValue.CreateFromTensor(_imageInputName, input),
        };
        if (_scaleFactorInputName is not null)
        {
            // PaddleX feeds scale_factor as [scale_y, scale_x] (resized/original) so the graph un-scales boxes.
            var scaleFactor = new DenseTensor<float>(new[] { 1, 2 });
            scaleFactor[0, 0] = scaleY;
            scaleFactor[0, 1] = scaleX;
            inputs.Add(NamedOnnxValue.CreateFromTensor(_scaleFactorInputName, scaleFactor));
        }

        using var results = _session.Run(inputs);

        // POSTPROCESS: read the fused-decode/NMS detections [N,6] = [class_id, score, x1,y1,x2,y2].
        var detections = LayoutGraph.ReadDetections(results);
        if (detections.Rows == 0)
        {
            return Array.Empty<LayoutRegion>();
        }

        // When the graph consumed scale_factor it already emitted source-pixel boxes (boxScale = 1); otherwise
        // boxes are in input-pixel space and we scale each axis by orig/inputSize.
        bool boxesInSourceSpace = _scaleFactorInputName is not null;
        float invScaleX = boxesInSourceSpace ? 1f : origW / (float)_inputWidth;
        float invScaleY = boxesInSourceSpace ? 1f : origH / (float)_inputHeight;

        var regions = LayoutGraph.BuildRegions(
            detections, _classMap, ScoreThreshold, invScaleX, invScaleY, origW, origH);
        return regions;
    }

    /// <summary>
    /// Stretch-resizes <paramref name="image"/> to the model's square input (keep_ratio = false),
    /// ImageNet-normalizes <c>(pixel/255 − mean) / std</c> in RGB order, and packs CHW into a
    /// <c>[1, 3, H, W]</c> float32 tensor.
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
                    buffer[idx] = (px.R / 255f - Mean[0]) / Std[0];             // R channel
                    buffer[plane + idx] = (px.G / 255f - Mean[1]) / Std[1];      // G channel
                    buffer[2 * plane + idx] = (px.B / 255f - Mean[2]) / Std[2];  // B channel
                }
            }
        });

        return tensor;
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}
