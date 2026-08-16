using EasyOcrSharp.Cli.CommandLine;
using EasyOcrSharp.Cli.Internal;
using EasyOcrSharp.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EasyOcrSharp.Cli.Commands;

/// <summary>
/// <c>models</c> — manage the ONNX model cache. Exists so an air-gapped or immutable deployment can
/// seed the cache during image build (<c>models pull</c>) and then run the OCR commands with
/// <c>--offline</c>, never touching the network at request time.
/// </summary>
internal static class ModelsCommand
{
    /// <summary>Declaration for <c>models pull</c>.</summary>
    public static readonly CliCommand PullSpec = new(
        "models pull",
        "Download the detector and the recognizer packs for the given languages into the model cache, "
        + "then verify they load. Run this at image-build time so production starts warm and offline.",
        "models pull <languages> [options]",
        [CommonOptions.Cache, CommonOptions.Quiet])
    {
        Examples =
        [
            "easyocrsharp models pull en",
            "easyocrsharp models pull en,fr,de --cache /var/cache/easyocr",
        ],
    };

    /// <summary>Declaration for <c>models list</c>.</summary>
    public static readonly CliCommand ListSpec = new(
        "models list",
        "List the model files currently in the cache. Pass languages to also report which of their "
        + "models are still missing.",
        "models list [languages] [options]",
        [CommonOptions.Cache, CommonOptions.Quiet])
    {
        Remarks =
            "Exits 1 when a requested language still has missing models, so a readiness gate can simply "
            + "check the exit code.",
        Examples =
        [
            "easyocrsharp models list",
            "easyocrsharp models list en,ja",
        ],
    };

    /// <summary>Declaration for <c>models path</c>.</summary>
    public static readonly CliCommand PathSpec = new(
        "models path",
        "Print the model cache directory (and nothing else), so it can be captured by a script: "
        + "MODELS=$(easyocrsharp models path).",
        "models path [options]",
        [CommonOptions.Cache]);

    /// <summary>Routes the <c>models</c> sub-command and runs it.</summary>
    public static Task<int> RunAsync(IReadOnlyList<string> argv, CancellationToken cancellationToken)
    {
        var sub = argv.Count > 0 ? argv[0] : null;
        string[] rest = argv.Count > 0 ? [.. argv.Skip(1)] : [];

        return sub switch
        {
            "pull" => PullAsync(rest, cancellationToken),
            "list" => ListAsync(rest, cancellationToken),
            "path" => Task.FromResult(PrintPath(rest)),
            null or "--help" or "-h" or "help" => Task.FromResult(PrintHelp()),
            _ => throw new CliUsageException($"Unknown 'models' sub-command '{sub}'. Expected: pull, list, path."),
        };
    }

    private static int PrintHelp()
    {
        Console.Out.Write(
            $"""
            Usage:
              {CliMetadata.ExecutableName} models <pull|list|path> [options]

            Manage the ONNX model cache.

            Sub-commands:
              pull <languages>  Download the models for these languages into the cache.
              list [languages]  Show what is cached (and what is missing for those languages).
              path              Print the cache directory.

            Run '{CliMetadata.ExecutableName} models <sub-command> --help' for the options of each.

            """);
        return ExitCodes.Success;
    }

