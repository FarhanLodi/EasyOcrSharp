using EasyOcrSharp.Cli.CommandLine;
using EasyOcrSharp.Cli.Internal;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Services;

namespace EasyOcrSharp.Cli.Commands;

/// <summary>
/// <c>scan</c> — the workhorse. Recognizes text in images, PDFs, folders and globs and writes it as
/// text, JSON, hOCR, ALTO or TSV. Images are processed through the library's bounded-concurrency batch
/// pipeline; PDFs are rasterized page by page so a 500-page scan never has to fit in memory at once.
/// </summary>
internal static class ScanCommand
{
    private static readonly CliOption Format = new(
        "format", "-f", true, "Output format: text (default), json, hocr, alto, tsv.", "fmt");

    private static readonly CliOption Output = new(
        "output", "-o", true,
        "Write results here: a file for a single document, otherwise a directory. Default: stdout.", "path");

    private static readonly CliOption Paragraph = new(
        "paragraph", null, false, "Merge lines into paragraph blocks.");

    private static readonly CliOption Line = new(
        "line", null, false, "Merge boxes into lines (the default).");

    private static readonly CliOption Word = new(
        "word", null, false, "Do not merge: one result per detected box.");

    private static readonly CliOption Recursive = new(
        "recursive", "-r", false, "Descend into sub-directories when an input is a folder or a glob.");

    private static readonly CliOption Jobs = new(
        "jobs", "-j", true, "Images recognized concurrently (default: half the CPU count).", "n");

    private static readonly ScanFlags GroupingFlags = new(Paragraph, Line, Word);

    /// <summary>The command's declaration — options, usage and help.</summary>
    public static readonly CliCommand Spec = new(
        "scan",
        "Recognize text in images and PDFs. Inputs may be files, folders or globs; results go to stdout "
        + "unless -o is given. Text goes to stdout and diagnostics to stderr, so the tool pipes cleanly.",
        "scan <input...> [options]",
        [
            CommonOptions.Lang,
            Format,
            Output,
            Paragraph,
            Line,
            Word,
            CommonOptions.Detail,
            CommonOptions.Allowlist,
            CommonOptions.Blocklist,
            CommonOptions.MinConfidence,
            CommonOptions.Decoder,
            CommonOptions.BeamWidth,
            CommonOptions.Preprocess,
            CommonOptions.Dpi,
            Recursive,
            Jobs,
            CommonOptions.Gpu,
            CommonOptions.Cpu,
            CommonOptions.Cache,
            CommonOptions.Offline,
            CommonOptions.Quiet,
        ])
    {
        Remarks =
            "Exit codes: 0 all inputs succeeded, 1 at least one failed, 2 the command line was wrong, "
            + "130 interrupted with Ctrl-C.",
        Examples =
        [
            "easyocrsharp scan receipt.png",
            "easyocrsharp scan scans/ -r -l en,de --format json -o out/",
            "easyocrsharp scan 'invoices/*.tif' --paragraph --min-confidence 0.4 > invoices.txt",
            "easyocrsharp scan report.pdf --dpi 300 --format hocr -o report.hocr.html",
            "easyocrsharp scan plate.jpg --allowlist ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
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
        if (args.Positionals.Count == 0)
        {
            throw args.Fail("No input given. Pass one or more images, PDFs, folders or globs.");
        }

        var inputs = InputResolver.Resolve(args.Positionals, args.Flag(Recursive.Name));
        var languages = OptionBinder.Languages(args);
        var format = ScanOutputWriter.ParseFormat(args, Format.Name);

        int jobs = args.Int(Jobs.Name) ?? 0;
        if (jobs < 0) throw args.Fail("--jobs must be 0 (auto) or positive.");

        var recognition = OptionBinder.BuildRecognitionOptions(args, GroupingFlags);
        if (jobs > 1)
        {
            // Whole-image concurrency and per-box concurrency multiply; split the CPU between them so a
            // large --jobs doesn't oversubscribe the machine into thrashing.
            recognition = recognition with
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / jobs),
            };
        }

