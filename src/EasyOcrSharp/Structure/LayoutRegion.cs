using EasyOcrSharp.Structure.Engine.Models;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure;

/// <summary>
/// A single region produced by a layout detector (<see cref="Layout.ILayoutDetector"/>): the region's
/// semantic <see cref="Type"/>, its axis-aligned <see cref="Bounds"/> in source-image pixel coordinates,
/// the detector's confidence <see cref="Score"/> (0–1), and the detector's original integer class index
/// (<see cref="RawClassId"/>) plus its raw label name (<see cref="RawLabel"/>) before they were mapped onto
/// <see cref="StructureBlockType"/> — retained for diagnostics, for re-mapping against a different label
/// set, and because the post-processing filters key off the raw names. When the model emits one,
/// <see cref="OrderIndex"/> carries its own predicted reading-order position.
/// </summary>
/// <param name="Type">The mapped semantic category of the region.</param>
/// <param name="Bounds">Axis-aligned bounds in source-image pixel coordinates.</param>
/// <param name="Score">Detector confidence in the region (0–1).</param>
/// <param name="RawClassId">The detector's original class index, before mapping to <see cref="StructureBlockType"/>.</param>
/// <param name="RawLabel">
/// The model's own label for <paramref name="RawClassId"/> (normalized: lower-case, <c>_</c>-separated), e.g.
/// <c>reference_content</c> or <c>inline_formula</c> — distinctions <see cref="StructureBlockType"/>
/// deliberately collapses. <c>null</c> when the label sidecar was unavailable.
/// </param>
/// <param name="OrderIndex">
/// The reading-order position predicted by the model itself, when the export emits one: PP-DocLayoutV3's
/// 7-wide rows carry it as their trailing column. <c>null</c> for the 6-wide PicoDet / PP-DocLayout_plus-L
/// exports, which have no such column — those fall back to the XY-cut orderer.
/// </param>
internal sealed record LayoutRegion(
    StructureBlockType Type,
    OcrBoundingBox Bounds,
    float Score,
    int RawClassId,
    string? RawLabel = null,
    int? OrderIndex = null);
