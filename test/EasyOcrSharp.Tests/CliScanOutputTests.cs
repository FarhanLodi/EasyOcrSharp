using EasyOcrSharp.Cli.CommandLine;
using EasyOcrSharp.Cli.Internal;
using EasyOcrSharp.Models;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Unit tests for <c>scan</c>'s output layer: format selection, where documents land (stdout, one
/// file, or a directory), input-order reassembly from the parallel pipeline, and collision-free file
/// naming. No models are involved — results are hand-built.
/// </summary>
public sealed class CliScanOutputTests : IDisposable
{
    private readonly string _root;

    public CliScanOutputTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "easyocr-cli-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly CliOption Format = new("format", "-f", true, "Output format.", "name");

    private static readonly CliCommand Command = new("scan", "Recognize text.", "scan <input...>", [Format]);

    private static CliConsole Quiet => new(quiet: true);

    private static OcrResult Result(string text, int width = 200, int height = 50) => new()
    {
        FullText = text,
        Lines =
        [
            new OcrLine
            {
                Text = text,
                Confidence = 0.9,
                BoundingPolygon = [new OcrPoint(0, 0), new OcrPoint(width, 0), new OcrPoint(width, height), new OcrPoint(0, height)],
                BoundingBox = new OcrBoundingBox(0, 0, width, height),
            },
        ],
        Languages = ["en"],
        SourceWidth = width,
        SourceHeight = height,
    };

    private static CliScanReport Report(string source, string text, int? page = null) =>
        new() { Source = source, Page = page, Result = Result(text) };

    // ---------------------------------------------------------------- format parsing

    // OutputFormat is internal, so the theory data names it by string and the assertion compares names.
    [Theory]
    [InlineData(null, "Text")]
    [InlineData("text", "Text")]
    [InlineData("txt", "Text")]
    [InlineData("plain", "Text")]
    [InlineData("json", "Json")]
    [InlineData("hocr", "Hocr")]
    [InlineData("alto", "Alto")]
    [InlineData("xml", "Alto")]
    [InlineData("tsv", "Tsv")]
    [InlineData("JSON", "Json")]
    public void Every_documented_format_spelling_is_accepted(string? spelling, string expected)
    {
        var args = spelling is null
            ? ArgParser.Parse(Command, [])
            : ArgParser.Parse(Command, ["--format", spelling]);

        Assert.Equal(expected, ScanOutputWriter.ParseFormat(args, Format.Name).ToString());
    }

