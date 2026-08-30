using System.Text;
using System.Text.Json;
using EasyOcrSharp.Cli.CommandLine;
using EasyOcrSharp.Export;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Cli.Internal;

/// <summary>The document formats <c>scan</c> can emit.</summary>
internal enum OutputFormat
{
    /// <summary>Plain recognized text (the default).</summary>
    Text,

    /// <summary>The full result — geometry, confidences, timings — as JSON.</summary>
    Json,

    /// <summary>hOCR, the HTML layout format DMS tooling consumes.</summary>
    Hocr,

    /// <summary>ALTO XML v4, used by libraries and digitization workflows.</summary>
    Alto,

    /// <summary>Tesseract-style tab-separated values, one row per word.</summary>
    Tsv,
}

/// <summary>
/// Renders results in the requested format and decides where they land: a single file, one file per
/// input inside a directory, or stdout. Results arriving from the parallel batch pipeline are keyed by
/// their input position so stdout output is reassembled in the order the user typed the inputs, no
/// matter which image finished first.
/// </summary>
internal sealed class ScanOutputWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly OutputFormat _format;
    private readonly CliConsole _console;
    private readonly string? _filePath;
    private readonly string? _directory;
    private readonly bool _headers;
    private readonly List<(long Order, string Rendered)> _buffered = [];
    private readonly HashSet<string> _usedFileNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>
    /// Creates a writer for the resolved destination.
    /// </summary>
    /// <param name="format">Output format.</param>
    /// <param name="output">The <c>-o</c> value: a file path, a directory, or null for stdout.</param>
    /// <param name="multipleExpected">True when the run can produce more than one document.</param>
    /// <param name="console">Console used for stdout output and diagnostics.</param>
    public ScanOutputWriter(OutputFormat format, string? output, bool multipleExpected, CliConsole console)
    {
        _format = format;
        _console = console;
        _headers = multipleExpected && format is OutputFormat.Text;

        if (string.IsNullOrWhiteSpace(output))
        {
            return; // stdout
        }

        var full = Path.GetFullPath(output);
        // Decide from the DESTINATION, not from how many documents happen to be coming. `multipleExpected`
        // alone used to force directory mode, and it is set for any PDF input regardless of page count -- so
        // this command's own documented example, `scan report.pdf --format hocr -o report.hocr.html`, created
        // a *directory* named report.hocr.html holding report.p001.hocr.html. Any script doing
        // `scan in.pdf -o out.json && jq . out.json` broke, and because the directory is created in this
        // constructor (before OCR runs) it was left behind even when the run failed.
        //
        // An explicit filename is now honoured and Flush() concatenates in input order -- which it already
        // did, including building the single JSON array that directory mode never produced. Multi-document
        // runs still get a directory when the destination looks like one: it exists, it ends with a
        // separator, or it carries no extension (`-o results`).
        bool looksLikeDirectory =
            Directory.Exists(full)
            || output.EndsWith(Path.DirectorySeparatorChar) || output.EndsWith(Path.AltDirectorySeparatorChar)
            || (multipleExpected && !Path.HasExtension(full));

        if (looksLikeDirectory)
        {
            _directory = full;
            Directory.CreateDirectory(_directory);
        }
        else
        {
            _filePath = full;
            var parent = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        }
    }

    /// <summary>True when each result becomes its own file, so results can be written as they complete.</summary>
    public bool IsDirectoryMode => _directory is not null;

    /// <summary>The file extension used for one result document in the current format.</summary>
    private string Extension => _format switch
    {
        OutputFormat.Json => ".json",
        OutputFormat.Hocr => ".hocr.html",
        OutputFormat.Alto => ".alto.xml",
        OutputFormat.Tsv => ".tsv",
        _ => ".txt",
    };

    /// <summary>
    /// Renders and records one result. In directory mode it is written immediately (bounded memory for
    /// very large batches); otherwise it is buffered and emitted in input order by <see cref="Flush"/>.
    /// </summary>
    /// <param name="order">Sort key: the input's position, with the page number as a tiebreaker.</param>
    /// <param name="report">The result (or failure) to render.</param>
    public void Add(long order, CliScanReport report)
    {
        var rendered = Render(report);

        if (_directory is not null)
        {
            // A failed input has no document to write; the error already went to stderr.
            if (rendered.Length == 0) return;

            var path = ReserveFilePath(report);
            File.WriteAllText(path, rendered, Utf8NoBom);
            _console.Info($"wrote {path}");
            return;
        }

        lock (_gate)
        {
            _buffered.Add((order, rendered));
        }
    }

    /// <summary>Writes everything buffered for stdout or for a single output file, in input order.</summary>
    public void Flush()
    {
        if (_directory is not null) return;

        List<(long Order, string Rendered)> ordered;
        lock (_gate)
        {
            ordered = [.. _buffered.OrderBy(entry => entry.Order)];
        }

        var sb = new StringBuilder();
        if (_format is OutputFormat.Json)
        {
            // Always an array, even for one input, so a consumer never has to branch on shape.
            sb.Append("[\n");
            for (int i = 0; i < ordered.Count; i++)
            {
                sb.Append(Indent(ordered[i].Rendered));
                if (i < ordered.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("]\n");
        }
        else
        {
            foreach (var entry in ordered)
            {
                sb.Append(entry.Rendered);
                if (!entry.Rendered.EndsWith('\n')) sb.Append('\n');
            }
        }

        if (_filePath is not null)
        {
            File.WriteAllText(_filePath, sb.ToString(), Utf8NoBom);
            _console.Info($"wrote {_filePath}");
        }
        else
        {
            _console.Write(sb.ToString());
        }
    }

    private string Render(CliScanReport report)
    {
        if (_format is OutputFormat.Json)
        {
            return JsonSerializer.Serialize(report, CliJsonContext.Unescaped.CliScanReport);
        }

        if (report.Result is not { } result)
        {
            // Non-JSON formats have nowhere to put an error, so the document is empty. Reporting it is the
            // caller's job -- ScanCommand already writes the message and owns the failure counter -- and
            // doing it here too printed every failure to stderr twice.
            return string.Empty;
        }

        var name = Path.GetFileName(report.Source);
        var body = _format switch
        {
            OutputFormat.Hocr => result.ToHocr(result.SourceWidth, result.SourceHeight, name),
            OutputFormat.Alto => result.ToAlto(result.SourceWidth, result.SourceHeight, name),
            OutputFormat.Tsv => result.ToTsv(),
            _ => result.FullText,
        };

        if (!_headers) return body;

        var header = report.Page is { } page
            ? $"===== {report.Source} (page {page}) ====="
            : $"===== {report.Source} =====";
        return header + "\n" + body + "\n";
    }

    /// <summary>
    /// Picks a collision-free output file for a result: <c>&lt;input-name&gt;[.pNNN]&lt;ext&gt;</c>, with a
    /// numeric suffix if two inputs from different folders share a name.
    /// </summary>
    private string ReserveFilePath(CliScanReport report)
    {
        var stem = Path.GetFileNameWithoutExtension(report.Source);
        if (string.IsNullOrEmpty(stem)) stem = "page";
        if (report.Page is { } page) stem += $".p{page:D3}";

        lock (_gate)
        {
            var candidate = stem + Extension;
            for (int suffix = 2; !_usedFileNames.Add(candidate); suffix++)
            {
                candidate = $"{stem}-{suffix}{Extension}";
            }
            return Path.Combine(_directory!, candidate);
        }
    }

    /// <summary>Indents a serialized object by two spaces so the emitted JSON array stays readable.</summary>
    private static string Indent(string json)
    {
        var lines = json.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = "  " + lines[i].TrimEnd('\r');
        }
        return string.Join('\n', lines);
    }

    /// <summary>Parses the <c>--format</c> value.</summary>
    public static OutputFormat ParseFormat(ParsedArgs args, string optionName) =>
        args.Value(optionName)?.ToLowerInvariant() switch
        {
            null or "text" or "txt" or "plain" => OutputFormat.Text,
            "json" => OutputFormat.Json,
            "hocr" => OutputFormat.Hocr,
            "alto" or "xml" => OutputFormat.Alto,
            "tsv" => OutputFormat.Tsv,
            var other => throw args.Fail($"Unknown --format '{other}'. Expected: text, json, hocr, alto, tsv."),
        };
}
