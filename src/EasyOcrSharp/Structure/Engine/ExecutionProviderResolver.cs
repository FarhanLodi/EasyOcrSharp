using System.Text.RegularExpressions;
using EasyOcrSharp.Structure.Engine.Services;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// Outcome of building <see cref="SessionOptions"/> for an already-resolved provider.
/// <see cref="ActiveProvider"/> is the provider the options actually carry: the requested one on
/// success, or <see cref="OcrExecutionProvider.Cpu"/> when an accelerator failed to attach — in which
/// case <see cref="ProviderFailureHint"/> holds a human-readable explanation of the failure and how to
/// fix it (wrong CUDA major, package pins, DirectML alternative). Null hint means nothing went wrong.
/// </summary>
internal readonly record struct SessionBuildResult(
    SessionOptions Options,
    OcrExecutionProvider ActiveProvider,
    string? ProviderFailureHint);

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
internal static partial class ExecutionProviderResolver
{
    // ONNX Runtime's provider names as returned by OrtEnv.GetAvailableProviders().
    private const string CudaName = "CUDAExecutionProvider";
    private const string DmlName = "DmlExecutionProvider";
    private const string CoreMlName = "CoreMLExecutionProvider";

    /// <summary>
    /// Resolves the provider EasyOcrSharp.Structure.Engine will attempt to use. An explicit (non-Auto) request is
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
    /// Builds <see cref="SessionOptions"/> for a concrete (already-resolved) provider and reports what
    /// actually got configured. A non-CPU provider that fails to attach does not throw: the returned
    /// options run on CPU, <see cref="SessionBuildResult.ActiveProvider"/> is
    /// <see cref="OcrExecutionProvider.Cpu"/>, and <see cref="SessionBuildResult.ProviderFailureHint"/>
    /// explains the failure (including the CUDA-major mismatch diagnosis from
    /// <see cref="CudaToolkitHint"/> when applicable).
    /// <para>
    /// When an accelerator fails to attach and <paramref name="logger"/> is null, the hint is also
    /// written once per process to <see cref="Console.Error"/> so default-constructed services are not
    /// silently degraded. Configuring any <see cref="ILogger"/> suppresses the stderr line — the hint
    /// then goes to the logger as a warning instead.
    /// </para>
    /// </summary>
    /// <param name="provider">The concrete, already-resolved execution provider to configure.</param>
    /// <param name="options">Engine options supplying thread counts and the accelerator device index.</param>
    /// <param name="logger">Optional logger for provider-attach diagnostics.</param>
    public static SessionBuildResult BuildSessionOptionsWithStatus(OcrExecutionProvider provider, StructureEngineOptions options, ILogger? logger)
    {
        var opts = CreateBaseOptions(options);

        string? failureHint = null;
        switch (provider)
        {
            case OcrExecutionProvider.Cuda:
                failureHint = TryAppendProvider(logger, provider, "CUDA", "EasyOcrSharp.Gpu", () => AppendCuda(opts, options));
                break;
            case OcrExecutionProvider.DirectMl:
                // DirectML needs sequential execution with memory pattern disabled.
                opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                opts.EnableMemoryPattern = false;
                failureHint = TryAppendProvider(logger, provider, "DirectML", "Microsoft.ML.OnnxRuntime.DirectML", () => opts.AppendExecutionProvider_DML(options.DeviceId));
                break;
            case OcrExecutionProvider.CoreMl:
                failureHint = TryAppendProvider(logger, provider, "CoreML", "a CoreML-enabled ONNX Runtime build", () => opts.AppendExecutionProvider("CoreML"));
                break;
            case OcrExecutionProvider.Cpu:
            case OcrExecutionProvider.Auto:
            default:
                return new SessionBuildResult(opts, OcrExecutionProvider.Cpu, null);
        }

        return failureHint is null
            ? new SessionBuildResult(opts, provider, null)
            : new SessionBuildResult(opts, OcrExecutionProvider.Cpu, failureHint);
    }