    [Fact]
    public void An_unknown_format_is_rejected_and_lists_the_valid_ones()
    {
        var args = ArgParser.Parse(Command, ["--format", "pdf"]);

        var ex = Assert.Throws<CliUsageException>(() => ScanOutputWriter.ParseFormat(args, Format.Name));

        Assert.Contains("text, json, hocr, alto, tsv", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- single-file mode

    [Fact]
    public void A_single_output_path_collects_every_result_into_that_file()
    {
        var path = Path.Combine(_root, "out.txt");
        var writer = new ScanOutputWriter(OutputFormat.Text, path, multipleExpected: false, Quiet);

        writer.Add(0, Report("a.png", "alpha"));
        writer.Flush();

        Assert.True(File.Exists(path));
        Assert.Contains("alpha", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.False(writer.IsDirectoryMode);
    }

    [Fact]
    public void Results_are_emitted_in_input_order_regardless_of_completion_order()
    {
        // The batch pipeline finishes images out of order; the sort key restores what the user typed.
        var path = Path.Combine(_root, "ordered.txt");
        var writer = new ScanOutputWriter(OutputFormat.Text, path, multipleExpected: false, Quiet);

        writer.Add(2, Report("c.png", "gamma"));
        writer.Add(0, Report("a.png", "alpha"));
        writer.Add(1, Report("b.png", "beta"));
        writer.Flush();

        var text = File.ReadAllText(path);
        Assert.True(
            text.IndexOf("alpha", StringComparison.Ordinal) < text.IndexOf("beta", StringComparison.Ordinal)
            && text.IndexOf("beta", StringComparison.Ordinal) < text.IndexOf("gamma", StringComparison.Ordinal),
            $"expected alpha < beta < gamma, got:\n{text}");
    }

    [Fact]
    public void A_missing_parent_directory_is_created_for_a_single_output_file()
    {
        var path = Path.Combine(_root, "deep", "nested", "out.txt");
        var writer = new ScanOutputWriter(OutputFormat.Text, path, multipleExpected: false, Quiet);

        writer.Add(0, Report("a.png", "alpha"));
        writer.Flush();

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Json_output_is_always_an_array_even_for_a_single_input()
    {
        // So a consumer never has to branch on "one result or many".
        var path = Path.Combine(_root, "one.json");
        var writer = new ScanOutputWriter(OutputFormat.Json, path, multipleExpected: false, Quiet);

        writer.Add(0, Report("a.png", "alpha"));
        writer.Flush();

        var json = File.ReadAllText(path).Trim();
        Assert.StartsWith("[", json, StringComparison.Ordinal);
        Assert.EndsWith("]", json, StringComparison.Ordinal);

        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, parsed.RootElement.ValueKind);
        Assert.Equal(1, parsed.RootElement.GetArrayLength());
    }

    [Fact]
    public void Json_output_of_several_inputs_is_valid_and_ordered()
    {
        var path = Path.Combine(_root, "many.json");
        var writer = new ScanOutputWriter(OutputFormat.Json, path, multipleExpected: false, Quiet);

        writer.Add(1, Report("b.png", "beta"));
        writer.Add(0, Report("a.png", "alpha"));
        writer.Flush();

        using var parsed = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var items = parsed.RootElement.EnumerateArray().ToArray();

        Assert.Equal(2, items.Length);
        Assert.Equal("a.png", items[0].GetProperty("Source").GetString());
        Assert.Equal("b.png", items[1].GetProperty("Source").GetString());
    }

    [Fact]
    public void Json_output_writes_non_ascii_text_verbatim()
    {
        // Recognized Cyrillic (or CJK, Arabic, …) reaching the file as \uXXXX escapes is valid JSON but
        // unreadable in a plain editor, which is the whole point of scanning to a file.
        var path = Path.Combine(_root, "cyrillic.json");
        var writer = new ScanOutputWriter(OutputFormat.Json, path, multipleExpected: false, Quiet);

        writer.Add(0, Report("ru.png", "ИСТОРИЯ РОССИЙСКОГО ГОСУДАРСТВА"));
        writer.Flush();

        var json = File.ReadAllText(path);

        Assert.Contains("ИСТОРИЯ РОССИЙСКОГО ГОСУДАРСТВА", json, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\u04", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Json_output_still_omits_null_properties()
    {
        // The unescaping context is built from an explicit options instance, which does not inherit the
        // [JsonSourceGenerationOptions] attribute — so the null handling has to be restated there, and
        // this pins that it was.
        var path = Path.Combine(_root, "nulls.json");
        var writer = new ScanOutputWriter(OutputFormat.Json, path, multipleExpected: false, Quiet);

        writer.Add(0, Report("a.png", "alpha"));   // no Page, no Error
        writer.Flush();

        using var parsed = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var item = parsed.RootElement.EnumerateArray().Single();

        Assert.False(item.TryGetProperty("Error", out _));
        Assert.False(item.TryGetProperty("Page", out _));
    }

    [Fact]
    public void A_failed_input_still_appears_in_json_carrying_its_error()
    {
        var path = Path.Combine(_root, "failed.json");
        var writer = new ScanOutputWriter(OutputFormat.Json, path, multipleExpected: false, Quiet);

        writer.Add(0, new CliScanReport { Source = "broken.png", Error = "could not decode" });
        writer.Flush();

        using var parsed = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var item = parsed.RootElement.EnumerateArray().Single();

        Assert.Equal("broken.png", item.GetProperty("Source").GetString());
        Assert.Equal("could not decode", item.GetProperty("Error").GetString());
    }

    [Fact]
    public void Text_output_of_several_inputs_separates_them_with_newlines()
    {
        var path = Path.Combine(_root, "joined.txt");
        var writer = new ScanOutputWriter(OutputFormat.Text, path, multipleExpected: false, Quiet);

        writer.Add(0, Report("a.png", "alpha"));
        writer.Add(1, Report("b.png", "beta"));
        writer.Flush();

        Assert.Equal("alpha\nbeta\n", File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Flushing_with_nothing_added_writes_an_empty_document_rather_than_failing()
    {
        var path = Path.Combine(_root, "empty.txt");
        var writer = new ScanOutputWriter(OutputFormat.Text, path, multipleExpected: false, Quiet);

        writer.Flush();

        Assert.True(File.Exists(path));
        Assert.Equal("", File.ReadAllText(path));
    }

    // ---------------------------------------------------------------- directory mode

    [Fact]
    public void Multiple_expected_inputs_turn_an_output_path_into_a_directory()
    {
        var directory = Path.Combine(_root, "out");
        var writer = new ScanOutputWriter(OutputFormat.Text, directory, multipleExpected: true, Quiet);

        Assert.True(writer.IsDirectoryMode);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void A_trailing_separator_marks_the_output_as_a_directory_even_for_one_input()
    {
        var directory = Path.Combine(_root, "explicit") + Path.DirectorySeparatorChar;
        var writer = new ScanOutputWriter(OutputFormat.Text, directory, multipleExpected: false, Quiet);

        Assert.True(writer.IsDirectoryMode);
    }

    [Fact]
    public void An_existing_directory_is_treated_as_one()
    {
        var directory = Path.Combine(_root, "already-there");
        Directory.CreateDirectory(directory);

        Assert.True(new ScanOutputWriter(OutputFormat.Text, directory, multipleExpected: false, Quiet).IsDirectoryMode);
    }

    [Fact]
    public void Directory_mode_writes_one_file_per_result_named_after_its_input()
    {
        var directory = Path.Combine(_root, "per-input");
        var writer = new ScanOutputWriter(OutputFormat.Text, directory, multipleExpected: true, Quiet);

        writer.Add(0, Report(Path.Combine("scans", "alpha.png"), "alpha"));
        writer.Add(1, Report(Path.Combine("scans", "beta.png"), "beta"));
        writer.Flush();

        Assert.True(File.Exists(Path.Combine(directory, "alpha.txt")));
        Assert.True(File.Exists(Path.Combine(directory, "beta.txt")));

        // Multi-input text runs are labelled, so the body is contained rather than the whole file.
        Assert.Contains("alpha", File.ReadAllText(Path.Combine(directory, "alpha.txt")), StringComparison.Ordinal);
        Assert.Contains("beta", File.ReadAllText(Path.Combine(directory, "beta.txt")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Text", ".txt")]
    [InlineData("Json", ".json")]
    [InlineData("Hocr", ".hocr.html")]
    [InlineData("Alto", ".alto.xml")]
    [InlineData("Tsv", ".tsv")]
    public void Each_format_gets_its_own_file_extension(string formatName, string extension)
    {
        var format = Enum.Parse<OutputFormat>(formatName);
        var directory = Path.Combine(_root, "ext-" + formatName);
        var writer = new ScanOutputWriter(format, directory, multipleExpected: true, Quiet);

        writer.Add(0, Report("page.png", "text"));

        Assert.True(File.Exists(Path.Combine(directory, "page" + extension)));
    }

    [Fact]
    public void Inputs_from_different_folders_sharing_a_name_do_not_overwrite_each_other()
    {
        var directory = Path.Combine(_root, "collisions");
        var writer = new ScanOutputWriter(OutputFormat.Text, directory, multipleExpected: true, Quiet);

        writer.Add(0, Report(Path.Combine("january", "invoice.png"), "first"));
        writer.Add(1, Report(Path.Combine("february", "invoice.png"), "second"));
        writer.Add(2, Report(Path.Combine("march", "invoice.png"), "third"));

        // Each landed in its own file rather than overwriting the previous one.
        Assert.Contains("first", File.ReadAllText(Path.Combine(directory, "invoice.txt")), StringComparison.Ordinal);
        Assert.Contains("second", File.ReadAllText(Path.Combine(directory, "invoice-2.txt")), StringComparison.Ordinal);
        Assert.Contains("third", File.ReadAllText(Path.Combine(directory, "invoice-3.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_pages_are_written_as_separate_zero_padded_files()
    {
        var directory = Path.Combine(_root, "pages");
        var writer = new ScanOutputWriter(OutputFormat.Text, directory, multipleExpected: true, Quiet);

        writer.Add(0, Report("scan.pdf", "page one", page: 1));
        writer.Add(1, Report("scan.pdf", "page two", page: 2));
        writer.Add(2, Report("scan.pdf", "page ten", page: 10));

        Assert.True(File.Exists(Path.Combine(directory, "scan.p001.txt")));
        Assert.True(File.Exists(Path.Combine(directory, "scan.p002.txt")));
        Assert.True(File.Exists(Path.Combine(directory, "scan.p010.txt")));
    }

    [Fact]
    public void A_failed_input_produces_no_document_in_directory_mode()
    {
        // The error already went to stderr; an empty file would be worse than none.
        var directory = Path.Combine(_root, "with-failure");
        var writer = new ScanOutputWriter(OutputFormat.Text, directory, multipleExpected: true, Quiet);

        writer.Add(0, new CliScanReport { Source = "broken.png", Error = "could not decode" });
        writer.Flush();

        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void Directory_mode_flush_is_a_no_op_because_results_are_written_as_they_complete()
    {
        var directory = Path.Combine(_root, "streamed");
        var writer = new ScanOutputWriter(OutputFormat.Text, directory, multipleExpected: true, Quiet);

        writer.Add(0, Report("a.png", "alpha"));
        int before = Directory.GetFiles(directory).Length;
        writer.Flush();

        Assert.Equal(1, before);
        Assert.Equal(before, Directory.GetFiles(directory).Length);
    }

    // ---------------------------------------------------------------- headers

    [Fact]
    public void A_multi_input_run_writes_one_file_when_the_destination_names_one()
    {
        // Directory mode follows the destination, not the input count. `-o report.hocr.html` on a PDF used
        // to produce a *directory* of that name — breaking `scan report.pdf --format hocr -o report.hocr.html`,
        // the example in this command's own help text.
        var path = Path.Combine(_root, "headers.txt");
        var writer = new ScanOutputWriter(OutputFormat.Text, path, multipleExpected: true, Quiet);

        Assert.False(writer.IsDirectoryMode);

        writer.Add(0, Report("a.png", "alpha"));
        writer.Add(1, Report("b.png", "beta"));
        writer.Flush();

        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("alpha", text);
        Assert.Contains("beta", text);
    }

    [Fact]
    public void A_multi_input_run_writes_a_directory_when_the_destination_has_no_extension()
    {
        var path = Path.Combine(_root, "out-dir-no-ext");
        var writer = new ScanOutputWriter(OutputFormat.Text, path, multipleExpected: true, Quiet);

        Assert.True(writer.IsDirectoryMode);
    }

    [Fact]
    public void A_single_pdf_run_honours_an_explicit_output_file_name()
    {
        // The regression this guards: any PDF input set multipleExpected, so even a one-page document with an
        // explicit -o filename went to a directory, and `scan in.pdf -o out.json && jq . out.json` broke.
        var path = Path.Combine(_root, "report.json");
        var writer = new ScanOutputWriter(OutputFormat.Json, path, multipleExpected: true, Quiet);

        Assert.False(writer.IsDirectoryMode);

        writer.Add(0, Report("report.pdf", "page one"));
        writer.Flush();

        // Flush also produces the single JSON array that directory mode never emitted.
        var json = File.ReadAllText(path).TrimStart();
        Assert.StartsWith("[", json);
        Assert.Contains("page one", json);
    }

    [Fact]
    public void Headers_appear_only_in_text_format_for_multi_input_runs()
    {
        var directory = Path.Combine(_root, "headers-dir");
        var writer = new ScanOutputWriter(OutputFormat.Text, directory, multipleExpected: true, Quiet);

        writer.Add(0, Report("alpha.png", "alpha"));

        Assert.Contains("===== alpha.png =====", File.ReadAllText(Path.Combine(directory, "alpha.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_header_names_the_page_number()
    {
        var directory = Path.Combine(_root, "page-headers");
        var writer = new ScanOutputWriter(OutputFormat.Text, directory, multipleExpected: true, Quiet);

        writer.Add(0, Report("scan.pdf", "body", page: 4));

        Assert.Contains("(page 4)", File.ReadAllText(Path.Combine(directory, "scan.p004.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public void Structured_formats_never_get_text_headers_that_would_corrupt_them()
    {
        var directory = Path.Combine(_root, "structured");
        var writer = new ScanOutputWriter(OutputFormat.Alto, directory, multipleExpected: true, Quiet);

        writer.Add(0, Report("alpha.png", "alpha"));

        var alto = File.ReadAllText(Path.Combine(directory, "alpha.alto.xml"));
        Assert.DoesNotContain("=====", alto, StringComparison.Ordinal);
        Assert.StartsWith("<?xml", alto.TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public void Hocr_output_is_html_carrying_the_recognized_text()
    {
        var directory = Path.Combine(_root, "hocr");
        var writer = new ScanOutputWriter(OutputFormat.Hocr, directory, multipleExpected: true, Quiet);

        writer.Add(0, Report("alpha.png", "alpha"));

        var hocr = File.ReadAllText(Path.Combine(directory, "alpha.hocr.html"));
        Assert.Contains("ocr_line", hocr, StringComparison.Ordinal);
        Assert.Contains("alpha", hocr, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- concurrency

    [Fact]
    public void Concurrent_adds_are_safe_and_lose_nothing()
    {
        // The batch pipeline calls Add from many threads at once.
        var directory = Path.Combine(_root, "parallel");
        var writer = new ScanOutputWriter(OutputFormat.Text, directory, multipleExpected: true, Quiet);

        Parallel.For(0, 100, i => writer.Add(i, Report($"input-{i}.png", $"text {i}")));
        writer.Flush();

        Assert.Equal(100, Directory.GetFiles(directory).Length);
    }

    [Fact]
    public void Concurrent_adds_to_a_single_file_preserve_input_order()
    {
        var path = Path.Combine(_root, "parallel.txt");
        var writer = new ScanOutputWriter(OutputFormat.Text, path, multipleExpected: false, Quiet);

        Parallel.For(0, 50, i => writer.Add(i, Report($"input-{i}.png", $"line{i:D2}")));
        writer.Flush();

        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();
        Assert.Equal(50, lines.Length);
        Assert.Equal(lines.OrderBy(l => l, StringComparer.Ordinal), lines);
    }
}
