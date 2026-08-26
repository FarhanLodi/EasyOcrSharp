using EasyOcrSharp.Structure.Engine.Models;

namespace EasyOcrSharp.Structure;

/// <summary>
/// Selects which layout-detection model the structure pipeline uses. PP-DocLayout ships in S/M sizes and
/// the higher-accuracy RT-DETR ("plus-L") variant; all three are mapped onto a shared
/// <see cref="StructureBlockType"/> label set by the engine.
/// </summary>
internal enum LayoutModel
{
    /// <summary>
    /// PP-DocLayout-S — the lightweight PicoDet-based layout detector (fastest, smallest).
    /// </summary>
    PicoDetS,

    /// <summary>
    /// PP-DocLayout-M — the medium PicoDet-based layout detector (balanced).
    /// </summary>
    PicoDetM,

    /// <summary>
    /// PP-DocLayout_plus-L — the RT-DETR-based layout detector (highest accuracy, heaviest).
    /// </summary>
    RtDetrL,
}