    /// <summary>
    /// Session options before any provider is attached: full graph optimization, and the caller's thread
    /// settings. Shared by every provider.
    /// </summary>
    internal static SessionOptions CreateBaseOptions(StructureEngineOptions options)
    {
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        if (options.IntraOpNumThreads is { } intra and > 0) opts.IntraOpNumThreads = intra;
        if (options.InterOpNumThreads is { } inter and > 0) opts.InterOpNumThreads = inter;

        // Intra-op spinning keeps worker threads busy-waiting between ops. It helps a single hot session
        // but, with several sessions run back to back (det -> cls -> rec) each owning a pool, idle pools
        // spin against the active one. Only set when the caller chose; null keeps ONNX Runtime's default.
        if (options.AllowIntraOpSpinning is { } spin)
        {
            opts.AddSessionConfigEntry("session.intra_op.allow_spinning", spin ? "1" : "0");
        }

        return opts;
    }

    /// <summary>
    /// Attaches the CUDA provider. Without a <see cref="StructureEngineOptions.CudnnConvAlgoSearch"/> choice
    /// this is the plain device-id overload (unchanged behavior); otherwise the provider is configured
    /// through <see cref="OrtCUDAProviderOptions"/> with <c>cudnn_conv_algo_search</c> set.
    /// </summary>
    private static void AppendCuda(SessionOptions opts, StructureEngineOptions options)
    {
        if (options.CudnnConvAlgoSearch is not { } search)
        {
            opts.AppendExecutionProvider_CUDA(options.DeviceId);
            return;
        }

        using var cuda = new OrtCUDAProviderOptions();
        cuda.UpdateOptions(new Dictionary<string, string>
        {
            ["device_id"] = options.DeviceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["cudnn_conv_algo_search"] = EasyOcrSharp.Internal.ExecutionProviderResolver.CudnnSearchValue(search),
        });
        opts.AppendExecutionProvider_CUDA(cuda);
    }

    /// <summary>
    /// Delegating wrapper over <see cref="BuildSessionOptionsWithStatus"/> for callers that only need
    /// the <see cref="SessionOptions"/> (the append-failure status is discarded).
    /// </summary>
    /// <param name="provider">The concrete, already-resolved execution provider to configure.</param>
    /// <param name="options">Engine options supplying thread counts and the accelerator device index.</param>
    /// <param name="logger">Optional logger for provider-attach diagnostics.</param>
    /// <param name="perBoxParallel">
    /// Ignored. It used to cap CPU intra-op threads to 1 for a per-box <c>Parallel.For</c> that never
    /// materialized, which only serialized recognition on multi-core machines; ORT's default thread
    /// pools now apply unless <see cref="StructureEngineOptions.IntraOpNumThreads"/> pins a count.
    /// </param>
    public static SessionOptions BuildSessionOptions(OcrExecutionProvider provider, StructureEngineOptions options, ILogger? logger, bool perBoxParallel = false)
        => BuildSessionOptionsWithStatus(provider, options, logger).Options;

    /// <summary>
    /// Appends one execution provider to the options under construction. Returns null on success, or
    /// the composed human-readable failure hint when the append throws (also logged as a warning, or
    /// written once per process to stderr when no logger exists — see
    /// <see cref="BuildSessionOptionsWithStatus"/>).
    /// </summary>
    private static string? TryAppendProvider(ILogger? logger, OcrExecutionProvider provider, string name, string package, Action append)
    {
        try
        {
            append();
            logger?.LogInformation("ONNX Runtime: {Provider} execution provider enabled.", name);
            return null;
        }
        catch (Exception ex)
        {
            var hint = ComposeFailureHint(provider, name, package, ex);
            if (logger is not null) logger.LogWarning(ex, "{Hint}", hint);
            else WarnStderrOnce(hint);
            return hint;
        }
    }

