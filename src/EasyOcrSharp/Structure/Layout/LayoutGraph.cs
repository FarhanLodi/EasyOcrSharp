using EasyOcrSharp.Structure.Engine.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Layout;

/// <summary>
/// Shared ONNX-graph helpers for the PaddleX / PaddleOCR layout detectors (<see cref="PicoDetLayoutDetector"/>
/// and <see cref="RtDetrLayoutDetector"/>). These exports fuse the box decode + NMS into the graph and emit
/// already-decoded detections whose leading columns are <c>[class_id, score, x1, y1, x2, y2]</c>, plus an
/// <c>int32</c> <c>boxes_num</c> tensor giving the per-image row count (batch = 1). The PicoDet / RT-DETR-plus
/// exports use a 6-wide row; the re-hosted PP-DocLayoutV3 RT-DETR export emits a <b>7-wide</b> row
/// (<c>[class_id, score, x1, y1, x2, y2, extra]</c>, the trailing column being an in-graph rank/index that is
/// ignored), confirmed by loading the ONNX and printing the output. The detection row width is therefore
/// detected per-run rather than fixed. This class centralizes (a) resolving the image input name + fixed
/// spatial size and the optional auxiliary inputs from the session metadata, (b) reading those fused
/// detections out of the run results, and (c) thresholding + class-mapping + box-scaling them into
/// <see cref="LayoutRegion"/>s.
/// </summary>
internal static class LayoutGraph
{
    /// <summary>
    /// A flat view over the graph's fused-decode output: <see cref="Rows"/> detections, each a
    /// <see cref="RowWidth"/>-wide run whose leading columns are <c>[class_id, score, x1, y1, x2, y2]</c> in
    /// <see cref="Data"/> (any trailing columns past the box corners are ignored).
    /// </summary>
    internal readonly struct Detections
    {
        /// <summary>
        /// The contiguous <c>[Rows × RowWidth]</c> detection values, row-major.
        /// </summary>
        public readonly float[] Data;

        /// <summary>
        /// The number of valid detection rows.
        /// </summary>
        public readonly int Rows;

        /// <summary>
        /// The per-row stride: 6 for the PicoDet / RT-DETR-plus exports, 7 for PP-DocLayoutV3.
        /// </summary>
        public readonly int RowWidth;

        public Detections(float[] data, int rows, int rowWidth)
        {
            Data = data;
            Rows = rows;
            RowWidth = rowWidth;
        }
    }

    /// <summary>
    /// Resolves the model's single image input: returns its name and reports its fixed spatial size via
    /// <paramref name="height"/>/<paramref name="width"/>. The image input is the first 4-D
    /// (<c>[N,C,H,W]</c>) input; its H/W come from the graph metadata, falling back to
    /// <paramref name="defaultSize"/> for any dimension the export left dynamic (≤ 0).
    /// </summary>
    public static string ResolveImageInput(InferenceSession session, out int height, out int width, int defaultSize)
    {
        height = defaultSize;
        width = defaultSize;

        foreach (var kvp in session.InputMetadata)
        {
            var dims = kvp.Value.Dimensions;
            if (dims is { Length: 4 })
            {
                int h = dims[2];
                int w = dims[3];
                height = h > 0 ? h : defaultSize;
                width = w > 0 ? w : defaultSize;
                return kvp.Key;
            }
        }

        // No 4-D input found (unexpected): fall back to the first input and the default square size.
        return session.InputMetadata.Keys.First();
    }

    /// <summary>
    /// Returns the graph input whose name equals (case-insensitively) or contains <paramref name="name"/>,
    /// or <c>null</c> when the export does not declare it (e.g. a PicoDet build with no <c>scale_factor</c>).
    /// </summary>
    public static string? FindInput(InferenceSession session, string name)
    {
        foreach (var key in session.InputMetadata.Keys)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) ||
                key.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// The minimum detection row width: <c>[class_id, score, x1, y1, x2, y2]</c>.
    /// </summary>
    private const int MinRowWidth = 6;

