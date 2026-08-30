using System.Runtime.InteropServices;
using System.Text.Json;
using EasyOcrSharp.Cli.CommandLine;
using EasyOcrSharp.Cli.Internal;
using EasyOcrSharp.Services;
using Microsoft.ML.OnnxRuntime;

namespace EasyOcrSharp.Cli.Commands;

/// <summary>
/// <c>info</c> — what this installation actually is: versions, the execution provider that will really
/// be used on this host (not the one that was requested), whether a GPU is present but idle, and where
/// models live. The first thing to paste into a bug report.
/// </summary>
internal static class InfoCommand
{
    private static readonly CliOption Json = new(
        "json", null, false, "Emit the report as JSON for scripts and provisioning checks.");

    /// <summary>The command's declaration — options, usage and help.</summary>
    public static readonly CliCommand Spec = new(
        "info",
        "Report tool and library versions, the resolved ONNX Runtime execution provider, GPU status "
        + "and the model cache location.",
        "info [options]",
        [Json, CommonOptions.Gpu, CommonOptions.Cpu, CommonOptions.Cache])
    {
        Examples =
        [
            "easyocrsharp info",
            "easyocrsharp info --json",
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

        cancellationToken.ThrowIfCancellationRequested();
        var console = new CliConsole(quiet: true);
        var report = await BuildReportAsync(args, console).ConfigureAwait(false);

        if (args.Flag(Json.Name))
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(report, CliJsonContext.Unescaped.CliInfoReport));
            return ExitCodes.Success;
        }

        Print("EasyOcrSharp CLI", report.CliVersion);
        Print("EasyOcrSharp lib", report.LibraryVersion);
        Print(".NET runtime", report.Runtime);
        Print("Operating system", $"{report.OsDescription} ({report.Architecture})");
        Print("Execution provider", report.ExecutionProvider);
        Print("ORT providers", string.Join(", ", report.AvailableProviders));
        Print("Model cache", $"{report.ModelCachePath} ({report.CachedFileCount} file(s), {DownloadProgressReporter.Format(report.CachedBytes)})");
        if (report.GpuHint is { } hint)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine(hint);
        }

        return ExitCodes.Success;
    }

    private static async Task<CliInfoReport> BuildReportAsync(ParsedArgs args, CliConsole console)
    {
        var available = AvailableProviders();
        var cacheRoot = ModelCacheLocator.Resolve(args.Value(CommonOptions.Cache.Name));
        var cached = ModelCacheLocator.CachedFiles(cacheRoot);

        // Constructing the service resolves the provider without loading (or downloading) a single
        // model, so `info` is cheap and works on a cold, offline machine.
        string provider;
        string? gpuHint;
        await using (var service = new EasyOcrService(OptionBinder.BuildServiceOptions(args, console)))
        {
            provider = service.UseGpu
                ? available.FirstOrDefault(static p => !p.StartsWith("CPU", StringComparison.OrdinalIgnoreCase)) ?? "GPU"
                : "CPUExecutionProvider";
            gpuHint = service.GpuAccelerationHint;
        }

        return new CliInfoReport
        {
            CliVersion = CliMetadata.CliVersion,
            LibraryVersion = CliMetadata.LibraryVersion,
            Runtime = RuntimeInformation.FrameworkDescription,
            OsDescription = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ExecutionProvider = provider,
            AvailableProviders = available,
            GpuHint = gpuHint,
            ModelCachePath = cacheRoot,
            CachedFileCount = cached.Count,
            CachedBytes = cached.Sum(file => file.Length),
        };
    }

    /// <summary>
    /// The execution providers compiled into the loaded ONNX Runtime. Only one native runtime package
    /// can be referenced at a time, so this is the definitive answer to "can this build use a GPU?".
    /// </summary>
    private static string[] AvailableProviders()
    {
        try
        {
            return [.. OrtEnv.Instance().GetAvailableProviders()];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A broken native load shouldn't stop `info` from reporting everything else it knows.
            return ["(unavailable)"];
        }
    }

    private static void Print(string label, string value)
        => Console.Out.WriteLine($"{label.PadRight(18)}  {value}");
}