    private static async Task<int> PullAsync(IReadOnlyList<string> argv, CancellationToken cancellationToken)
    {
        var args = ArgParser.Parse(PullSpec, argv);
        if (args.Flag(CliCommand.HelpOption.Name))
        {
            Console.Out.Write(PullSpec.HelpText());
            return ExitCodes.Success;
        }

        var console = new CliConsole(args.Flag(CommonOptions.Quiet.Name));
        var languages = ParseLanguages(args);
        if (languages.Length == 0)
        {
            throw args.Fail("Name at least one language, e.g. 'models pull en,fr'.");
        }

        var cacheRoot = ModelCacheLocator.Resolve(args.Value(CommonOptions.Cache.Name));
        console.Info($"Pulling models for [{string.Join(", ", languages)}] into {cacheRoot}");

        var options = new EasyOcrServiceOptions
        {
            ModelCachePath = args.Value(CommonOptions.Cache.Name),
            // Pulling is pure I/O: never spin up an accelerator just to download and validate files.
            ExecutionProvider = OcrExecutionProvider.Cpu,
        };
        if (console.ProgressEnabled)
        {
            options.Download.Progress = new DownloadProgressReporter(console);
        }

        await using var service = new EasyOcrService(options);
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
            throw new CliException($"Could not pull models for [{string.Join(", ", languages)}]: {ex.Message}", ex);
        }
        finally
        {
            console.ClearProgress();
        }

        var files = ModelCacheLocator.CachedFiles(cacheRoot);
        long bytes = files.Sum(f => f.Length);
        console.Info($"Cache ready: {files.Count} file(s), {DownloadProgressReporter.Format(bytes)}.");
        return ExitCodes.Success;
    }

    private static async Task<int> ListAsync(IReadOnlyList<string> argv, CancellationToken cancellationToken)
    {
        var args = ArgParser.Parse(ListSpec, argv);
        if (args.Flag(CliCommand.HelpOption.Name))
        {
            Console.Out.Write(ListSpec.HelpText());
            return ExitCodes.Success;
        }

        var console = new CliConsole(args.Flag(CommonOptions.Quiet.Name));
        var cachePath = args.Value(CommonOptions.Cache.Name);
        var cacheRoot = ModelCacheLocator.Resolve(cachePath);
        var files = ModelCacheLocator.CachedFiles(cacheRoot);

        console.WriteLine($"Cache: {cacheRoot}");
        if (files.Count == 0)
        {
            console.WriteLine("  (empty — run 'easyocrsharp models pull <languages>')");
        }
        else
        {
            int width = files.Max(f => f.Name.Length);
            foreach (var file in files)
            {
                console.WriteLine($"  {file.Name.PadRight(width)}  {DownloadProgressReporter.Format(file.Length),10}");
            }
            console.WriteLine($"  {files.Count} file(s), {DownloadProgressReporter.Format(files.Sum(f => f.Length))}");
        }

        var languages = ParseLanguages(args);
        if (languages.Length == 0) return ExitCodes.Success;

        // The health check is the library's own answer to "are these languages ready to serve?", so the
        // CLI reports exactly what a readiness probe in production would.
        var check = new EasyOcrHealthCheck(new EasyOcrServiceOptions { ModelCachePath = cachePath }, languages);
        var health = await check.CheckHealthAsync(new HealthCheckContext(), cancellationToken).ConfigureAwait(false);

        console.WriteLine();
        console.WriteLine($"Languages [{string.Join(", ", languages)}]: {health.Status}");
        if (health.Data.TryGetValue("missing", out var missing) && missing is IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                console.WriteLine($"  missing: {name}");
            }
            return ExitCodes.Failure;
        }

        return ExitCodes.Success;
    }

    private static int PrintPath(IReadOnlyList<string> argv)
    {
        var args = ArgParser.Parse(PathSpec, argv);
        if (args.Flag(CliCommand.HelpOption.Name))
        {
            Console.Out.Write(PathSpec.HelpText());
            return ExitCodes.Success;
        }

        // Bare path on stdout, nothing else — this command exists to be captured by $(...).
        Console.Out.WriteLine(ModelCacheLocator.Resolve(args.Value(CommonOptions.Cache.Name)));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Languages for a models sub-command, taken from the positionals: <c>models pull en,fr</c> and
    /// <c>models pull en fr</c> are equivalent.
    /// </summary>
    private static string[] ParseLanguages(ParsedArgs args)
    {
        var languages = new List<string>();
        foreach (var positional in args.Positionals)
        {
            languages.AddRange(positional.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return [.. languages.Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
