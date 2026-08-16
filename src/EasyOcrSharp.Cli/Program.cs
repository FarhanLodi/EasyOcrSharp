using System.Text;
using EasyOcrSharp.Cli.CommandLine;
using EasyOcrSharp.Cli.Commands;

namespace EasyOcrSharp.Cli;

/// <summary>
/// Entry point and top-level dispatch. Everything here is about being a well-behaved Unix citizen:
/// results on stdout, diagnostics on stderr, a meaningful exit code, and a Ctrl-C that unwinds
/// cleanly instead of leaving a half-written PDF behind.
/// </summary>
internal static class Program
{
    /// <summary>Set to any non-empty value to print full stack traces on failure.</summary>
    private const string DebugEnvironmentVariable = "EASYOCRSHARP_DEBUG";

    /// <summary>Runs the tool and returns the process exit code.</summary>
    /// <param name="args">Raw command-line arguments.</param>
    public static async Task<int> Main(string[] args)
    {
        UseUtf8Output();

        using var cts = new CancellationTokenSource();
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            // First Ctrl-C cancels cooperatively; a second one takes the default path and kills us, so a
            // wedged native call can always be escaped.
            if (cts.IsCancellationRequested) return;
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine();
            Console.Error.WriteLine("Interrupting… (press Ctrl-C again to force quit)");
        }

        Console.CancelKeyPress += OnCancel;
        try
        {
            return await DispatchAsync(args, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("Cancelled.");
            return ExitCodes.Interrupted;
        }
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            Console.Error.WriteLine();
            Console.Error.Write(ex.Command?.HelpText() ?? RootHelp());
            return ExitCodes.Usage;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DebugEnvironmentVariable)))
            {
                Console.Error.WriteLine(ex.ToString());
            }
            return ExitCodes.Failure;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }
    }

    private static Task<int> DispatchAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            // No arguments is not success: print the map to stderr and say so with the exit code.
            Console.Error.Write(RootHelp());
            return Task.FromResult(ExitCodes.Usage);
        }

        var command = args[0];
        string[] rest = [.. args.Skip(1)];

        return command switch
        {
            "scan" => ScanCommand.RunAsync(rest, cancellationToken),
            "pdf" => PdfCommand.RunAsync(rest, cancellationToken),
            "models" => ModelsCommand.RunAsync(rest, cancellationToken),
            "info" => InfoCommand.RunAsync(rest, cancellationToken),
            "--help" or "-h" or "help" => Help(rest),
            "--version" or "-v" or "version" => Version(),
            _ => throw new CliUsageException($"Unknown command '{command}'. Expected: scan, pdf, models, info."),
        };
    }

    /// <summary><c>help</c> with a command name forwards to that command's own help page.</summary>
    private static Task<int> Help(IReadOnlyList<string> rest)
    {
        if (rest.Count > 0)
        {
            return DispatchAsync([rest[0], "--help"], CancellationToken.None);
        }

        Console.Out.Write(RootHelp());
        return Task.FromResult(ExitCodes.Success);
    }

    private static Task<int> Version()
    {
        Console.Out.WriteLine(CliMetadata.CliVersion);
        return Task.FromResult(ExitCodes.Success);
    }

    private static string RootHelp() =>
        $"""
        {CliMetadata.ExecutableName} {CliMetadata.CliVersion} — OCR powered by EasyOcrSharp
        (EasyOCR's neural models on ONNX Runtime; library {CliMetadata.LibraryVersion}).

        Usage:
          {CliMetadata.ExecutableName} <command> [options]

        Commands:
          scan    Recognize text in images, PDFs, folders and globs.
          pdf     Write a searchable PDF from a scanned one.
          models  Pull, list and locate the ONNX models (air-gapped deployment).
          info    Versions, execution provider, GPU status and cache location.

        Options:
          -h, --help     Show this help. Use '<command> --help' for a command's own options.
          -v, --version  Print the version and exit.

        Examples:
          {CliMetadata.ExecutableName} scan receipt.png
          {CliMetadata.ExecutableName} scan scans/ -r -l en,de --format json -o out/
          {CliMetadata.ExecutableName} pdf scan.pdf -o searchable.pdf
          {CliMetadata.ExecutableName} models pull en,fr

        """;

    /// <summary>
    /// Makes the console speak UTF-8 so recognized Cyrillic, CJK or Arabic text is not mangled on the
    /// way out. Best-effort: a redirected or exotic console may refuse, which is harmless.
    /// </summary>
    private static void UseUtf8Output()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or ArgumentException)
        {
            // Keep the platform default; text may transliterate but the tool still works.
        }
    }
}
