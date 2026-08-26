using EasyOcrSharp.Structure.Engine.Models;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure;

/// <summary>
/// A single region produced by a layout detector (<see cref="Layout.ILayoutDetector"/>): the region's
/// semantic <see cref="Type"/>, its axis-aligned <see cref="Bounds"/> in source-image pixel coordinates,
/// the detector's confidence <see cref="Score"/> (0–1), and the detector's original integer class index
/// (<see cref="RawClassId"/>) before it was mapped onto <see cref="StructureBlockType"/> — retained for
/// diagnostics and for re-mapping against a different label set.
/// </summary>
/// <param name="Type">The mapped semantic category of the region.</param>
/// <param name="Bounds">Axis-aligned bounds in source-image pixel coordinates.</param>
/// <param name="Score">Detector confidence in the region (0–1).</param>
/// <param name="RawClassId">The detector's original class index, before mapping to <see cref="StructureBlockType"/>.</param>
internal sealed record LayoutRegion(
    StructureBlockType Type,
    OcrBoundingBox Bounds,
    float Score,
    int RawClassId);
