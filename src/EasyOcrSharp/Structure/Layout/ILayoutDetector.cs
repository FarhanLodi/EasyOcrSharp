using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Structure.Layout;

/// <summary>
/// Detects document layout regions (text, title, table, figure, formula, seal, …) in a page image and
/// returns them as <see cref="LayoutRegion"/>s with mapped <see cref="StructureBlockType"/>s. Implemented
/// by <see cref="PicoDetLayoutDetector"/> (PP-DocLayout-S/M, PicoDet) and <see cref="RtDetrLayoutDetector"/>
/// (PP-DocLayout_plus-L, RT-DETR). Each implementation owns its ONNX session and disposes it.
/// </summary>
internal interface ILayoutDetector : IDisposable
{
    /// <summary>
    /// Runs layout detection on <paramref name="image"/>.
    /// </summary>
    /// <param name="image">The page image to analyze (caller retains ownership).</param>
    /// <param name="scoreThreshold">
    /// Confidence floor (0–1): detections scoring at or below this are discarded
    /// (<see cref="StructureOptions.DefaultLayoutScoreThreshold"/> = 0.5). Passed per call rather than held
    /// on the detector because <see cref="StructureEngine"/> caches one detector instance per
    /// <see cref="LayoutModel"/> and reuses it across calls with different <see cref="StructureOptions"/>.
    /// </param>
    /// <returns>The detected regions in source-image pixel coordinates; empty when none are found.</returns>
    IReadOnlyList<LayoutRegion> Detect(Image<Rgb24> image, float scoreThreshold);
}
