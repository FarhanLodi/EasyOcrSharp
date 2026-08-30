using System.IO.Compression;
using System.Text;

namespace EasyOcrSharp.Structure.Export;

/// <summary>
/// A rectangular table grid recovered from HTML. <see cref="Cells"/> is <c>[row, col]</c>; a <c>null</c> slot
/// is one folded into a preceding cell's <c>colspan</c> (no element should be emitted for it), whereas a
/// <see cref="OoxmlGridCell.IsRowSpanContinuation"/> slot is a vertical-merge continuation that an exporter
/// renders as a merged/empty cell.
/// </summary>
internal sealed class OoxmlTableGrid
{
    public required OoxmlGridCell?[,] Cells { get; init; }
    public required int RowCount { get; init; }
    public required int ColumnCount { get; init; }

    // Upper bounds on the materialized grid, mirroring TableHtmlParser's. FromRows allocates two
    // rowCount x maxCols arrays, so degenerate markup (many rows, each summing to a huge colspan) would
    // otherwise OOM inside SaveAsDocx/SaveAsXlsx. Far above any real recovered table.
    private const int MaxRows = 4096;
    private const int MaxColumns = 1024;

    public static OoxmlTableGrid FromRows(List<List<OoxmlRawCell>> rows)
    {
        int rowCount = Math.Min(rows.Count, MaxRows);

        // Column count is the widest row once colspans are accounted for. Because rowspans push cells down
        // into later rows, we resolve placement with an occupancy map and grow the column count as needed.
        // First pass: an upper bound on columns.
        int maxCols = 0;
        for (int r = 0; r < rowCount; r++)
        {
            int width = 0;
            foreach (var cell in rows[r]) width += cell.ColSpan;
            if (width > maxCols) maxCols = width;
        }
        if (maxCols == 0) maxCols = 1;
        if (maxCols > MaxColumns) maxCols = MaxColumns;

        // occupied[r,c] marks slots already taken by a rowspan/colspan placed earlier.
        var occupied = new bool[rowCount, maxCols];
        var cells = new OoxmlGridCell?[rowCount, maxCols];

        for (int r = 0; r < rowCount; r++)
        {
            int c = 0;
            foreach (var raw in rows[r])
            {
                // Skip to the next free slot in this row (one occupied by a rowspan from above).
                while (c < maxCols && occupied[r, c]) c++;
                if (c >= maxCols) break; // row overflows the grid; ignore extra cells defensively

                int colSpan = Math.Min(raw.ColSpan, maxCols - c);
                int rowSpan = Math.Min(raw.RowSpan, rowCount - r);

                cells[r, c] = new OoxmlGridCell
                {
                    Text = raw.Text,
                    ColSpan = colSpan,
                    RowSpan = rowSpan,
                    IsRowSpanContinuation = false,
                };

                // Mark the full span as occupied; for rows below the origin, plant continuation cells so the
                // exporters can emit vertical-merge placeholders that span the same columns.
                for (int dr = 0; dr < rowSpan; dr++)
                {
                    for (int dc = 0; dc < colSpan; dc++)
                    {
                        int rr = r + dr;
                        int cc = c + dc;
                        occupied[rr, cc] = true;
                        if (dr > 0 && dc == 0)
                        {
                            cells[rr, cc] = new OoxmlGridCell
                            {
                                Text = string.Empty,
                                ColSpan = colSpan,
                                RowSpan = 1,
                                IsRowSpanContinuation = true,
                            };
                        }
                    }
                }

                c += colSpan;
            }
        }

        return new OoxmlTableGrid { Cells = cells, RowCount = rowCount, ColumnCount = maxCols };
    }
}
