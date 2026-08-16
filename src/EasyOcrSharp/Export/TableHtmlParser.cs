using System.Net;
using System.Text;

namespace EasyOcrSharp.Export;

/// <summary>
/// A parsed HTML table: a rectangular grid of cell strings plus whether the first row was made of
/// header (<c>&lt;th&gt;</c>) cells.
/// </summary>
public sealed record TableGrid
{
    /// <summary>
    /// Gets the table's cells, row-major. Every row has the same length — short rows are padded with
    /// empty strings so consumers can index without bounds-checking each row.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }

    /// <summary>
    /// Gets a value indicating whether the first row consisted of <c>&lt;th&gt;</c> cells, which is the
    /// usual signal that it holds column names rather than data.
    /// </summary>
    public bool HasHeaderRow { get; init; }

    /// <summary>Gets the number of rows.</summary>
    public int RowCount => Rows.Count;

    /// <summary>Gets the number of columns (0 when the table is empty).</summary>
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows[0].Count;

    /// <summary>An empty grid, returned for null, blank or table-less input.</summary>
    public static TableGrid Empty { get; } = new() { Rows = Array.Empty<IReadOnlyList<string>>() };
}

/// <summary>
/// Converts the HTML table markup produced by document-structure analysis into a plain rectangular
/// grid of strings.
/// </summary>
/// <remarks>
/// <para>
/// PP-StructureV3 recovers tables as HTML, which is faithful but awkward for .NET consumers who want
/// rows and columns. This is a small, forgiving, dependency-free parser aimed squarely at that markup
/// rather than at the web: it understands <c>table</c>/<c>tr</c>/<c>td</c>/<c>th</c> with
/// <c>rowspan</c>/<c>colspan</c>, ignores every other tag while keeping its text, and never throws on
/// malformed input — unclosed tags, stray text, missing <c>tbody</c> and mixed casing all parse to the
/// best available interpretation.
/// </para>
/// <para>
/// <b>Merged cells are expanded, not preserved.</b> A cell spanning three columns becomes the same
/// value repeated in three columns, which is what <c>DataTable</c> and CSV consumers expect; the
/// original span is not recoverable from the result.
/// </para>
/// </remarks>
public static class TableHtmlParser
{
    // Guards against hostile or degenerate markup: a single colspan="1000000" would otherwise
    // materialize a million columns. These bounds are far above any real recovered table.
    private const int MaxSpan = 512;
    private const int MaxRows = 4096;
    private const int MaxColumns = 1024;
    private const int MaxCells = 262_144;

    /// <summary>
    /// Parses the first <c>&lt;table&gt;</c> found in <paramref name="html"/> into a rectangular grid.
    /// Returns <see cref="TableGrid.Empty"/> for null, blank, or cell-less input.
    /// </summary>
    /// <param name="html">HTML table markup, e.g. <c>StructureBlock.TableHtml</c>.</param>
    public static TableGrid Parse(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return TableGrid.Empty;
        }

        var parsedRows = ScanRows(html);
        if (parsedRows.Count == 0)
        {
            return TableGrid.Empty;
        }

        var grid = BuildGrid(parsedRows);
        if (grid.Count == 0)
        {
            return TableGrid.Empty;
        }

        bool headerRow = parsedRows[0].Count > 0 && parsedRows[0].TrueForAll(c => c.IsHeader);

