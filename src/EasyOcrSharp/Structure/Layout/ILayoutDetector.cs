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
    /// <returns>The detected regions in source-image pixel coordinates; empty when none are found.</returns>
    IReadOnlyList<LayoutRegion> Detect(Image<Rgb24> image);
}
