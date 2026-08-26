using System.Data;
using System.Globalization;
using System.Text;
using EasyOcrSharp.Structure;

namespace EasyOcrSharp.Export;

/// <summary>
/// Turns the tables recovered by <c>AnalyzeDocumentAsync</c> into the shapes .NET code actually works
/// with: rows of strings, a <see cref="DataTable"/>, or CSV.
/// </summary>
/// <remarks>
/// Document-structure analysis reports each table as HTML (<c>StructureBlock.TableHtml</c>), which is
/// faithful to the original layout but awkward to consume. These extensions run that markup through
/// <see cref="TableHtmlParser"/> and hand back a rectangular grid. Merged cells are expanded into
/// repeated values — see <see cref="TableHtmlParser"/> for the details.
/// </remarks>
public static class StructureExportExtensions
{
    /// <summary>
    /// Returns the table blocks of an analyzed document, in reading order.
    /// </summary>
    /// <param name="result">The document-structure analysis result.</param>
    public static IReadOnlyList<StructureBlock> Tables(this StructureResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var tables = new List<StructureBlock>();
        foreach (var block in result.Blocks)
        {
            if (block.Type == StructureBlockType.Table)
            {
                tables.Add(block);
            }
        }

        return tables;
    }

    /// <summary>
    /// Parses a table block's HTML into a rectangular grid of cell strings. Returns an empty list for
    /// a block that carries no table markup.
    /// </summary>
    /// <param name="block">A block whose <c>Type</c> is <c>Table</c>.</param>
    public static IReadOnlyList<IReadOnlyList<string>> ToRows(this StructureBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return TableHtmlParser.ToRows(block.TableHtml);
    }

    /// <summary>
    /// Parses a table block's HTML, also reporting whether its first row was made of header cells.
    /// </summary>
    /// <param name="block">A block whose <c>Type</c> is <c>Table</c>.</param>
    public static TableGrid ToGrid(this StructureBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return TableHtmlParser.Parse(block.TableHtml);
    }

    /// <summary>
    /// Converts a table block into a <see cref="DataTable"/>.
    /// </summary>
    /// <param name="block">A block whose <c>Type</c> is <c>Table</c>.</param>
    /// <param name="firstRowIsHeader">
    /// <c>null</c> (the default) uses the markup's own signal — the first row becomes column names only
    /// when it was written with <c>&lt;th&gt;</c> cells. Pass <c>true</c>/<c>false</c> to decide
    /// explicitly.
    /// </param>
    /// <param name="tableName">Optional <see cref="DataTable.TableName"/>.</param>
    /// <remarks>
    /// Column names are made safe automatically: blanks become <c>Column1</c>, <c>Column2</c>, … and
    /// duplicates get a numeric suffix, because <see cref="DataTable"/> rejects both.
    /// </remarks>
    public static DataTable ToDataTable(this StructureBlock block, bool? firstRowIsHeader = null, string? tableName = null)
    {
        ArgumentNullException.ThrowIfNull(block);
        return ToDataTable(block.ToGrid(), firstRowIsHeader, tableName);
    }

    /// <summary>
    /// Converts a parsed grid into a <see cref="DataTable"/>. See
    /// <see cref="ToDataTable(StructureBlock, bool?, string?)"/> for the header rules.
    /// </summary>
    /// <param name="grid">The parsed table.</param>
    /// <param name="firstRowIsHeader">Overrides the grid's own <see cref="TableGrid.HasHeaderRow"/>.</param>
    /// <param name="tableName">Optional <see cref="DataTable.TableName"/>.</param>
    public static DataTable ToDataTable(this TableGrid grid, bool? firstRowIsHeader = null, string? tableName = null)
    {
        ArgumentNullException.ThrowIfNull(grid);

        var table = tableName is null ? new DataTable() : new DataTable(tableName);
        table.Locale = CultureInfo.InvariantCulture;

        if (grid.RowCount == 0)
        {
            return table;
        }

        bool header = firstRowIsHeader ?? grid.HasHeaderRow;
        var names = header ? grid.Rows[0] : null;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < grid.ColumnCount; c++)
        {
            string candidate = names is not null && c < names.Count && names[c].Length > 0
                ? names[c]
                : string.Create(CultureInfo.InvariantCulture, $"Column{c + 1}");

            string unique = candidate;
            int suffix = 2;
            while (!used.Add(unique))
            {
                unique = string.Create(CultureInfo.InvariantCulture, $"{candidate}_{suffix}");
                suffix++;
            }

            table.Columns.Add(unique, typeof(string));
        }

