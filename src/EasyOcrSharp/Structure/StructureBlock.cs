using EasyOcrSharp.Structure.Engine.Models;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure;

/// <summary>
/// One assembled block of the analyzed document, in final reading order. A block always carries its
/// semantic <see cref="Type"/>, its axis-aligned <see cref="Bounds"/> (source-image pixels) and its
/// reading-order <see cref="Order"/> index; the remaining members are populated only for the kinds of
/// block they apply to:
/// <list type="bullet">
///   <item><see cref="Text"/> — recognized text for text-like blocks (title, paragraph, caption, …).</item>
///   <item><see cref="TableHtml"/> — recovered HTML for a <see cref="StructureBlockType.Table"/>.</item>
///   <item><see cref="Latex"/> — recovered LaTeX for a <see cref="StructureBlockType.Formula"/>.</item>
///   <item><see cref="Lines"/> — the underlying recognized OCR lines (text/seal blocks), when available.</item>
/// </list>
/// <see cref="Score"/> is the block's overall confidence (defaults to 1 for blocks with no meaningful score).
/// </summary>
/// <param name="Type">The semantic category of the block.</param>
/// <param name="Bounds">Axis-aligned bounds in source-image pixel coordinates.</param>
/// <param name="Order">Zero-based reading-order index assigned by the reading-order pass.</param>
/// <param name="Text">Recognized text for text-like blocks; <c>null</c> when not applicable.</param>
/// <param name="TableHtml">Recovered table HTML for table blocks; <c>null</c> otherwise.</param>
/// <param name="Latex">Recovered LaTeX for formula blocks; <c>null</c> otherwise.</param>
/// <param name="Lines">The underlying recognized OCR lines, when available; <c>null</c> otherwise.</param>
/// <param name="Score">Overall block confidence (0–1); defaults to 1.</param>
public sealed record StructureBlock(
    StructureBlockType Type,
    OcrBoundingBox Bounds,
    int Order,
    string? Text = null,
    string? TableHtml = null,
    string? Latex = null,
    IReadOnlyList<OcrLine>? Lines = null,
    float Score = 1);