    /// <summary>
    /// Reads the fused-decode detections out of the run <paramref name="results"/>. Picks the float output
    /// shaped <c>[N, W]</c> (W ≥ 6) as the detection tensor — its leading six columns are
    /// <c>[class_id, score, x1, y1, x2, y2]</c> and any trailing column is ignored — and uses the companion
    /// <c>int32</c> <c>boxes_num</c> tensor (when present) to cap the row count to the actual number of valid
    /// boxes (otherwise every row is taken). The per-row stride W is taken from the tensor's declared shape so
    /// both the 6-wide (PicoDet / RT-DETR-plus) and the 7-wide (PP-DocLayoutV3) exports parse correctly.
    /// </summary>
    public static Detections ReadDetections(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        float[]? data = null;
        int rowWidth = 0;
        int? boxesNum = null;

        foreach (var value in results)
        {
            // The companion count tensor ("boxes_num") is an int32/int64 [batch] tensor: read its first entry.
            if (boxesNum is null && TryReadCount(value, out int count))
            {
                boxesNum = count;
                continue;
            }

            if (data is null && value.Value is Tensor<float> floatTensor && floatTensor.Length > 0)
            {
                // The detection tensor is 2-D [N, W]; prefer its declared last-dimension stride. Fall back to
                // a 6/7-wide divisibility check for exports that flatten the output to a 1-D buffer.
                var dims = floatTensor.Dimensions;
                int w = dims.Length >= 2 ? dims[dims.Length - 1] : 0;
                if (w < MinRowWidth)
                {
                    w = floatTensor.Length % MinRowWidth == 0 ? MinRowWidth
                        : floatTensor.Length % 7 == 0 ? 7
                        : 0;
                }

                if (w >= MinRowWidth && floatTensor.Length % w == 0)
                {
                    data = floatTensor.ToArray();
                    rowWidth = w;
                }
            }
        }

        if (data is null)
        {
            return new Detections(Array.Empty<float>(), 0, MinRowWidth);
        }

        int totalRows = data.Length / rowWidth;
        int rows = boxesNum is int n && n >= 0 ? Math.Min(n, totalRows) : totalRows;
        return new Detections(data, rows, rowWidth);
    }

    /// <summary>
    /// Thresholds, class-maps, and scales the fused detections into <see cref="LayoutRegion"/>s. Each row is
    /// kept when its score exceeds <paramref name="scoreThreshold"/>; its box corners are scaled by
    /// (<paramref name="scaleX"/>, <paramref name="scaleY"/>) into source-image space (1,1 when the graph
    /// already emitted source pixels) and clamped to <paramref name="origW"/>×<paramref name="origH"/>; and
    /// its raw class id is mapped via <paramref name="classMap"/> (unmapped ids resolve to
    /// <see cref="StructureBlockType.Unknown"/>). Zero-area boxes are dropped.
    /// </summary>
    public static IReadOnlyList<LayoutRegion> BuildRegions(
        Detections detections,
        IReadOnlyDictionary<int, StructureBlockType> classMap,
        float scoreThreshold,
        float scaleX,
        float scaleY,
        int origW,
        int origH)
    {
        var data = detections.Data;
        int rowWidth = detections.RowWidth;
        var regions = new List<LayoutRegion>();

        for (int i = 0; i < detections.Rows; i++)
        {
            int baseIdx = i * rowWidth;
            float score = data[baseIdx + 1];
            if (score <= scoreThreshold)
            {
                continue;
            }

            int rawClassId = (int)MathF.Round(data[baseIdx]);

            // Scale box corners from the graph's output space into source-image pixels, then clamp.
            float x1 = data[baseIdx + 2] * scaleX;
            float y1 = data[baseIdx + 3] * scaleY;
            float x2 = data[baseIdx + 4] * scaleX;
            float y2 = data[baseIdx + 5] * scaleY;

            double minX = Math.Clamp(Math.Min(x1, x2), 0, origW);
            double minY = Math.Clamp(Math.Min(y1, y2), 0, origH);
            double maxX = Math.Clamp(Math.Max(x1, x2), 0, origW);
            double maxY = Math.Clamp(Math.Max(y1, y2), 0, origH);
            if (maxX <= minX || maxY <= minY)
            {
                continue; // degenerate / off-image box
            }

            var type = classMap.TryGetValue(rawClassId, out var mapped) ? mapped : StructureBlockType.Unknown;
            regions.Add(new LayoutRegion(type, new OcrBoundingBox(minX, minY, maxX, maxY), score, rawClassId));
        }

        return regions;
    }

    /// <summary>
    /// Tries to read a scalar count from a <c>boxes_num</c>-style tensor (an <c>int32</c> or <c>int64</c>
    /// tensor of length ≥ 1). Returns the first element; <c>false</c> for float tensors or unsupported types.
    /// </summary>
    private static bool TryReadCount(DisposableNamedOnnxValue value, out int count)
    {
        switch (value.Value)
        {
            case Tensor<int> i32 when i32.Length >= 1:
                count = i32.GetValue(0);
                return true;
            case Tensor<long> i64 when i64.Length >= 1:
                count = (int)i64.GetValue(0);
                return true;
            default:
                count = 0;
                return false;
        }
    }
}
