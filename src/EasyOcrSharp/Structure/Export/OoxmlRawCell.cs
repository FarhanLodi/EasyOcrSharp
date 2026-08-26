using System.IO.Compression;
using System.Text;

namespace EasyOcrSharp.Structure.Export;

/// <summary>
/// A raw cell as scanned from the HTML, before placement into the rectangular grid.
/// </summary>
internal sealed record OoxmlRawCell(string Text, int ColSpan, int RowSpan);
