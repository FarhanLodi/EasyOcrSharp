using EasyOcrSharp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Turns a requested <see cref="OcrExecutionProvider"/> (including
/// <see cref="OcrExecutionProvider.Auto"/>) into a concrete provider and the matching ONNX Runtime
/// <see cref="SessionOptions"/>.
/// <para>
/// Only one ONNX Runtime native package can be referenced by an app at a time (they all ship the same
/// <c>onnxruntime.dll</c>, compiled with different providers), so the set of usable accelerators is
/// fixed at build time by whichever package the consumer installed. <see cref="Resolve"/> asks the
/// loaded runtime what it actually contains via <see cref="OrtEnv.GetAvailableProviders"/> and, for
/// <see cref="OcrExecutionProvider.Auto"/>, picks the best one for the current OS — never guessing a
/// provider whose native code is not present.
/// </para>
/// </summary>
internal static class ExecutionProviderResolver
{
    // ONNX Runtime's provider names as returned by OrtEnv.GetAvailableProviders().
    private const string CudaName = "CUDAExecutionProvider";
    private const string DmlName = "DmlExecutionProvider";
    private const string CoreMlName = "CoreMLExecutionProvider";

    /// <summary>
    /// Resolves the provider EasyOcrSharp will attempt to use. An explicit (non-Auto) request is
    /// returned unchanged — if its runtime is missing, session building still degrades to CPU with a
    /// warning. <see cref="OcrExecutionProvider.Auto"/> is resolved to the best accelerator the loaded
    /// runtime reports for this OS, or <see cref="OcrExecutionProvider.Cpu"/> when none is available.
    /// </summary>
    public static OcrExecutionProvider Resolve(OcrExecutionProvider requested, ILogger? logger)
    {
        if (requested != OcrExecutionProvider.Auto) return requested;

        IReadOnlyCollection<string> available;
        try
        {
            available = OrtEnv.Instance().GetAvailableProviders();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Auto-detect: could not query ONNX Runtime providers; using CPU.");
            return OcrExecutionProvider.Cpu;
        }

        // Priority per OS. The provider packages are mutually exclusive, so in practice at most one
        // accelerator is ever present; the order only decides ties that cannot actually occur.
        foreach (var candidate in CandidatesFor())
        {
            if (available.Contains(NativeNameOf(candidate)))
            {
                logger?.LogInformation(
                    "Auto-detect: ONNX Runtime providers [{Available}] -> selected {Provider}.",
                    string.Join(", ", available), candidate);
                return candidate;
            }
        }

        logger?.LogInformation(
            "Auto-detect: no accelerated provider available (runtime has [{Available}]); using CPU. " +
            "Install EasyOcrSharp.Gpu (CUDA / NVIDIA) to enable GPU acceleration.",
            string.Join(", ", available));
        return OcrExecutionProvider.Cpu;
    }

    private static OcrExecutionProvider[] CandidatesFor()
    {
        if (OperatingSystem.IsWindows()) return new[] { OcrExecutionProvider.DirectMl, OcrExecutionProvider.Cuda };
        if (OperatingSystem.IsMacOS()) return new[] { OcrExecutionProvider.CoreMl };
        if (OperatingSystem.IsLinux()) return new[] { OcrExecutionProvider.Cuda };
        return Array.Empty<OcrExecutionProvider>();
    }

    private static string NativeNameOf(OcrExecutionProvider provider) => provider switch
    {
        OcrExecutionProvider.Cuda => CudaName,
        OcrExecutionProvider.DirectMl => DmlName,
        OcrExecutionProvider.CoreMl => CoreMlName,
        _ => "CPUExecutionProvider",
    };

    /// <summary>
    /// Builds <see cref="SessionOptions"/> for a concrete (already-resolved) provider. A non-CPU
    /// provider that fails to attach logs a warning and leaves the options on CPU rather than throwing.
    /// </summary>
    public static SessionOptions BuildSessionOptions(OcrExecutionProvider provider, EngineOptions options, ILogger? logger)
    {
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        if (options.IntraOpNumThreads is { } intra and > 0) opts.IntraOpNumThreads = intra;
        if (options.InterOpNumThreads is { } inter and > 0) opts.InterOpNumThreads = inter;

        switch (provider)
        {
            case OcrExecutionProvider.Cuda:
                TryAppendProvider(logger, "CUDA", "EasyOcrSharp.Gpu", () => opts.AppendExecutionProvider_CUDA());
                break;
            case OcrExecutionProvider.DirectMl:
                // DirectML needs sequential execution with memory pattern disabled.
                opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                opts.EnableMemoryPattern = false;
                TryAppendProvider(logger, "DirectML", "Microsoft.ML.OnnxRuntime.DirectML", () => opts.AppendExecutionProvider_DML(0));
                break;
            case OcrExecutionProvider.CoreMl:
                TryAppendProvider(logger, "CoreML", "a CoreML-enabled ONNX Runtime build", () => opts.AppendExecutionProvider("CoreML"));
                break;
            case OcrExecutionProvider.Cpu:
            case OcrExecutionProvider.Auto:
            default:
                break;
        }

        return opts;
    }

    private static void TryAppendProvider(ILogger? logger, string name, string package, Action append)
    {
        try
        {
            append();
            logger?.LogInformation("ONNX Runtime: {Provider} execution provider enabled.", name);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "{Provider} execution provider unavailable. Falling back to CPU. Install {Package} for support.", name, package);
        }
    }
}
