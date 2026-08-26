using EasyOcrSharp.Structure.Engine.Models;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure;

/// <summary>
/// The result of recognizing a single table crop (<see cref="Table.ITableRecognizer"/>): the recovered
/// table as an HTML <c>&lt;table&gt;</c> string (cell structure with the OCR text filled in), plus the
/// predicted cell bounding boxes (in the table crop's pixel coordinates) the structure model emitted —
/// useful for overlaying or for re-aligning OCR lines to cells.
/// </summary>
/// <param name="Html">The recovered table as an HTML <c>&lt;table&gt;…&lt;/table&gt;</c> fragment.</param>
/// <param name="CellBounds">Per-cell axis-aligned bounds in the table crop's pixel coordinates.</param>
internal sealed record TableResult(
    string Html,
    IReadOnlyList<OcrBoundingBox> CellBounds);
