using EasyOcrSharp.Diagnostics;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Pure-logic tests (no model download, CI-safe) for the GPU fallback diagnostics: the CUDA-major and
/// cuDNN hints, the attach-failure explanation, the displaced-runtime detection, and the truthful
/// active-provider reporting. Written to pass on CPU-only machines and on real CUDA machines alike.
/// </summary>
public class GpuDiagnosticsTests
{
    // ---- CudaToolkitHint (library named in the ONNX Runtime message) ----

    private const string Cuda13OnCuda12 =
        "[ONNXRuntimeError] : 1 : FAIL : Error loading " +
        @"""C:\app\onnxruntime_providers_cuda.dll"" which depends on ""cublasLt64_13.dll"" which is missing. (Error 126)";

    [Fact]
    public void Missing_cuda13_library_names_cuda13()
    {
        var hint = ExecutionProviderResolver.CudaToolkitHint(Cuda13OnCuda12);
        Assert.NotNull(hint);
        Assert.Contains("CUDA 13.x", hint);
        Assert.Contains("cublasLt64_13.dll", hint);
        Assert.Contains("1.27", hint);
    }

    [Fact]
    public void Missing_cuda12_library_names_cuda12()
    {
        var hint = ExecutionProviderResolver.CudaToolkitHint(@"depends on ""cudart64_12.dll"" which is missing.");
        Assert.NotNull(hint);
        Assert.Contains("CUDA 12.x", hint);
    }

    [Fact]
    public void Linux_spelling_is_recognized()
    {
        var hint = ExecutionProviderResolver.CudaToolkitHint("libonnxruntime_providers_cuda.so: cannot open shared object file: libcublasLt.so.13");
        Assert.NotNull(hint);
        Assert.Contains("CUDA 13.x", hint);
    }

    [Fact]
    public void Cudnn_suffix_is_not_read_as_a_cuda_major()
    {
        var hint = ExecutionProviderResolver.CudaToolkitHint(@"depends on ""cudnn64_9.dll"" which is missing.");
        Assert.NotNull(hint);
        Assert.Contains("cuDNN 9", hint);
        Assert.DoesNotContain("CUDA 9", hint);
    }

    [Fact]
    public void Cufft_suffix_maps_one_major_ahead()
    {
        Assert.Contains("CUDA 13.x", ExecutionProviderResolver.CudaToolkitHint("cufft64_12.dll is missing")!);
        Assert.Contains("CUDA 12.x", ExecutionProviderResolver.CudaToolkitHint("cufft64_11.dll is missing")!);
    }

    [Fact]
    public void Unrelated_failure_produces_no_cuda_hint()
    {
        Assert.Null(ExecutionProviderResolver.CudaToolkitHint("No CUDA-capable device is detected."));
        Assert.Null(ExecutionProviderResolver.CudaToolkitHint("Failed to load library onnxruntime_providers_shared.dll"));
    }

    // ---- version mapping + dependency probe (library NOT named in the message, as on ORT 1.29 Windows) ----

    [Theory]
    [InlineData("1.30.0", 13)]
    [InlineData("1.29.0", 13)]
    [InlineData("1.27.1", 13)]
    [InlineData("1.26.0", 12)]
    [InlineData("1.21.0", 12)]
    [InlineData("2.0.0", 13)]
    public void Onnx_runtime_version_maps_to_cuda_major(string version, int expected)
        => Assert.Equal(expected, ExecutionProviderResolver.CudaMajorForOnnxRuntime(version));

    [Theory]
    [InlineData("1.20.1")]
    [InlineData("unknown")]
    [InlineData(null)]
    public void Unknown_versions_map_to_null(string? version)
        => Assert.Null(ExecutionProviderResolver.CudaMajorForOnnxRuntime(version));

    [Fact]
    public void Probe_reports_cuda13_missing_and_notes_installed_cuda12()
    {
        var present = new HashSet<string> { "cublasLt64_12.dll", "cublas64_12.dll", "cudnn64_9.dll" };
        var hint = ExecutionProviderResolver.ProbeCudaDependencies(13, windows: true, present.Contains);
        Assert.NotNull(hint);
        Assert.Contains("cublasLt64_13.dll", hint);
        Assert.Contains("CUDA 12 is installed", hint);
    }

    [Fact]
    public void Probe_reports_missing_cudnn_when_cuda_is_present()
    {
        var present = new HashSet<string> { "libcublasLt.so.13", "libcublas.so.13" };
        var hint = ExecutionProviderResolver.ProbeCudaDependencies(13, windows: false, present.Contains);
        Assert.NotNull(hint);
        Assert.Contains("libcudnn.so.9", hint);
    }

    [Fact]
    public void Probe_returns_null_when_everything_is_found()
        => Assert.Null(ExecutionProviderResolver.ProbeCudaDependencies(13, windows: true, _ => true));

    [Fact]
    public void Library_load_failure_messages_are_recognized()
    {
        Assert.True(ExecutionProviderResolver.LooksLikeLibraryLoadFailure("[ErrorCode:Fail] OrtSessionOptionsAppendExecutionProvider_Cuda: Failed to load shared library"));
        Assert.False(ExecutionProviderResolver.LooksLikeLibraryLoadFailure("out of memory"));
    }

    // ---- ComposeFailureHint ----

    [Fact]
    public void Provider_missing_from_runtime_advises_installing_the_package()
    {
        var hint = ExecutionProviderResolver.ComposeFailureHint("CUDA", "EasyOcrSharp.Gpu", isCuda: true, "entry point not found",
            providerCompiledIn: false, providerLibraryDeployed: false, cudaDependencyHint: null, GpuProbe.GpuVendor.Nvidia);
        Assert.Contains("install EasyOcrSharp.Gpu", hint);
        Assert.Contains("OCR will run on CPU", hint);
    }

    [Fact]
    public void Provider_deployed_but_denied_is_diagnosed_as_displaced_runtime()
    {
        var hint = ExecutionProviderResolver.ComposeFailureHint("CUDA", "EasyOcrSharp.Gpu", isCuda: true, "entry point not found",
            providerCompiledIn: false, providerLibraryDeployed: true, cudaDependencyHint: null, GpuProbe.GpuVendor.Nvidia);
        Assert.Contains("ExcludeAssets", hint);
        Assert.Contains("EasyOcrSharpGpuPreferGpuRuntime", hint);
        Assert.DoesNotContain("install EasyOcrSharp.Gpu", hint);
    }

    [Fact]
    public void Compiled_in_provider_points_at_native_dependencies_not_the_package()
    {
        var hint = ExecutionProviderResolver.ComposeFailureHint("CUDA", "EasyOcrSharp.Gpu", isCuda: true, "Failed to load shared library",
            providerCompiledIn: true, providerLibraryDeployed: true, cudaDependencyHint: null, GpuProbe.GpuVendor.Nvidia);
        Assert.Contains("installed correctly", hint);
        Assert.Contains("cuDNN 9", hint);
        Assert.DoesNotContain("install EasyOcrSharp.Gpu", hint);
    }

    [Fact]
    public void Cuda_dependency_hint_is_embedded_and_non_nvidia_host_is_noted()
    {
        var hint = ExecutionProviderResolver.ComposeFailureHint("CUDA", "EasyOcrSharp.Gpu", isCuda: true, "Failed to load shared library",
            providerCompiledIn: true, providerLibraryDeployed: true, cudaDependencyHint: "DEPENDENCY-HINT", GpuProbe.GpuVendor.Amd);
        Assert.Contains("DEPENDENCY-HINT", hint);
        Assert.Contains("no NVIDIA GPU was detected", hint);
    }

    [Fact]
    public void Session_init_failure_hint_names_the_provider()
    {
        var hint = ExecutionProviderResolver.ComposeSessionInitFailureHint(OcrExecutionProvider.Cuda, "boom", GpuProbe.GpuVendor.Nvidia);
        Assert.Contains("Cuda", hint);
        Assert.Contains("boom", hint);
    }

    // ---- displaced runtime detection ----

    [Fact]
    public void Output_without_cuda_provider_is_not_displaced()
    {
        using var dir = new TempDirectory();
        Assert.False(ExecutionProviderResolver.CudaProviderLibraryDeployed(dir.Path));
        Assert.Null(ExecutionProviderResolver.CudaProviderDisplacedHint(new[] { "CPUExecutionProvider" }, dir.Path));
    }

    [SkippableFact]
    public void Cuda_provider_found_in_portable_and_flat_layouts()
    {
        Skip.IfNot(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), "CUDA ships for Windows and Linux only.");
        using var portable = new TempDirectory();
        var native = Path.Combine(portable.Path, "runtimes", NativeRid, "native");
        Directory.CreateDirectory(native);
        File.WriteAllBytes(Path.Combine(native, CudaProviderLibraryName), Array.Empty<byte>());
        Assert.True(ExecutionProviderResolver.CudaProviderLibraryDeployed(portable.Path));

        using var flat = new TempDirectory();
        File.WriteAllBytes(Path.Combine(flat.Path, CudaProviderLibraryName), Array.Empty<byte>());
        Assert.True(ExecutionProviderResolver.CudaProviderLibraryDeployed(flat.Path));
    }

    [SkippableFact]
    public void Deployed_provider_with_runtime_lacking_cuda_is_diagnosed()
    {
        Skip.IfNot(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), "CUDA ships for Windows and Linux only.");
        using var dir = new TempDirectory();
        File.WriteAllBytes(Path.Combine(dir.Path, CudaProviderLibraryName), Array.Empty<byte>());

        var hint = ExecutionProviderResolver.CudaProviderDisplacedHint(new[] { "AzureExecutionProvider", "CPUExecutionProvider" }, dir.Path);
        Assert.NotNull(hint);
        Assert.Contains("Microsoft.ML.OnnxRuntime.Gpu", hint);
        Assert.Contains("ExcludeAssets", hint);

        Assert.Null(ExecutionProviderResolver.CudaProviderDisplacedHint(new[] { "CUDAExecutionProvider", "CPUExecutionProvider" }, dir.Path));
    }

    // ---- truthful provider reporting ----

    [Fact]
    public void Cpu_request_builds_cpu_with_no_hint()
    {
        var result = ExecutionProviderResolver.BuildSessionOptionsWithStatus(OcrExecutionProvider.Cpu, new EngineOptions(), logger: null);
        using var options = result.Options;
        Assert.Equal(OcrExecutionProvider.Cpu, result.ActiveProvider);
        Assert.Null(result.ProviderFailureHint);
    }

    [Fact]
    public void Cuda_request_never_throws_and_reports_truthfully()
    {
        var result = ExecutionProviderResolver.BuildSessionOptionsWithStatus(OcrExecutionProvider.Cuda, new EngineOptions(), logger: null);
        using var options = result.Options;
        if (result.ActiveProvider == OcrExecutionProvider.Cuda)
        {
            Assert.Null(result.ProviderFailureHint);
        }
        else
        {
            Assert.Equal(OcrExecutionProvider.Cpu, result.ActiveProvider);
            Assert.False(string.IsNullOrWhiteSpace(result.ProviderFailureHint));
        }
    }

    [Fact]
    public void Service_reports_active_provider_and_runtime_info()
    {
        using var service = new EasyOcrService(new EasyOcrServiceOptions { ExecutionProvider = OcrExecutionProvider.Cuda });

        // UseGpu keeps its documented meaning (an accelerator was selected); the active provider is truthful.
        Assert.True(service.UseGpu);
        var info = service.GetRuntimeInfo();
        Assert.Equal(OcrExecutionProvider.Cuda, info.RequestedProvider);
        Assert.Equal(OcrExecutionProvider.Cuda, info.ResolvedProvider);
        Assert.Equal(service.ActiveExecutionProvider, info.ActiveProvider);
        Assert.NotEqual(OcrExecutionProvider.Auto, info.ActiveProvider);

        if (!OrtEnv.Instance().GetAvailableProviders().Contains("CUDAExecutionProvider"))
        {
            Assert.Equal(OcrExecutionProvider.Cpu, service.ActiveExecutionProvider);
            Assert.NotNull(service.GpuAccelerationHint);
            Assert.Equal(service.GpuAccelerationHint, info.Hint);
        }

        var text = info.ToString();
        Assert.Contains("Active provider:", text);
        Assert.Contains("Available providers:", text);
    }

    [Fact]
    public void OcrResult_execution_provider_defaults_to_cpu()
        => Assert.Equal(OcrExecutionProvider.Cpu, OcrResult.Empty.ExecutionProvider);

    [Fact]
    public void Gpu_probe_description_never_throws()
        => Assert.False(string.IsNullOrWhiteSpace(GpuProbe.Describe()));

    private static string CudaProviderLibraryName => OperatingSystem.IsWindows()
        ? "onnxruntime_providers_cuda.dll"
        : "libonnxruntime_providers_cuda.so";

    private static string NativeRid => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "easyocrsharp-gpu-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* a locked temp file must not fail the test */ }
        }
    }
}
