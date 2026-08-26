using System.IO.Compression;
using System.Text;

namespace EasyOcrSharp.Structure.Export;

/// <summary>
/// A placed grid cell, carrying its text, its spans and whether it is a rowspan continuation slot.
/// </summary>
internal sealed class OoxmlGridCell
{
    public string Text { get; init; } = string.Empty;
    public int ColSpan { get; init; } = 1;
    public int RowSpan { get; init; } = 1;

    /// <summary>
    /// True when this slot is occupied by a <c>rowspan</c> from a cell in an earlier row.
    /// </summary>
    public bool IsRowSpanContinuation { get; init; }
}