        return new TableGrid { Rows = grid, HasHeaderRow = headerRow };
    }

    /// <summary>
    /// Parses <paramref name="html"/> and returns just the cell grid — a shorthand for
    /// <c>Parse(html).Rows</c>.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ToRows(string? html) => Parse(html).Rows;

    /// <summary>A cell as it appeared in the markup, before spans are expanded.</summary>
    private readonly record struct RawCell(string Text, int ColSpan, int RowSpan, bool IsHeader);

    /// <summary>
    /// Walks the markup once, emitting one list of <see cref="RawCell"/> per <c>&lt;tr&gt;</c>.
    /// Everything that is not a row or cell boundary is treated as cell text.
    /// </summary>
    private static List<List<RawCell>> ScanRows(string html)
    {
        var rows = new List<List<RawCell>>();
        List<RawCell>? currentRow = null;
        var cell = new StringBuilder();
        bool inCell = false;
        bool cellIsHeader = false;
        int colSpan = 1;
        int rowSpan = 1;
        int cellCount = 0;

        void CommitCell()
        {
            if (!inCell)
            {
                return;
            }

            currentRow ??= new List<RawCell>();
            if (cellCount < MaxCells)
            {
                currentRow.Add(new RawCell(Clean(cell.ToString()), colSpan, rowSpan, cellIsHeader));
                cellCount++;
            }

            cell.Clear();
            inCell = false;
            colSpan = 1;
            rowSpan = 1;
        }

        void CommitRow()
        {
            CommitCell();
            if (currentRow is { Count: > 0 } && rows.Count < MaxRows)
            {
                rows.Add(currentRow);
            }

            currentRow = null;
        }

        int i = 0;
        while (i < html.Length)
        {
            char ch = html[i];
            if (ch != '<')
            {
                if (inCell)
                {
                    cell.Append(ch);
                }

                i++;
                continue;
            }

            // An unterminated '<' is literal text, not a tag.
            int end = FindTagEnd(html, i);
            if (end < 0)
            {
                if (inCell)
                {
                    cell.Append(html.AsSpan(i));
                }

                break;
            }

            var tag = html.AsSpan(i + 1, end - i - 1);
            i = end + 1;

            if (tag.StartsWith("!--", StringComparison.Ordinal))
            {
                continue;   // comment; FindTagEnd already consumed through '-->'
            }

            if (tag.Length == 0 || tag[0] == '!' || tag[0] == '?')
            {
                continue;   // doctype / processing instruction
            }

            bool closing = tag[0] == '/';
            var nameSpan = closing ? tag[1..] : tag;
            int nameLength = 0;
            while (nameLength < nameSpan.Length && !char.IsWhiteSpace(nameSpan[nameLength]) && nameSpan[nameLength] != '/')
            {
                nameLength++;
            }

            var name = nameSpan[..nameLength];

            if (name.Equals("tr", StringComparison.OrdinalIgnoreCase))
            {
                // Both <tr> and </tr> close the row in progress; an unclosed row is common in
                // generated markup and must not swallow the next one.
                CommitRow();
                if (!closing)
                {
                    currentRow = new List<RawCell>();
                }
            }
            else if (name.Equals("td", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("th", StringComparison.OrdinalIgnoreCase))
            {
                CommitCell();
                if (!closing)
                {
                    inCell = true;
                    cellIsHeader = name.Equals("th", StringComparison.OrdinalIgnoreCase);
                    colSpan = ReadSpan(nameSpan[nameLength..], "colspan");
                    rowSpan = ReadSpan(nameSpan[nameLength..], "rowspan");
                    currentRow ??= new List<RawCell>();
                }
            }
            else if (name.Equals("table", StringComparison.OrdinalIgnoreCase) && closing)
            {
                CommitRow();
                break;      // only the first table in the fragment
            }
            else if (name.Equals("br", StringComparison.OrdinalIgnoreCase) && inCell)
            {
                // A line break inside a cell becomes a space: cells are single-line values here, and a
                // newline would otherwise have to be quoted by every downstream CSV writer.
                cell.Append(' ');
            }

            // Every other tag (<b>, <span>, <p>, <tbody>, ...) is skipped while its text is kept.
        }

        CommitRow();
        return rows;
    }

    /// <summary>
    /// Finds the index of the '&gt;' closing the tag starting at <paramref name="start"/>, honouring
    /// quoted attribute values (so <c>title="a &gt; b"</c> does not end the tag early) and running to
    /// <c>--&gt;</c> for comments. Returns -1 when the tag is never terminated.
    /// </summary>
    private static int FindTagEnd(string html, int start)
    {
        if (html.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal))
        {
            int close = html.IndexOf("-->", start + 4, StringComparison.Ordinal);
            return close < 0 ? -1 : close + 2;
        }

        char quote = '\0';
        for (int i = start + 1; i < html.Length; i++)
        {
            char ch = html[i];
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (ch == '>')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reads a <c>colspan</c>/<c>rowspan</c> attribute out of an attribute span, accepting values in
    /// double quotes, single quotes or none at all. Missing, unparsable or out-of-range values fall
    /// back to 1, and anything huge is clamped rather than trusted.
    /// </summary>
    private static int ReadSpan(ReadOnlySpan<char> attributes, string attributeName)
    {
        int at = attributes.IndexOf(attributeName, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return 1;
        }

        var rest = attributes[(at + attributeName.Length)..];
        int eq = rest.IndexOf('=');
        if (eq < 0)
        {
            return 1;
        }

        rest = rest[(eq + 1)..].TrimStart();
        if (rest.Length == 0)
        {
            return 1;
        }

        if (rest[0] is '"' or '\'')
        {
            char quote = rest[0];
            rest = rest[1..];
            int close = rest.IndexOf(quote);
            if (close >= 0)
            {
                rest = rest[..close];
            }
        }
        else
        {
            int stop = 0;
            while (stop < rest.Length && !char.IsWhiteSpace(rest[stop]) && rest[stop] != '/')
            {
                stop++;
            }

            rest = rest[..stop];
        }

        return int.TryParse(rest.Trim(), out int value) && value >= 1
            ? Math.Min(value, MaxSpan)
            : 1;
    }

    /// <summary>
    /// Places the scanned cells into a rectangular grid, expanding <c>colspan</c> horizontally and
    /// <c>rowspan</c> vertically. Cells are laid into the first free column of their row, which is what
    /// makes a cell carried down by a rowspan push later cells to the right, exactly as a browser
    /// lays the table out.
    /// </summary>
    private static List<IReadOnlyList<string>> BuildGrid(List<List<RawCell>> rows)
    {
        var grid = new List<List<string?>>();

        for (int r = 0; r < rows.Count; r++)
        {
            EnsureRow(grid, r);
            int column = 0;

            foreach (var cell in rows[r])
            {
                while (column < grid[r].Count && grid[r][column] is not null)
                {
                    column++;
                }

                if (column >= MaxColumns)
                {
                    break;
                }

                int colSpan = Math.Min(cell.ColSpan, MaxColumns - column);
                int rowSpan = Math.Min(cell.RowSpan, MaxRows - r);

                for (int dr = 0; dr < rowSpan; dr++)
                {
                    EnsureRow(grid, r + dr);
                    for (int dc = 0; dc < colSpan; dc++)
                    {
                        SetCell(grid[r + dr], column + dc, cell.Text);
                    }
                }

                column += colSpan;
            }
        }

        int width = 0;
        foreach (var row in grid)
        {
            width = Math.Max(width, row.Count);
        }

        var result = new List<IReadOnlyList<string>>(grid.Count);
        foreach (var row in grid)
        {
            var line = new string[width];
            for (int c = 0; c < width; c++)
            {
                line[c] = c < row.Count ? row[c] ?? string.Empty : string.Empty;
            }

            result.Add(line);
        }

        // A rowspan on the last row can manufacture trailing rows that hold nothing.
        while (result.Count > 0 && AllEmpty(result[^1]))
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private static bool AllEmpty(IReadOnlyList<string> row)
    {
        foreach (var value in row)
        {
            if (value.Length != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureRow(List<List<string?>> grid, int index)
    {
        while (grid.Count <= index && grid.Count < MaxRows)
        {
            grid.Add(new List<string?>());
        }
    }

    private static void SetCell(List<string?> row, int column, string value)
    {
        while (row.Count <= column)
        {
            row.Add(null);
        }

        row[column] ??= value;
    }

    /// <summary>
    /// Turns raw cell content into a value: HTML entities decoded, all whitespace runs (including the
    /// newlines and indentation generated markup is full of) collapsed to single spaces, then trimmed.
    /// </summary>
    private static string Clean(string raw)
    {
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        // WebUtility handles the full named-entity set plus &#NNN;/&#xHH; and leaves unknown
        // ampersands alone, which is exactly the forgiving behaviour wanted here.
        string decoded = raw.Contains('&', StringComparison.Ordinal) ? WebUtility.HtmlDecode(raw) : raw;

        var builder = new StringBuilder(decoded.Length);
        bool pendingSpace = false;
        foreach (char ch in decoded)
        {
            // Treat the non-breaking space &nbsp; decodes to as whitespace too, otherwise cells come
            // out with invisible padding that breaks equality checks downstream.
            if (char.IsWhiteSpace(ch) || ch == ' ')
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
