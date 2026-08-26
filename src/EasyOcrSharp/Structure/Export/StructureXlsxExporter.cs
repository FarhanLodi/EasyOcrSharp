using System.IO.Compression;
using System.Text;

namespace EasyOcrSharp.Structure.Export;

/// <summary>
/// Exports a <see cref="StructureResult"/> to a Microsoft Excel <c>.xlsx</c> (SpreadsheetML) workbook,
/// reaching parity with Python PaddleOCR's PP-StructureV3 <c>save_to_xlsx()</c>. Each detected
/// <see cref="StructureBlockType.Table"/> becomes its own worksheet, with cell text laid out by row/column
/// from the table's recovered HTML grid; merged cells (<c>colspan</c>/<c>rowspan</c>) are emitted as
/// <c>&lt;mergeCell&gt;</c> ranges. When the document contains no tables, a single "Text" worksheet is
/// produced listing the text blocks one per row, so the workbook is always valid and non-empty.
/// <para>
/// The package is built by hand (an <c>.xlsx</c> is a ZIP of XML parts) using only the BCL, keeping the
/// library dependency-free and Native-AOT safe. Cell strings are emitted inline (<c>t="inlineStr"</c>) to
/// avoid maintaining a shared-strings table, and all text is XML-escaped.
/// </para>
/// </summary>
internal static class StructureXlsxExporter
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Renders the structure result to an in-memory SpreadsheetML <c>.xlsx</c> workbook.
    /// </summary>
    /// <param name="result">The analyzed document to export. Must not be <c>null</c>.</param>
    /// <returns>The bytes of a valid <c>.xlsx</c> ZIP package.</returns>
    public static byte[] ToXlsx(this StructureResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        using var buffer = new MemoryStream();
        WriteXlsx(result, buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Writes the structure result as a SpreadsheetML <c>.xlsx</c> workbook into <paramref name="destination"/>.
    /// The stream is left open so callers retain ownership.
    /// </summary>
    /// <param name="result">The analyzed document to export. Must not be <c>null</c>.</param>
    /// <param name="destination">The writable, seekable stream to receive the package. Must not be <c>null</c>.</param>
    public static void WriteXlsx(this StructureResult result, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(destination);
        WriteXlsx(result, destination, leaveOpen: true);
    }

    /// <summary>
    /// Renders the structure result and saves it to <paramref name="path"/> as an <c>.xlsx</c> file.
    /// </summary>
    /// <param name="result">The analyzed document to export. Must not be <c>null</c>.</param>
    /// <param name="path">The destination file path. Must not be <c>null</c> or empty.</param>
    public static void SaveAsXlsx(this StructureResult result, string path)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var file = File.Create(path);
        WriteXlsx(result, file, leaveOpen: false);
    }

    private static void WriteXlsx(StructureResult result, Stream destination, bool leaveOpen)
    {
        var sheets = BuildSheets(result);

        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen);

        StructureDocxExporter.WriteEntry(zip, "[Content_Types].xml", ContentTypesXml(sheets.Count));
        StructureDocxExporter.WriteEntry(zip, "_rels/.rels", RootRelsXml());
        StructureDocxExporter.WriteEntry(zip, "xl/workbook.xml", WorkbookXml(sheets));
        StructureDocxExporter.WriteEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml(sheets.Count));

        for (int s = 0; s < sheets.Count; s++)
        {
            StructureDocxExporter.WriteEntry(zip, $"xl/worksheets/sheet{s + 1}.xml", WorksheetXml(sheets[s]));
        }
    }

    // ---- sheet assembly --------------------------------------------------------------------------------

    /// <summary>
    /// One worksheet's logical contents: its (sanitized, unique) name plus its cell grid.
    /// </summary>
    private sealed record SheetModel(string Name, OoxmlTableGrid Grid);

    private static List<SheetModel> BuildSheets(StructureResult result)
    {
        var sheets = new List<SheetModel>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int tableNo = 0;

        foreach (var block in result.Blocks.OrderBy(b => b.Order))
        {
            if (block.Type != StructureBlockType.Table) continue;

            var grid = OoxmlHtmlTable.Parse(block.TableHtml);
            if (grid is null || grid.RowCount == 0 || grid.ColumnCount == 0)
            {
                // Table region with no parseable grid: keep it as a single-cell sheet so the region survives.
                grid = SingleCellGrid(!string.IsNullOrWhiteSpace(block.Text)
                    ? block.Text!.Trim()
                    : (block.TableHtml?.Trim() ?? string.Empty));
            }

            tableNo++;
            sheets.Add(new SheetModel(UniqueSheetName($"Table {tableNo}", used), grid));
        }

        // No tables in the document: emit a single sheet listing the text-bearing blocks, one per row.
        if (sheets.Count == 0)
        {
            sheets.Add(new SheetModel(UniqueSheetName("Text", used), TextBlocksGrid(result)));
        }

        return sheets;
    }

    private static OoxmlTableGrid SingleCellGrid(string text)
    {
        var cells = new OoxmlGridCell?[1, 1];
        cells[0, 0] = new OoxmlGridCell { Text = text, ColSpan = 1, RowSpan = 1 };
        return new OoxmlTableGrid { Cells = cells, RowCount = 1, ColumnCount = 1 };
    }

    /// <summary>
    /// Builds a single-column grid of every text-bearing block, used when no tables are present.
    /// </summary>
    private static OoxmlTableGrid TextBlocksGrid(StructureResult result)
    {
        var texts = new List<string>();
        foreach (var block in result.Blocks.OrderBy(b => b.Order))
        {
            string? text = block.Type switch
            {
                StructureBlockType.Formula => string.IsNullOrWhiteSpace(block.Latex) ? null : "$$ " + block.Latex!.Trim() + " $$",
                StructureBlockType.Table => block.Text, // a table that fell through here has no grid; keep any text
                _ => block.Text,
            };
            if (!string.IsNullOrWhiteSpace(text)) texts.Add(text!.Trim());
        }

        if (texts.Count == 0)
        {
            // Always emit at least one (empty) cell so the worksheet's dimension/sheetData is valid.
            return SingleCellGrid(string.Empty);
        }

        var cells = new OoxmlGridCell?[texts.Count, 1];
        for (int r = 0; r < texts.Count; r++)
        {
            cells[r, 0] = new OoxmlGridCell { Text = texts[r], ColSpan = 1, RowSpan = 1 };
        }
        return new OoxmlTableGrid { Cells = cells, RowCount = texts.Count, ColumnCount = 1 };
    }

    /// <summary>
    /// Sanitizes and de-duplicates a worksheet name to Excel's rules (≤31 chars, no <c>[]*?/\:</c>).
    /// </summary>
    private static string UniqueSheetName(string desired, HashSet<string> used)
    {
        var sb = new StringBuilder(desired.Length);
        foreach (char ch in desired)
        {
            sb.Append(ch is '[' or ']' or '*' or '?' or '/' or '\\' or ':' ? '_' : ch);
        }
        string baseName = sb.ToString().Trim();
        if (baseName.Length == 0) baseName = "Sheet";
        if (baseName.Length > 31) baseName = baseName[..31];

        string name = baseName;
        int suffix = 2;
        while (!used.Add(name))
        {
            string tail = "_" + suffix++;
            name = baseName.Length + tail.Length > 31 ? baseName[..(31 - tail.Length)] + tail : baseName + tail;
        }
        return name;
    }

    // ---- top-level OOXML parts -------------------------------------------------------------------------

    private static string ContentTypesXml(int sheetCount)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        for (int s = 1; s <= sheetCount; s++)
        {
            sb.Append("<Override PartName=\"/xl/worksheets/sheet").Append(s)
              .Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }
        sb.Append("</Types>");
        return sb.ToString();
    }

    private static string RootRelsXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" " +
        "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" " +
        "Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private static string WorkbookXml(List<SheetModel> sheets)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ")
          .Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        sb.Append("<sheets>");
        for (int s = 0; s < sheets.Count; s++)
        {
            // sheetId is 1-based and stable; r:id wires the sheet to its part via workbook.xml.rels.
            sb.Append("<sheet name=\"").Append(StructureDocxExporter.Xml(sheets[s].Name))
              .Append("\" sheetId=\"").Append(s + 1)
              .Append("\" r:id=\"rId").Append(s + 1).Append("\"/>");
        }
        sb.Append("</sheets>");
        sb.Append("</workbook>");
        return sb.ToString();
    }

    private static string WorkbookRelsXml(int sheetCount)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (int s = 1; s <= sheetCount; s++)
        {
            sb.Append("<Relationship Id=\"rId").Append(s)
              .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" ")
              .Append("Target=\"worksheets/sheet").Append(s).Append(".xml\"/>");
        }
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    // ---- worksheet -------------------------------------------------------------------------------------

    private static string WorksheetXml(SheetModel sheet)
    {
        var grid = sheet.Grid;
        int rows = grid.RowCount;
        int cols = grid.ColumnCount;

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        // The used range, e.g. A1:C5. Excel tolerates its absence but it helps some readers.
        sb.Append("<dimension ref=\"A1:").Append(CellRef(rows, cols)).Append("\"/>");

        sb.Append("<sheetData>");
        var merges = new List<string>();

        for (int r = 0; r < rows; r++)
        {
            sb.Append("<row r=\"").Append(r + 1).Append("\">");
            for (int c = 0; c < cols; c++)
            {
                var cell = grid.Cells[r, c];

                // Skip slots folded into a colspan (null) and rowspan-continuation slots: in a spreadsheet a
                // merged region keeps its value only in the top-left anchor cell; the rest stay empty/absent.
                if (cell is null || cell.IsRowSpanContinuation) continue;

                if (cell.ColSpan > 1 || cell.RowSpan > 1)
                {
                    int lastRow = Math.Min(rows, r + cell.RowSpan);
                    int lastCol = Math.Min(cols, c + cell.ColSpan);
                    merges.Add(CellRef(r + 1, c + 1) + ":" + CellRef(lastRow, lastCol));
                }

                AppendCell(sb, r + 1, c + 1, cell.Text);
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");

        if (merges.Count > 0)
        {
            sb.Append("<mergeCells count=\"").Append(merges.Count).Append("\">");
            foreach (var m in merges)
            {
                sb.Append("<mergeCell ref=\"").Append(m).Append("\"/>");
            }
            sb.Append("</mergeCells>");
        }

        sb.Append("</worksheet>");
        return sb.ToString();
    }

    /// <summary>
    /// Emits one cell as an inline string (or omits the value element entirely when empty).
    /// </summary>
    private static void AppendCell(StringBuilder sb, int row, int col, string text)
    {
        string reference = CellRef(row, col);
        if (string.IsNullOrEmpty(text))
        {
            sb.Append("<c r=\"").Append(reference).Append("\"/>");
            return;
        }

        sb.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
          .Append(StructureDocxExporter.Xml(text)).Append("</t></is></c>");
    }

    /// <summary>
    /// Builds an A1-style cell reference from 1-based <paramref name="row"/>/<paramref name="col"/>.
    /// </summary>
    private static string CellRef(int row, int col) => ColumnName(col) + row.ToString();

    /// <summary>
    /// Converts a 1-based column index to its Excel letter(s) (1→A, 26→Z, 27→AA …).
    /// </summary>
    private static string ColumnName(int col)
    {
        Span<char> buffer = stackalloc char[8];
        int i = buffer.Length;
        int n = Math.Max(1, col);
        while (n > 0)
        {
            int rem = (n - 1) % 26;
            buffer[--i] = (char)('A' + rem);
            n = (n - 1) / 26;
        }
        return new string(buffer[i..]);
    }
}