        for (int r = header ? 1 : 0; r < grid.RowCount; r++)
        {
            var source = grid.Rows[r];
            var values = new object[grid.ColumnCount];
            for (int c = 0; c < grid.ColumnCount; c++)
            {
                values[c] = c < source.Count ? source[c] : string.Empty;
            }

            table.Rows.Add(values);
        }

        return table;
    }

    /// <summary>
    /// Renders a table block as CSV.
    /// </summary>
    /// <param name="block">A block whose <c>Type</c> is <c>Table</c>.</param>
    /// <param name="delimiter">Field separator. Default <c>,</c>; pass <c>;</c> or <c>\t</c> as needed.</param>
    /// <param name="includeHeaderRow">
    /// When <c>false</c>, a first row that the markup marked as headers is dropped. Ignored when the
    /// table has no header row. Default <c>true</c>.
    /// </param>
    public static string ToCsv(this StructureBlock block, char delimiter = ',', bool includeHeaderRow = true)
    {
        ArgumentNullException.ThrowIfNull(block);

        var grid = block.ToGrid();
        var rows = grid.Rows;
        if (!includeHeaderRow && grid.HasHeaderRow && rows.Count > 0)
        {
            rows = rows.Skip(1).ToList();
        }

        return ToCsv(rows, delimiter);
    }

    /// <summary>
    /// Renders a grid of cells as <a href="https://www.rfc-editor.org/rfc/rfc4180">RFC 4180</a> CSV: a
    /// value is quoted when it contains the delimiter, a double quote, CR or LF, and embedded quotes
    /// are doubled. Rows are terminated with CRLF, as the RFC specifies.
    /// </summary>
    /// <param name="rows">The cells, row-major.</param>
    /// <param name="delimiter">Field separator. Default <c>,</c>.</param>
    public static string ToCsv(this IReadOnlyList<IReadOnlyList<string>> rows, char delimiter = ',')
    {
        ArgumentNullException.ThrowIfNull(rows);

        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            for (int c = 0; c < row.Count; c++)
            {
                if (c > 0)
                {
                    builder.Append(delimiter);
                }

                AppendCsvField(builder, row[c], delimiter);
            }

            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Converts every table in an analyzed document to a <see cref="DataTable"/>, in reading order.
    /// </summary>
    /// <param name="result">The document-structure analysis result.</param>
    /// <param name="firstRowIsHeader">See <see cref="ToDataTable(StructureBlock, bool?, string?)"/>.</param>
    public static IReadOnlyList<DataTable> ToDataTables(this StructureResult result, bool? firstRowIsHeader = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var tables = result.Tables();
        var converted = new List<DataTable>(tables.Count);
        for (int i = 0; i < tables.Count; i++)
        {
            converted.Add(tables[i].ToDataTable(
                firstRowIsHeader,
                string.Create(CultureInfo.InvariantCulture, $"Table{i + 1}")));
        }

        return converted;
    }

    private static void AppendCsvField(StringBuilder builder, string? value, char delimiter)
    {
        value ??= string.Empty;

        bool needsQuotes = false;
        foreach (char ch in value)
        {
            if (ch == delimiter || ch == '"' || ch == '\r' || ch == '\n')
            {
                needsQuotes = true;
                break;
            }
        }

        if (!needsQuotes)
        {
            builder.Append(value);
            return;
        }

        builder.Append('"');
        foreach (char ch in value)
        {
            if (ch == '"')
            {
                builder.Append('"');
            }

            builder.Append(ch);
        }

        builder.Append('"');
    }
}