        var images = inputs.Where(static path => !InputResolver.IsPdf(path)).ToArray();
        var pdfs = inputs.Where(InputResolver.IsPdf).ToArray();
        bool multipleExpected = inputs.Count > 1 || pdfs.Length > 0;

        var writer = new ScanOutputWriter(format, args.Value(Output.Name), multipleExpected, console);
        var order = BuildOrderIndex(inputs);

        var serviceOptions = OptionBinder.BuildServiceOptions(args, console);
        await using var service = new EasyOcrService(serviceOptions);

        console.Info($"easyocrsharp: {inputs.Count} input(s), languages [{string.Join(", ", languages)}]"
            + (service.UseGpu ? ", GPU" : ", CPU"));

        int completed = 0;
        int failures = 0;

        // Loading the models once up front keeps the download progress out of the per-file progress line
        // and makes an offline/missing-model failure surface immediately instead of per input.
        try
        {
            await service.WarmUp(languages, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CliException($"Could not load the models for [{string.Join(", ", languages)}]: {ex.Message}", ex);
        }
        finally
        {
            console.ClearProgress();
        }

        if (images.Length > 0)
        {
            await foreach (var batch in service
                .ExtractTextFromImagesAsync(images, languages, recognition, jobs, cancellationToken)
                .ConfigureAwait(false))
            {
                long key = (long)order.GetValueOrDefault(batch.Source, int.MaxValue) * PageStride;
                if (batch.Succeeded && batch.Result is { } result)
                {
                    writer.Add(key, new CliScanReport { Source = batch.Source, Result = result });
                }
                else
                {
                    failures++;
                    console.Error($"{batch.Source}: {Describe(batch.Error)}");
                    writer.Add(key, new CliScanReport { Source = batch.Source, Error = Describe(batch.Error) });
                }

                Report(console, ++completed, inputs.Count, batch.Source);
            }
        }

        foreach (var pdf in pdfs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long key = (long)order.GetValueOrDefault(pdf, int.MaxValue) * PageStride;
            try
            {
                var pdfOptions = OptionBinder.BuildPdfOptions(args, new SyncProgress<PdfPageProgress>(
                    progress => console.Progress($"[{completed + 1}/{inputs.Count}] {Path.GetFileName(pdf)}  page {progress.PageNumber}/{progress.PageCount}")));

                var document = await service
                    .ExtractTextFromPdfAsync(pdf, languages, recognition, pdfOptions, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var page in document.Pages)
                {
                    writer.Add(key + page.PageNumber, new CliScanReport
                    {
                        Source = pdf,
                        Page = page.PageNumber,
                        Result = page.Ocr,
                    });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures++;
                console.Error($"{pdf}: {Describe(ex)}");
                writer.Add(key, new CliScanReport { Source = pdf, Error = Describe(ex) });
            }

            Report(console, ++completed, inputs.Count, pdf);
        }

        console.ClearProgress();
        writer.Flush();

        if (failures > 0)
        {
            console.Info($"{inputs.Count - failures}/{inputs.Count} succeeded, {failures} failed.");
            return ExitCodes.Failure;
        }

        console.Info($"{inputs.Count}/{inputs.Count} succeeded.");
        return ExitCodes.Success;
    }

    /// <summary>
    /// Room reserved between two inputs' sort keys for the pages of a PDF, so page 2 of input 1 still
    /// sorts before input 2. 100,000 pages is far beyond what the library will even rasterize.
    /// </summary>
    private const long PageStride = 100_000;

    private static Dictionary<string, int> BuildOrderIndex(IReadOnlyList<string> inputs)
    {
        var index = new Dictionary<string, int>(
            inputs.Count,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        for (int i = 0; i < inputs.Count; i++)
        {
            index[inputs[i]] = i;
        }
        return index;
    }

    private static void Report(CliConsole console, int completed, int total, string source)
        => console.Progress($"[{completed}/{total}] {Path.GetFileName(source)}");

    private static string Describe(Exception? error) => error switch
    {
        null => "unknown error",
        FileNotFoundException => "file not found",
        _ => error.Message,
    };
}