    /// <summary>
    /// Explains an append failure. The advice hinges on whether the loaded ONNX Runtime contains the
    /// provider at all: if it does, the accelerator package is installed correctly and the fault is in the
    /// provider's own native dependencies, so telling the caller to install that package — which is what
    /// this used to do unconditionally — sends someone who already has it chasing the wrong thing.
    /// </summary>
    private static string ComposeFailureHint(OcrExecutionProvider provider, string name, string package, Exception ex)
    {
        var cudaHint = CudaToolkitHint(ex);
        if (cudaHint is not null)
        {
            var dmlAlternative = OperatingSystem.IsWindows()
                ? " Alternatively, on Windows the Microsoft.ML.OnnxRuntime.DirectML package accelerates " +
                  "any DirectX 12 GPU with no CUDA install at all (set ExecutionProvider to DirectMl or Auto)."
                : string.Empty;
            return $"{name} execution provider unavailable; OCR will run on CPU. {cudaHint}{dmlAlternative}";
        }

        if (ProviderCompiledIn(provider))
        {
            var dependencies = provider == OcrExecutionProvider.Cuda
                ? "an NVIDIA driver, a usable NVIDIA GPU, and a matching CUDA runtime with cuDNN 9 on PATH"
                : "the provider's own native dependencies";
            return $"{name} execution provider unavailable; OCR will run on CPU. The loaded ONNX Runtime does " +
                   $"contain this provider, so {package} is installed correctly — what could not be loaded is " +
                   $"{dependencies}. ({ex.Message})";
        }

        return $"{name} execution provider unavailable; OCR will run on CPU. Install {package} for support. ({ex.Message})";
    }

    /// <summary>
    /// True when the loaded ONNX Runtime reports <paramref name="provider"/> among its available providers,
    /// i.e. the native build carrying it is the one that got deployed.
    /// </summary>
    private static bool ProviderCompiledIn(OcrExecutionProvider provider)
    {
        try { return OrtEnv.Instance().GetAvailableProviders().Contains(NativeNameOf(provider)); }
        catch { return false; }
    }

    // One-time (process-wide) stderr fallback so a provider failure is never completely invisible when
    // the service was constructed without a logger. Interlocked because engines can be built concurrently.
    private static int _stderrWarned;

    private static void WarnStderrOnce(string hint)
    {
        if (Interlocked.Exchange(ref _stderrWarned, 1) != 0) return;
        try { Console.Error.WriteLine($"[EasyOcrSharp] {hint}"); }
        catch { /* stderr can be unavailable (detached console); the hint still travels in SessionBuildResult */ }
    }

    /// <summary>
    /// Recognizes the "the GPU package is installed but its runtime got displaced" state and explains it,
    /// or returns <c>null</c> when that is not what happened.
    /// <para>
    /// <c>Microsoft.ML.OnnxRuntime</c> (CPU) and <c>Microsoft.ML.OnnxRuntime.Gpu</c> both ship the same
    /// native <c>onnxruntime</c> library path, so when a project's graph contains both — which it does
    /// whenever <c>EasyOcrSharp.Gpu</c> is installed, since <c>EasyOcrSharp</c> depends on the CPU package —
    /// NuGet awards that one slot to the CPU package. The CUDA <i>provider</i> library still deploys, next to
    /// a core runtime with no CUDA support compiled in that will never load it. Nothing throws and nothing
    /// logs: the provider simply never appears in <see cref="OrtEnv.GetAvailableProviders"/>, auto-detection
    /// resolves to CPU, and the fallback is completely silent (GitHub issue #6).
    /// </para>
    /// <para>
    /// The giveaway is the CUDA provider library sitting in the application's output while the loaded
    /// runtime denies having CUDA — a combination that cannot arise from a missing install, a missing
    /// driver or a CUDA-major mismatch, all of which leave the provider present but failing to attach.
    /// </para>
    /// </summary>
    /// <param name="baseDirectory">
    /// Application directory to probe for the CUDA provider library; defaults to
    /// <see cref="AppContext.BaseDirectory"/>. Exists so tests can exercise both deployment layouts.
    /// </param>
    internal static string? CudaProviderDisplacedHint(string? baseDirectory = null)
    {
        IReadOnlyCollection<string> available;
        try { available = OrtEnv.Instance().GetAvailableProviders(); }
        catch { return null; }

        if (available.Contains(CudaName)) return null;
        if (!CudaProviderLibraryDeployed(baseDirectory ?? AppContext.BaseDirectory)) return null;

        return "EasyOcrSharp: the CUDA provider library is deployed with this application, but the ONNX " +
               "Runtime that loaded has no CUDA support compiled in, so OCR is running on CPU. That is the " +
               "NuGet native-asset conflict between 'Microsoft.ML.OnnxRuntime' (CPU) and " +
               "'Microsoft.ML.OnnxRuntime.Gpu' — both ship the same 'onnxruntime' native library and only one " +
               "of them can win, and the CPU copy did. Upgrade EasyOcrSharp.Gpu to a release that resolves " +
               "this during the build, or add " +
               "<PackageReference Include=\"Microsoft.ML.OnnxRuntime\" ExcludeAssets=\"native\" /> to the " +
               "application project so only the GPU package supplies the runtime.";
    }

