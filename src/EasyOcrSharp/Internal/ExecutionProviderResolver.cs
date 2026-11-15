using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using EasyOcrSharp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Outcome of building <see cref="SessionOptions"/> for an already-resolved provider.
/// <see cref="ActiveProvider"/> is the provider the options actually carry: the requested one when it
/// attached, or <see cref="OcrExecutionProvider.Cpu"/> when an accelerator failed to attach — in which
/// case <see cref="ProviderFailureHint"/> explains the failure and how to fix it. A null hint means
/// nothing went wrong.
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

    /// <summary>The NuGet package that brings the CUDA build of ONNX Runtime.</summary>
    internal const string GpuPackage = "EasyOcrSharp.Gpu";

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

    /// <summary>ONNX Runtime's native name for a provider (<c>CUDAExecutionProvider</c>, ...).</summary>
    internal static string NativeNameOf(OcrExecutionProvider provider) => provider switch
    {
        OcrExecutionProvider.Cuda => CudaName,
        OcrExecutionProvider.DirectMl => DmlName,
        OcrExecutionProvider.CoreMl => CoreMlName,
        _ => "CPUExecutionProvider",
    };

    /// <summary>
    /// Builds <see cref="SessionOptions"/> for a concrete (already-resolved) provider. A non-CPU
    /// provider that fails to attach logs a warning and leaves the options on CPU rather than throwing.
    /// Callers that need to know whether the accelerator really attached use
    /// <see cref="BuildSessionOptionsWithStatus"/>.
    /// </summary>
    /// <param name="provider">The concrete, already-resolved execution provider to configure.</param>
    /// <param name="options">Engine options supplying optional intra/inter-op thread counts, intra-op spinning and the cuDNN algorithm search.</param>
    /// <param name="logger">Optional logger for provider-attach diagnostics.</param>
    /// <param name="perBoxParallel">
    /// True for the per-box CRNN recognizer, which is invoked from a data-parallel <c>Parallel.For</c> over
    /// regions. On CPU, when the caller hasn't pinned a thread count, its intra-op threads are capped to 1 so
    /// the box-level parallelism supplies the concurrency instead of fighting ORT's intra-op pool for it. The
    /// CRAFT detector (a single big run per image) keeps the default and benefits from intra-op parallelism.
    /// </param>
    public static SessionOptions BuildSessionOptions(OcrExecutionProvider provider, EngineOptions options, ILogger? logger, bool perBoxParallel = false)
        => BuildSessionOptionsWithStatus(provider, options, logger, perBoxParallel).Options;

    /// <summary>
    /// Builds <see cref="SessionOptions"/> for a concrete (already-resolved) provider and reports what
    /// actually got configured. A non-CPU provider that fails to attach does not throw: the returned
    /// options run on CPU, <see cref="SessionBuildResult.ActiveProvider"/> is
    /// <see cref="OcrExecutionProvider.Cpu"/>, and <see cref="SessionBuildResult.ProviderFailureHint"/>
    /// explains the failure.
    /// <para>
    /// The hint goes to <paramref name="logger"/> as a warning; when no logger is configured it is
    /// written once per process to <see cref="Console.Error"/> instead, so a default-constructed service
    /// is never silently degraded.
    /// </para>
    /// </summary>
    /// <param name="provider">The concrete, already-resolved execution provider to configure.</param>
    /// <param name="options">Engine options supplying optional intra/inter-op thread counts, intra-op spinning and the cuDNN algorithm search.</param>
    /// <param name="logger">Optional logger for provider-attach diagnostics.</param>
    /// <param name="perBoxParallel">See <see cref="BuildSessionOptions"/>.</param>
    public static SessionBuildResult BuildSessionOptionsWithStatus(OcrExecutionProvider provider, EngineOptions options, ILogger? logger, bool perBoxParallel = false)
    {
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        if (options.IntraOpNumThreads is { } intra and > 0) opts.IntraOpNumThreads = intra;
        else if (perBoxParallel && provider == OcrExecutionProvider.Cpu) opts.IntraOpNumThreads = 1;
        if (options.InterOpNumThreads is { } inter and > 0) opts.InterOpNumThreads = inter;

        foreach (var (key, value) in SessionConfigEntriesFor(options))
        {
            opts.AddSessionConfigEntry(key, value);
        }

        string? failureHint;
        switch (provider)
        {
            case OcrExecutionProvider.Cuda:
                failureHint = TryAppendProvider(logger, provider, "CUDA", GpuPackage, () => AppendCuda(opts, options));
                break;
            case OcrExecutionProvider.DirectMl:
                // DirectML needs sequential execution with memory pattern disabled.
                opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                opts.EnableMemoryPattern = false;
                failureHint = TryAppendProvider(logger, provider, "DirectML", "Microsoft.ML.OnnxRuntime.DirectML", () => opts.AppendExecutionProvider_DML(0));
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

    /// <summary>ONNX Runtime's session config key behind <see cref="EngineOptions.AllowIntraOpSpinning"/>.</summary>
    internal const string IntraOpSpinningKey = "session.intra_op.allow_spinning";

    /// <summary>
    /// The session config entries <paramref name="options"/> asks for — empty when the caller left every
    /// knob at null, so ONNX Runtime's defaults apply untouched.
    /// </summary>
    /// <remarks>
    /// Intra-op spinning keeps worker threads busy-waiting between ops. It helps a single hot session but,
    /// with the detector and recognizer sessions run back to back, each owning a pool, idle pools spin
    /// against the active one. Only set when the caller chose; null keeps ONNX Runtime's default (on).
    /// </remarks>
    internal static IReadOnlyList<KeyValuePair<string, string>> SessionConfigEntriesFor(EngineOptions options)
        => options.AllowIntraOpSpinning is { } spin
            ? [new KeyValuePair<string, string>(IntraOpSpinningKey, spin ? "1" : "0")]
            : [];

    /// <summary>
    /// Attaches the CUDA provider. Without a <see cref="EngineOptions.CudnnConvAlgoSearch"/> choice this is
    /// the plain parameterless overload (device 0, every provider option at its default — unchanged
    /// behavior); otherwise the provider is configured through <see cref="OrtCUDAProviderOptions"/> with
    /// <c>cudnn_conv_algo_search</c> set and the same device.
    /// </summary>
    private static void AppendCuda(SessionOptions opts, EngineOptions options)
    {
        if (CudaProviderOptionsFor(options) is not { } values)
        {
            opts.AppendExecutionProvider_CUDA();
            return;
        }

        using var cuda = new OrtCUDAProviderOptions();
        cuda.UpdateOptions(values);
        opts.AppendExecutionProvider_CUDA(cuda);
    }

    /// <summary>
    /// The CUDA provider options <paramref name="options"/> asks for, or null when it asks for none (the
    /// provider is then appended with ONNX Runtime's defaults).
    /// </summary>
    internal static Dictionary<string, string>? CudaProviderOptionsFor(EngineOptions options)
        => options.CudnnConvAlgoSearch is { } search
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["device_id"] = "0",
                ["cudnn_conv_algo_search"] = CudnnSearchValue(search),
            }
            : null;

    /// <summary>
    /// ONNX Runtime's string for a <see cref="CudnnConvolutionAlgorithmSearch"/> value. An undefined value
    /// maps to <c>EXHAUSTIVE</c>, ONNX Runtime's own default, rather than failing the provider attach.
    /// </summary>
    internal static string CudnnSearchValue(CudnnConvolutionAlgorithmSearch search) => search switch
    {
        CudnnConvolutionAlgorithmSearch.Heuristic => "HEURISTIC",
        CudnnConvolutionAlgorithmSearch.Default => "DEFAULT",
        _ => "EXHAUSTIVE",
    };

    /// <summary>
    /// Appends one execution provider. Returns null on success, or the composed failure hint when the
    /// append throws (logged as a warning, or written once to stderr when there is no logger).
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
            Warn(logger, ex, hint);
            return hint;
        }
    }

    /// <summary>
    /// Surfaces an accelerator failure: as a warning on <paramref name="logger"/> when one exists,
    /// otherwise once per process on standard error.
    /// </summary>
    internal static void Warn(ILogger? logger, Exception? ex, string hint)
    {
        if (logger is not null) logger.LogWarning(ex, "{Hint}", hint);
        else WarnStderrOnce(hint);
    }

    // One-time (process-wide) stderr fallback so an accelerator failure is never completely invisible when
    // the service was constructed without a logger. Interlocked because engines can be built concurrently.
    private static int _stderrWarned;

    private static void WarnStderrOnce(string hint)
    {
        if (Interlocked.Exchange(ref _stderrWarned, 1) != 0) return;
        try { Console.Error.WriteLine(hint); }
        catch { /* stderr can be unavailable (detached console); the hint is still exposed by the service */ }
    }

    // ---- failure explanations ----------------------------------------------------------------------

    private const string HintPrefix = "EasyOcrSharp: ";

    /// <summary>
    /// Gathers the facts an append-failure explanation depends on (whether the loaded runtime contains
    /// the provider, whether the CUDA provider library is deployed, which CUDA library is missing, the
    /// host GPU vendor) and composes the hint via the pure overload.
    /// </summary>
    private static string ComposeFailureHint(OcrExecutionProvider provider, string name, string package, Exception ex)
    {
        bool isCuda = provider == OcrExecutionProvider.Cuda;
        bool compiledIn = ProviderCompiledIn(provider);
        return ComposeFailureHint(
            name,
            package,
            isCuda,
            ex.Message,
            compiledIn,
            providerLibraryDeployed: isCuda && !compiledIn && CudaProviderLibraryDeployed(AppContext.BaseDirectory),
            cudaDependencyHint: isCuda && compiledIn ? DiagnoseCudaDependencies(ex.Message) : null,
            hostVendor: isCuda ? GpuProbe.Detect() : GpuProbe.GpuVendor.None);
    }

    /// <summary>
    /// Explains an append failure. The advice hinges on whether the loaded ONNX Runtime contains the
    /// provider at all. If it does, the accelerator package is installed correctly and the fault is in the
    /// provider's own native dependencies (driver, CUDA runtime, cuDNN) — telling that caller to "install
    /// the package" sends someone who already has it chasing the wrong thing. If it does not, but the CUDA
    /// provider library was deployed anyway, the CPU runtime displaced the GPU one (see
    /// <see cref="DisplacedRuntimeMessage"/>). Only when neither holds is the package genuinely missing.
    /// </summary>
    internal static string ComposeFailureHint(
        string name,
        string package,
        bool isCuda,
        string errorMessage,
        bool providerCompiledIn,
        bool providerLibraryDeployed,
        string? cudaDependencyHint,
        GpuProbe.GpuVendor hostVendor)
    {
        var lead = $"{HintPrefix}the {name} execution provider could not be attached, so OCR will run on CPU.";
        var vendorNote = isCuda ? NonNvidiaNote(hostVendor) : string.Empty;

        if (!providerCompiledIn)
        {
            if (isCuda && providerLibraryDeployed)
                return $"{lead} {DisplacedRuntimeMessage}{vendorNote}";
            return $"{lead} The loaded ONNX Runtime does not contain this provider; install {package} for support.{vendorNote} ({errorMessage})";
        }

        if (cudaDependencyHint is not null)
            return $"{lead} {cudaDependencyHint}{vendorNote} (ONNX Runtime: {errorMessage})";

        var dependencies = isCuda
            ? "an NVIDIA driver, a usable NVIDIA GPU, and the matching CUDA runtime with cuDNN 9 on PATH"
            : "the provider's own native dependencies";
        return $"{lead} The loaded ONNX Runtime does contain this provider, so {package} is installed correctly; " +
               $"what could not be loaded is {dependencies}.{vendorNote} ({errorMessage})";
    }

    /// <summary>
    /// Explains a session that failed to initialize on an accelerator that had attached successfully —
    /// the failure moved from append time to the first model load (typically no usable device, an
    /// unsupported GPU or driver, or insufficient GPU memory).
    /// </summary>
    internal static string ComposeSessionInitFailureHint(OcrExecutionProvider provider, string errorMessage, GpuProbe.GpuVendor hostVendor)
    {
        var vendorNote = provider == OcrExecutionProvider.Cuda ? NonNvidiaNote(hostVendor) : string.Empty;
        return $"{HintPrefix}the {provider} execution provider attached, but an ONNX session failed to initialize on it " +
               "(commonly no usable device, an unsupported GPU or driver, or insufficient GPU memory), so all " +
               $"sessions now run on CPU.{vendorNote} ({errorMessage})";
    }

    private static string NonNvidiaNote(GpuProbe.GpuVendor hostVendor) => hostVendor is GpuProbe.GpuVendor.Amd or GpuProbe.GpuVendor.Intel or GpuProbe.GpuVendor.Other
        ? $" Note: no NVIDIA GPU was detected on this machine (found: {hostVendor}); CUDA runs only on NVIDIA GPUs."
        : string.Empty;

    /// <summary>
    /// True when the loaded ONNX Runtime reports <paramref name="provider"/> among its available providers,
    /// i.e. the native build carrying it is the one that got deployed.
    /// </summary>
    internal static bool ProviderCompiledIn(OcrExecutionProvider provider)
    {
        try { return OrtEnv.Instance().GetAvailableProviders().Contains(NativeNameOf(provider)); }
        catch { return false; }
    }

    // ---- displaced GPU runtime -------------------------------------------------------------------

    /// <summary>
    /// The explanation for the "GPU package installed but its runtime got displaced" state:
    /// <c>Microsoft.ML.OnnxRuntime</c> (CPU) and <c>Microsoft.ML.OnnxRuntime.Gpu</c> ship the same native
    /// <c>onnxruntime</c> library path, and NuGet gives that slot to the CPU package, so the CUDA provider
    /// library deploys next to a core runtime that cannot load it.
    /// </summary>
    internal const string DisplacedRuntimeMessage =
        "The CUDA provider library is deployed with this application, but the ONNX Runtime that loaded has " +
        "no CUDA support compiled in. That is the NuGet native-asset conflict between Microsoft.ML.OnnxRuntime " +
        "(CPU) and Microsoft.ML.OnnxRuntime.Gpu: both ship the same 'onnxruntime' native library and the CPU copy " +
        "won. EasyOcrSharp.Gpu 3.1.1 and later replace it with the GPU copy during build and publish, unless " +
        "EasyOcrSharpGpuPreferGpuRuntime is set to false. On an older EasyOcrSharp.Gpu, upgrade it, or add " +
        "<PackageReference Include=\"Microsoft.ML.OnnxRuntime\" Version=\"(the version EasyOcrSharp uses)\" " +
        "ExcludeAssets=\"native\" /> to the application project so only the GPU package supplies the runtime.";

    /// <summary>
    /// Recognizes the displaced-runtime state for the loaded runtime and application directory, or returns
    /// null when that is not what happened.
    /// </summary>
    internal static string? CudaProviderDisplacedHint()
    {
        IReadOnlyCollection<string> available;
        try { available = OrtEnv.Instance().GetAvailableProviders(); }
        catch { return null; }
        return CudaProviderDisplacedHint(available, AppContext.BaseDirectory);
    }

    /// <summary>
    /// The giveaway for a displaced runtime is the CUDA provider library sitting in the application's
    /// output while the loaded runtime denies having CUDA — a combination no missing driver, missing CUDA
    /// toolkit or CUDA-major mismatch can produce (those leave the provider present but failing to attach).
    /// Checked independently of the hardware probe, so it also fires on Linux where the probe is a no-op.
    /// </summary>
    internal static string? CudaProviderDisplacedHint(IReadOnlyCollection<string> availableProviders, string baseDirectory)
    {
        if (availableProviders.Contains(CudaName)) return null;
        if (!CudaProviderLibraryDeployed(baseDirectory)) return null;
        return $"{HintPrefix}OCR is running on CPU although {GpuPackage} is installed. {DisplacedRuntimeMessage}";
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

    // ---- CUDA toolkit / cuDNN diagnosis -------------------------------------------------------------

    /// <summary>
    /// Works out which CUDA dependency stopped the CUDA provider from loading. Some ONNX Runtime builds
    /// name the missing library in the exception text (<c>cublasLt64_13.dll</c>, <c>libcublasLt.so.13</c>);
    /// the 1.29 Windows build does not — its managed exception only says "Failed to load shared library"
    /// and the library name goes to ONNX Runtime's own log. So when the message names nothing, the CUDA
    /// libraries the loaded runtime needs are looked for directly.
    /// </summary>
    private static string? DiagnoseCudaDependencies(string errorMessage)
    {
        var named = CudaToolkitHint(errorMessage);
        if (named is not null) return named;
        if (!LooksLikeLibraryLoadFailure(errorMessage)) return null;

        string? version;
        try { version = OrtEnv.Instance().GetVersionString(); }
        catch { return null; }

        return CudaMajorForOnnxRuntime(version) is { } wanted
            ? ProbeCudaDependencies(wanted, OperatingSystem.IsWindows(), NativeLibraryFindable)
            : null;
    }

    internal static bool LooksLikeLibraryLoadFailure(string errorMessage) =>
        errorMessage.Contains("load shared library", StringComparison.OrdinalIgnoreCase)
        || errorMessage.Contains("Error 126", StringComparison.OrdinalIgnoreCase)
        || errorMessage.Contains("cannot open shared object", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The CUDA major version a given ONNX Runtime release's GPU build targets: 13 from 1.27 on, 12 for
    /// 1.21 through 1.26 (verified against the published provider libraries' imports). Null when unknown.
    /// </summary>
    internal static int? CudaMajorForOnnxRuntime(string? onnxRuntimeVersion)
    {
        if (onnxRuntimeVersion is null) return null;
        var match = Regex.Match(onnxRuntimeVersion, @"^\s*(\d+)\.(\d+)");
        if (!match.Success) return null;
        int major = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        int minor = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (major > 1 || (major == 1 && minor >= 27)) return 13;
        if (major == 1 && minor >= 21) return 12;
        return null;
    }

    /// <summary>
    /// Checks the CUDA libraries a runtime built for CUDA <paramref name="wantedCudaMajor"/> loads, in the
    /// order they fail in practice (cuBLASLt, cuBLAS, then cuDNN 9), and explains the first one missing.
    /// Null when all of them are found — the failure is then not a missing-toolkit problem.
    /// </summary>
    internal static string? ProbeCudaDependencies(int wantedCudaMajor, bool windows, Func<string, bool> libraryFindable)
    {
        foreach (var stem in new[] { "cublasLt", "cublas" })
        {
            var library = CudaLibraryFileName(stem, wantedCudaMajor, windows);
            if (libraryFindable(library)) continue;

            int otherMajor = wantedCudaMajor == 13 ? 12 : 13;
            bool otherInstalled = libraryFindable(CudaLibraryFileName(stem, otherMajor, windows));
            return CudaMajorHint(library, wantedCudaMajor, otherInstalled ? otherMajor : null);
        }

        var cudnn = CudaLibraryFileName("cudnn", 9, windows);
        return libraryFindable(cudnn) ? null : CudnnHint(cudnn, 9, wantedCudaMajor);
    }

    /// <summary>File name of a versioned CUDA library on the given platform (<c>cublasLt64_13.dll</c> / <c>libcublasLt.so.13</c>).</summary>
    internal static string CudaLibraryFileName(string stem, int major, bool windows)
        => windows ? $"{stem}64_{major}.dll" : $"lib{stem}.so.{major}";

    /// <summary>
    /// Recognizes a CUDA / cuDNN library named in an ONNX Runtime error message and explains what is
    /// missing, or returns null when the message names none. Only libraries whose suffix identifies the
    /// CUDA major are interpreted as such: cuBLAS, cuBLASLt and the CUDA runtime carry the CUDA major
    /// itself, cuFFT is one behind (cuFFT 11 ships with CUDA 12, cuFFT 12 with CUDA 13), and cuDNN's suffix
    /// is cuDNN's own major — reading <c>cudnn64_9.dll</c> as "CUDA 9" would be exactly the wrong advice.
    /// </summary>
    internal static string? CudaToolkitHint(string errorMessage)
    {
        var match = CudaLibraryRegex().Match(errorMessage);
        if (!match.Success) return null;

        string library = match.Value;
        string stem = match.Groups["stem"].Value.ToLowerInvariant();
        int suffix = int.Parse(match.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture);

        return stem switch
        {
            "cudnn" => CudnnHint(library, suffix, cudaMajor: null),
            "cufft" when suffix is 11 or 12 => CudaMajorHint(library, suffix + 1, otherInstalledMajor: null),
            "cufft" => null,
            _ => CudaMajorHint(library, suffix, otherInstalledMajor: null),
        };
    }

    private static string CudaMajorHint(string library, int cudaMajor, int? otherInstalledMajor)
    {
        var other = otherInstalledMajor is { } o
            ? $" CUDA {o} is installed, but CUDA major versions are not interchangeable."
            : string.Empty;
        return $"The loaded ONNX Runtime was built against CUDA {cudaMajor}.x, and the CUDA {cudaMajor} library " +
               $"'{library}' could not be found on PATH.{other} ONNX Runtime 1.27 and later target CUDA 13 (1.21 " +
               $"through 1.26 targeted CUDA 12). Install the CUDA {cudaMajor}.x runtime together with cuDNN 9 for " +
               $"CUDA {cudaMajor} — it installs side by side with other CUDA versions — and put its bin folder on " +
               "PATH. Referencing an older Microsoft.ML.OnnxRuntime.Gpu to match an older CUDA does not work: " +
               "EasyOcrSharp is compiled against its own ONNX Runtime version and fails to load with an older one.";
    }

    private static string CudnnHint(string library, int cudnnMajor, int? cudaMajor)
    {
        var forCuda = cudaMajor is { } c ? $" for CUDA {c}" : string.Empty;
        return $"The CUDA provider needs cuDNN {cudnnMajor} ('{library}'), which could not be found on PATH. " +
               $"Install cuDNN {cudnnMajor}{forCuda} and put its bin folder on PATH.";
    }

    /// <summary>
    /// Matches a CUDA / cuDNN library name with its version suffix, in either platform's spelling:
    /// <c>cublasLt64_13.dll</c>, <c>cudnn64_9.dll</c> on Windows; <c>libcublasLt.so.13</c> on Linux. The two
    /// alternatives reuse the <c>major</c> group name, which .NET merges into one capture.
    /// </summary>
    [GeneratedRegex(@"\b(?:lib)?(?<stem>cublasLt|cublas|cudart|cufft|cudnn)(?:64_(?<major>\d+)\.dll|\.so\.(?<major>\d+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CudaLibraryRegex();

    /// <summary>
    /// Whether the OS loader could find a native library by file name. On Windows this looks where the
    /// loader looks for the CUDA provider's dependencies (application folders, System32, PATH) without
    /// loading anything; elsewhere it asks the dynamic loader. Errs towards "found" when the check itself
    /// fails, so an inconclusive probe never produces a wrong "missing" diagnosis.
    /// </summary>
    private static bool NativeLibraryFindable(string fileName)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                if (!NativeLibrary.TryLoad(fileName, out var handle)) return false;
                NativeLibrary.Free(handle);
                return true;
            }

            var baseDirectory = AppContext.BaseDirectory;
            var directories = new List<string>
            {
                baseDirectory,
                Path.Combine(baseDirectory, "runtimes", "win-x64", "native"),
                Environment.SystemDirectory,
            };
            directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static d => d.Trim('"')));

            foreach (var directory in directories)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, fileName))) return true;
                }
                catch
                {
                    // A malformed PATH entry is skipped, not fatal.
                }
            }
            return false;
        }
        catch
        {
            return true;
        }
    }
}
