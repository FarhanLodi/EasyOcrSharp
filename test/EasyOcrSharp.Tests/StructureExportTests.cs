using EasyOcrSharp.Export;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Unit tests for <see cref="TableHtmlParser"/> and the CSV / <c>DataTable</c> conversions in
/// <see cref="StructureExportExtensions"/>. All pure functions over HTML strings — no models, no
/// network, no structure-engine session.
/// </summary>
public class StructureExportTests
{
    private static IReadOnlyList<IReadOnlyList<string>> Rows(string html) => TableHtmlParser.ToRows(html);

    private static string Cell(string html, int row, int column) => Rows(html)[row][column];

    // ---------------------------------------------------------------- basic shape

    [Fact]
    public void Simple_grid_parses_to_rows_and_columns()
    {
        const string html = "<table><tr><td>a</td><td>b</td></tr><tr><td>c</td><td>d</td></tr></table>";

        var rows = Rows(html);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "a", "b" }, rows[0]);
        Assert.Equal(new[] { "c", "d" }, rows[1]);
    }

    [Fact]
    public void Header_row_of_th_cells_is_reported()
    {
        var grid = TableHtmlParser.Parse(
            "<table><tr><th>Item</th><th>Qty</th></tr><tr><td>Nut</td><td>4</td></tr></table>");

        Assert.True(grid.HasHeaderRow);
        Assert.Equal(2, grid.RowCount);
        Assert.Equal(2, grid.ColumnCount);
    }

    [Fact]
    public void A_body_only_table_is_not_reported_as_having_a_header()
    {
        var grid = TableHtmlParser.Parse("<table><tr><td>Nut</td><td>4</td></tr></table>");

        Assert.False(grid.HasHeaderRow);
    }

    [Fact]
    public void Tbody_thead_and_tfoot_wrappers_are_transparent()
    {
        const string html = """
            <table>
              <thead><tr><th>H</th></tr></thead>
              <tbody><tr><td>B</td></tr></tbody>
              <tfoot><tr><td>F</td></tr></tfoot>
            </table>
            """;

        var rows = Rows(html);

        Assert.Equal(3, rows.Count);
        Assert.Equal("H", rows[0][0]);
        Assert.Equal("B", rows[1][0]);
        Assert.Equal("F", rows[2][0]);
    }

    // ---------------------------------------------------------------- spans

    [Fact]
    public void Colspan_repeats_the_value_across_the_spanned_columns()
    {
        const string html = "<table><tr><td colspan=\"3\">wide</td></tr><tr><td>a</td><td>b</td><td>c</td></tr></table>";

        var rows = Rows(html);

        Assert.Equal(new[] { "wide", "wide", "wide" }, rows[0]);
        Assert.Equal(new[] { "a", "b", "c" }, rows[1]);
    }

    [Fact]
    public void Rowspan_carries_the_value_down_and_pushes_later_cells_right()
    {
        const string html = """
            <table>
              <tr><td rowspan="2">tall</td><td>b1</td></tr>
              <tr><td>b2</td></tr>
            </table>
            """;

        var rows = Rows(html);

        Assert.Equal(new[] { "tall", "b1" }, rows[0]);
        // The carried-down cell occupies column 0, so 'b2' lands in column 1 rather than column 0.
        Assert.Equal(new[] { "tall", "b2" }, rows[1]);
    }

    [Fact]
    public void Combined_row_and_col_spans_fill_the_whole_rectangle()
    {
        const string html = """
            <table>
              <tr><td rowspan="2" colspan="2">block</td><td>x</td></tr>
              <tr><td>y</td></tr>
            </table>
            """;

        var rows = Rows(html);

        Assert.Equal(new[] { "block", "block", "x" }, rows[0]);
        Assert.Equal(new[] { "block", "block", "y" }, rows[1]);
    }

    [Fact]
    public void Unquoted_and_single_quoted_span_attributes_are_read()
    {
        Assert.Equal(3, Rows("<table><tr><td colspan=3>w</td></tr></table>")[0].Count);
        Assert.Equal(3, Rows("<table><tr><td colspan='3'>w</td></tr></table>")[0].Count);
    }

    [Theory]
    [InlineData("colspan=\"0\"")]
    [InlineData("colspan=\"-4\"")]
    [InlineData("colspan=\"abc\"")]
    [InlineData("colspan")]
    public void A_nonsensical_span_falls_back_to_one_column(string attribute)
    {
        var rows = Rows($"<table><tr><td {attribute}>v</td><td>w</td></tr></table>");

        Assert.Equal(new[] { "v", "w" }, rows[0]);
    }

    [Fact]
    public void An_enormous_span_is_clamped_rather_than_trusted()
    {
        // Guards the parser against hostile markup manufacturing millions of columns.
        var rows = Rows("<table><tr><td colspan=\"100000\">v</td></tr></table>");

        Assert.NotEmpty(rows);
        Assert.InRange(rows[0].Count, 1, 1024);
    }

    [Fact]
    public void Short_rows_are_padded_so_every_row_has_the_same_width()
    {
        const string html = "<table><tr><td>a</td><td>b</td><td>c</td></tr><tr><td>d</td></tr></table>";

        var rows = Rows(html);

        Assert.All(rows, row => Assert.Equal(3, row.Count));
        Assert.Equal(new[] { "d", "", "" }, rows[1]);
    }

    // ---------------------------------------------------------------- cell content

    [Fact]
    public void Named_and_numeric_entities_are_decoded()
    {
        const string html = "<table><tr><td>a &amp; b</td><td>&lt;tag&gt;</td><td>&#65;&#x42;</td></tr></table>";

        var rows = Rows(html);

        Assert.Equal("a & b", rows[0][0]);
        Assert.Equal("<tag>", rows[0][1]);
        Assert.Equal("AB", rows[0][2]);
    }

    [Fact]
    public void Nbsp_is_treated_as_whitespace_rather_than_left_as_an_invisible_character()
    {
        // Otherwise cells arrive with padding that silently breaks equality checks downstream.
        Assert.Equal("Total", Cell("<table><tr><td>&nbsp;Total&nbsp;</td></tr></table>", 0, 0));
    }

    [Fact]
    public void Nested_markup_is_stripped_but_its_text_is_kept()
    {
        Assert.Equal("bold text", Cell("<table><tr><td><b>bold</b> <span>text</span></td></tr></table>", 0, 0));
    }

    [Fact]
    public void Whitespace_and_newlines_from_generated_markup_are_collapsed()
    {
        const string html = "<table>\n  <tr>\n    <td>  Line   one\n       wrapped  </td>\n  </tr>\n</table>";

        Assert.Equal("Line one wrapped", Cell(html, 0, 0));
    }

    [Fact]
    public void A_br_inside_a_cell_becomes_a_space()
    {
        Assert.Equal("one two", Cell("<table><tr><td>one<br/>two</td></tr></table>", 0, 0));
    }

    [Fact]
    public void Uppercase_and_mixed_case_tags_are_recognized()
    {
        var rows = Rows("<TABLE><TR><TH>H</TH></TR><Tr><Td>v</Td></Tr></TABLE>");

        Assert.Equal(2, rows.Count);
        Assert.Equal("H", rows[0][0]);
        Assert.Equal("v", rows[1][0]);
    }

    [Fact]
    public void A_quoted_angle_bracket_in_an_attribute_does_not_end_the_tag_early()
    {
        Assert.Equal("v", Cell("<table><tr><td title=\"a > b\">v</td></tr></table>", 0, 0));
    }

    [Fact]
    public void Comments_are_ignored()
    {
        Assert.Equal("v", Cell("<table><tr><td><!-- note --{}>v</td></tr></table>".Replace("--{}>", "-->"), 0, 0));
    }

    // ---------------------------------------------------------------- malformed input

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not html at all")]
    [InlineData("<table></table>")]
    [InlineData("<p>a paragraph, no table</p>")]
    public void Empty_or_table_less_input_yields_an_empty_grid(string? html)
    {
        var grid = TableHtmlParser.Parse(html);

        Assert.Empty(grid.Rows);
        Assert.Equal(0, grid.RowCount);
        Assert.Equal(0, grid.ColumnCount);
        Assert.False(grid.HasHeaderRow);
    }

    [Fact]
    public void Unclosed_cells_and_rows_still_parse()
    {
        // Generated table markup frequently omits closing tags.
        var rows = Rows("<table><tr><td>a<td>b<tr><td>c<td>d</table>");

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "a", "b" }, rows[0]);
        Assert.Equal(new[] { "c", "d" }, rows[1]);
    }

    [Fact]
    public void An_unterminated_tag_does_not_throw()
    {
        var grid = TableHtmlParser.Parse("<table><tr><td>a</td><td>b");

        Assert.NotNull(grid);
        Assert.Equal("a", grid.Rows[0][0]);
    }

    [Fact]
    public void Stray_text_outside_cells_is_discarded()
    {
        Assert.Equal(new[] { "a" }, Rows("<table>junk<tr>more junk<td>a</td></tr>tail</table>")[0]);
    }

    [Fact]
    public void Only_the_first_table_in_a_fragment_is_parsed()
    {
        var rows = Rows("<table><tr><td>first</td></tr></table><table><tr><td>second</td></tr></table>");

        Assert.Single(rows);
        Assert.Equal("first", rows[0][0]);
    }

    // ---------------------------------------------------------------- CSV

    [Fact]
    public void Csv_quotes_only_the_fields_that_need_it()
    {
        var rows = new IReadOnlyList<string>[]
        {
            new[] { "plain", "has,comma", "has\"quote", "has\nnewline" },
        };

        string csv = rows.ToCsv();

        Assert.Equal("plain,\"has,comma\",\"has\"\"quote\",\"has\nnewline\"\r\n", csv);
    }

    [Fact]
    public void Csv_rows_are_terminated_with_crlf_per_rfc4180()
    {
        string csv = new IReadOnlyList<string>[] { new[] { "a" }, new[] { "b" } }.ToCsv();

        Assert.Equal("a\r\nb\r\n", csv);
    }

    [Fact]
    public void Csv_honours_an_alternate_delimiter_when_deciding_to_quote()
    {
        var rows = new IReadOnlyList<string>[] { new[] { "a;b", "c,d" } };

        // With ';' as the delimiter it is the semicolon that forces quoting, not the comma.
        Assert.Equal("\"a;b\";c,d\r\n", rows.ToCsv(';'));
    }

    [Fact]
    public void Csv_of_an_empty_grid_is_empty()
    {
        Assert.Equal(string.Empty, Array.Empty<IReadOnlyList<string>>().ToCsv());
    }

    // ---------------------------------------------------------------- DataTable

    [Fact]
    public void DataTable_uses_the_header_row_for_column_names_and_skips_it_as_data()
    {
        var grid = TableHtmlParser.Parse(
            "<table><tr><th>Item</th><th>Qty</th></tr><tr><td>Nut</td><td>4</td></tr></table>");

        using var table = grid.ToDataTable();

        Assert.Equal(new[] { "Item", "Qty" }, table.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName));
        Assert.Single(table.Rows);
        Assert.Equal("Nut", table.Rows[0]["Item"]);
        Assert.Equal("4", table.Rows[0]["Qty"]);
    }

    [Fact]
    public void DataTable_without_a_header_row_generates_column_names_and_keeps_every_row()
    {
        var grid = TableHtmlParser.Parse("<table><tr><td>a</td><td>b</td></tr></table>");

        using var table = grid.ToDataTable();

        Assert.Equal(new[] { "Column1", "Column2" }, table.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName));
        Assert.Single(table.Rows);
    }

    [Fact]
    public void DataTable_makes_blank_and_duplicate_header_names_usable()
    {
        // DataTable rejects both, so the converter has to repair them rather than throw.
        var grid = TableHtmlParser.Parse(
            "<table><tr><th>Name</th><th></th><th>Name</th></tr><tr><td>a</td><td>b</td><td>c</td></tr></table>");

        using var table = grid.ToDataTable();

        var names = table.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName).ToArray();
        Assert.Equal(3, names.Length);
        Assert.Equal(3, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("Name", names[0]);
        Assert.Equal("Column2", names[1]);
    }

    [Fact]
    public void DataTable_header_detection_can_be_overridden()
    {
        var grid = TableHtmlParser.Parse(
            "<table><tr><th>Item</th></tr><tr><td>Nut</td></tr></table>");

        using var asData = grid.ToDataTable(firstRowIsHeader: false);

        Assert.Equal("Column1", asData.Columns[0].ColumnName);
        Assert.Equal(2, asData.Rows.Count);
    }

    [Fact]
    public void DataTable_of_an_empty_grid_has_no_columns_or_rows()
    {
        using var table = TableGrid.Empty.ToDataTable();

        Assert.Empty(table.Columns);
        Assert.Empty(table.Rows);
    }
}
