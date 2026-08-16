using EasyOcrSharp.Cli.CommandLine;
using EasyOcrSharp.Cli.Internal;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Services;

namespace EasyOcrSharp.Cli.Commands;

/// <summary>
/// <c>pdf</c> — turns a scanned PDF into a searchable one: the original page images with an invisible,
/// selectable text layer underneath. This is the archival workflow, so it is its own command rather
/// than a <c>scan</c> format: the output is a PDF, not a text document.
/// </summary>
internal static class PdfCommand
{
    private static readonly CliOption Output = new(
        "output", "-o", true, "Path of the searchable PDF to write (required).", "out.pdf");

    private static readonly CliOption JpegQuality = new(
        "jpeg-quality", null, true, "JPEG quality of the embedded page images, 1-100 (default: 75).", "n");

    private static readonly CliOption Paragraph = new(
        "paragraph", null, false, "Merge lines into paragraph blocks before building the text layer.");

    private static readonly CliOption Line = new(
        "line", null, false, "Merge boxes into lines (the default).");

    private static readonly CliOption Word = new(
        "word", null, false, "Do not merge: one text-layer entry per detected box.");

    private static readonly ScanFlags GroupingFlags = new(Paragraph, Line, Word);

    /// <summary>The command's declaration — options, usage and help.</summary>
    public static readonly CliCommand Spec = new(
        "pdf",
        "OCR a scanned PDF and write a searchable PDF: the same pages, with an invisible text layer "
        + "that readers can select, copy and full-text index.",
        "pdf <input.pdf> -o <output.pdf> [options]",
        [
            Output,
            CommonOptions.Lang,
            CommonOptions.Dpi,
            JpegQuality,
            Paragraph,
            Line,
            Word,
            CommonOptions.Preprocess,
            CommonOptions.Allowlist,
            CommonOptions.Blocklist,
            CommonOptions.MinConfidence,
            CommonOptions.Decoder,
            CommonOptions.BeamWidth,
            CommonOptions.Gpu,
            CommonOptions.Cpu,
            CommonOptions.Cache,
            CommonOptions.Offline,
            CommonOptions.Quiet,
        ])
    {
        Remarks =
            "The text layer is written with the standard Latin-1 encoding, so non-Latin scripts may not "
            + "be selectable in every reader even though recognition itself is unaffected.",
        Examples =
        [
            "easyocrsharp pdf scan.pdf -o searchable.pdf",
            "easyocrsharp pdf contract.pdf -o contract.ocr.pdf -l en,fr --dpi 300",
        ],
    };

    /// <summary>Parses and runs the command, returning the process exit code.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> argv, CancellationToken cancellationToken)
    {
        var args = ArgParser.Parse(Spec, argv);
        if (args.Flag(CliCommand.HelpOption.Name))
        {
            Console.Out.Write(Spec.HelpText());
            return ExitCodes.Success;
        }

        var console = new CliConsole(args.Flag(CommonOptions.Quiet.Name));

        if (args.Positionals.Count == 0) throw args.Fail("No input PDF given.");
        if (args.Positionals.Count > 1)
            throw args.Fail("pdf takes exactly one input PDF. Use 'scan' for many inputs.");

        var input = Path.GetFullPath(args.Positionals[0]);
        if (!File.Exists(input)) throw args.Fail($"'{input}' does not exist.");
        if (!InputResolver.IsPdf(input)) throw args.Fail($"'{input}' is not a PDF.");

        if (args.Value(Output.Name) is not { } outputValue || string.IsNullOrWhiteSpace(outputValue))
            throw args.Fail("-o/--output is required: name the searchable PDF to write.");

        var output = Path.GetFullPath(outputValue);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw args.Fail("The output PDF must not be the input PDF.");

        var parent = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        var languages = OptionBinder.Languages(args);
        var recognition = OptionBinder.BuildRecognitionOptions(args, GroupingFlags);
        var pdfOptions = OptionBinder.BuildPdfOptions(args, new SyncProgress<PdfPageProgress>(
            progress => console.Progress($"page {progress.PageNumber}/{progress.PageCount} ({progress.Fraction * 100:F0}%)")));

        if (args.Int(JpegQuality.Name) is { } quality)
        {
            if (quality is < 1 or > 100) throw args.Fail("--jpeg-quality must be between 1 and 100.");
            pdfOptions.JpegQuality = quality;
        }

        await using var service = new EasyOcrService(OptionBinder.BuildServiceOptions(args, console));
        console.Info($"easyocrsharp: {input} -> {output}, languages [{string.Join(", ", languages)}]"
            + (service.UseGpu ? ", GPU" : ", CPU"));

        try
        {
            var result = await service
                .CreateSearchablePdfAsync(input, output, languages, recognition, pdfOptions, cancellationToken)
                .ConfigureAwait(false);

            console.ClearProgress();
            int lines = result.Pages.Sum(page => page.Ocr.Lines.Count);
            console.Info($"Wrote {output}: {result.Pages.Count} page(s), {lines} line(s) of text.");

            // The path on stdout keeps the command composable: OUT=$(easyocrsharp pdf in.pdf -o out.pdf).
            console.WriteLine(output);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CliException($"Could not create the searchable PDF: {ex.Message}", ex);
        }
        finally
        {
            console.ClearProgress();
        }
    }
}