    /// <summary>
    /// True when the CUDA execution provider's native library was deployed alongside the application,
    /// in either the flat (RID-specific publish) or <c>runtimes/&lt;rid&gt;/native</c> (portable) layout.
    /// </summary>
    internal static bool CudaProviderLibraryDeployed(string baseDirectory)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return false;

        var (library, rid) = OperatingSystem.IsWindows()
            ? ("onnxruntime_providers_cuda.dll", "win-x64")
            : ("libonnxruntime_providers_cuda.so", "linux-x64");

        try
        {
            return File.Exists(Path.Combine(baseDirectory, library))
                || File.Exists(Path.Combine(baseDirectory, "runtimes", rid, "native", library));
        }
        catch
        {
            return false; // a restricted or unavailable base directory makes the probe simply inconclusive
        }
    }

    /// <summary>
    /// Recognizes the "wrong CUDA toolkit" flavour of provider-load failure and explains it, or returns
    /// <c>null</c> when the failure is not of that kind.
    /// <para>
    /// When the CUDA provider DLL itself loads but its CUDA dependencies do not, ONNX Runtime reports the
    /// first missing library by name — e.g. <c>cublasLt64_13.dll</c>. The trailing number is the CUDA
    /// <b>major</b> version that runtime was built against, so the message already says which toolkit is
    /// wanted; it is just very easy to misread as a broken CUDA install, because the machine usually has a
    /// perfectly good CUDA of the <em>other</em> major version.
    /// </para>
    /// </summary>
    internal static string? CudaToolkitHint(Exception ex)
    {
        var match = CudaLibraryRegex().Match(ex.Message);
        if (!match.Success) return null;

        string library = match.Value;
        string major = match.Groups["major"].Value;
        string built = major == "13"
            ? "ONNX Runtime 1.27 and later build against CUDA 13"
            : "ONNX Runtime 1.21 through 1.26 build against CUDA 12";

        return $"The loaded ONNX Runtime wants the CUDA {major}.x runtime \u2014 '{library}' was not found on PATH \u2014 " +
               $"so the installed CUDA is a different major version. {built}. Install the CUDA {major}.x runtime " +
               $"together with cuDNN 9 for CUDA {major} \u2014 it installs side by side with other CUDA versions \u2014 " +
               "and put its bin folder on PATH. Referencing an older Microsoft.ML.OnnxRuntime.Gpu to match an older " +
               "CUDA does not work: EasyOcrSharp is compiled against its own ONNX Runtime version and fails to load " +
               "with an older one.";
    }

    /// <summary>
    /// Matches a CUDA runtime library name carrying its major-version suffix, in either platform's spelling:
    /// <c>cublasLt64_13.dll</c> / <c>cudart64_12.dll</c> on Windows, <c>libcublasLt.so.13</c> on Linux. The
    /// two alternatives reuse the <c>major</c> group name, which .NET merges into one capture.
    /// </summary>
    [GeneratedRegex(@"\b(?:lib)?cu[a-z]+(?:64)?(?:_(?<major>\d+)\.dll|\.so\.(?<major>\d+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CudaLibraryRegex();
}
