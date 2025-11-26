using System.Diagnostics;
using System.IO.Compression;
using System.Management;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Handles GPU detection, background CUDA runtime downloads, and activation flow.
/// </summary>
internal static class GpuRuntimeManager
{
    private const string StageFolderName = "gpu_runtime";
    private const string StageMarkerFile = ".gpu-runtime-stage";
    private const string InstallMarkerFile = ".gpu-runtime-ready";
    private const string TorchCpuCommand = "torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cpu --trusted-host download.pytorch.org";
    private const string TorchGpuCommand = "torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu121 --trusted-host download.pytorch.org";

    private static readonly object InitLock = new();
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private static Task? _downloadTask;
    private static CancellationTokenSource? _downloadCts;
    private static string? _pythonHome;
    private static volatile bool _initialized;
    private static volatile bool _gpuAvailable;
    private static volatile bool _runtimeDownloaded;
    private static volatile bool _gpuReady;
    private static volatile bool _runningOnCpu = true;

    internal static void Initialize(ILogger? logger)
    {
        if (_initialized)
        {
            return;
        }

        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            _gpuAvailable = DetectCudaCapableGpu(logger);
            if (_gpuAvailable)
            {
                logger?.LogInformation("CUDA-capable GPU detected. GPU runtime will be managed automatically.");
                _runtimeDownloaded = IsGpuRuntimeStaged();
                _gpuReady = _runtimeDownloaded;
                if (_runtimeDownloaded)
                {
                    logger?.LogInformation("GPU runtime payload already available locally.");
                }
                else
                {
                    StartBackgroundDownload(logger);
                }
            }
            else
            {
                logger?.LogInformation("No CUDA-capable GPU detected. Remaining on CPU mode.");
            }

            _initialized = true;
        }
    }

    internal static void RegisterPythonHome(string pythonHome, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(pythonHome))
        {
            return;
        }

        _pythonHome = Path.GetFullPath(pythonHome);

        if (!_gpuAvailable)
        {
            return;
        }

        if (IsGpuRuntimeApplied())
        {
            _runningOnCpu = false;
            return;
        }

        if (_gpuReady)
        {
            TryActivateGpuRuntime(logger);
        }
    }

    internal static bool TryActivateGpuRuntime(ILogger? logger)
    {
        if (!_gpuAvailable || !_gpuReady || !_runtimeDownloaded || !_runningOnCpu)
        {
            return false;
        }

        var pythonHome = _pythonHome;
        if (string.IsNullOrWhiteSpace(pythonHome) || !Directory.Exists(pythonHome))
        {
            return false;
        }

        var stagePath = GetGpuStagePath();
        if (!Directory.Exists(stagePath))
        {
            return false;
        }

        try
        {
            var targetTorchPath = Path.Combine(pythonHome, "Lib", "site-packages", "torch");
            CopyDirectory(stagePath, targetTorchPath, logger);

            var markerPath = Path.Combine(pythonHome, InstallMarkerFile);
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
            _gpuReady = true;
            _runningOnCpu = false;

            Environment.SetEnvironmentVariable("EASYOCRSHARP_GPU_RUNTIME", targetTorchPath);

            logger?.LogInformation("GPU runtime activated at {TorchPath}. Future OCR calls will use CUDA once PyTorch reloads.", targetTorchPath);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to activate the GPU runtime payload. Staying on CPU mode.");
            return false;
        }
    }

    internal static EasyOcrRuntime.RuntimeStatus GetStatus()
        => new(
            GpuAvailable: _gpuAvailable,
            RuntimeDownloaded: _runtimeDownloaded,
            GpuReady: _gpuReady,
            IsRunningOnCpu: _runningOnCpu);

    internal static string GetRecommendedTorchCommand()
    {
        if (_gpuAvailable && _gpuReady && !_runningOnCpu)
        {
            return TorchGpuCommand;
        }

        return TorchCpuCommand;
    }

    private static bool DetectCudaCapableGpu(ILogger? logger)
    {
        if (CheckCudaRuntimeFiles(logger))
        {
            return true;
        }

        if (CheckNvidiaSmi(logger))
        {
            return true;
        }

        if (OperatingSystem.IsWindows() && CheckWindowsManagementInstrumentation(logger))
        {
            return true;
        }

        if (CheckVendorIdentifiers(logger))
        {
            return true;
        }

        return false;
    }

    private static bool CheckCudaRuntimeFiles(ILogger? logger)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var searchRoots = new List<string>();

                var envVars = Environment.GetEnvironmentVariables();
                foreach (var key in envVars.Keys)
                {
                    if (key is string envName && envName.StartsWith("CUDA_PATH", StringComparison.OrdinalIgnoreCase))
                    {
                        if (envVars[key] is string envValue && Directory.Exists(envValue))
                        {
                            searchRoots.Add(envValue);
                        }
                    }
                }

                var defaultCudaRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA GPU Computing Toolkit", "CUDA");
                if (Directory.Exists(defaultCudaRoot))
                {
                    searchRoots.AddRange(Directory.GetDirectories(defaultCudaRoot));
                }

                var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                searchRoots.AddRange(pathEntries.Where(Directory.Exists));

                foreach (var root in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var candidates = Directory.EnumerateFiles(root, "cudart64_*.dll", SearchOption.AllDirectories)
                        .Take(1);
                    if (candidates.Any())
                    {
                        logger?.LogDebug("Detected CUDA runtime DLLs under {Path}.", root);
                        return true;
                    }
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                var linuxCandidates = new[]
                {
                    "/usr/local/cuda/lib64/libcudart.so",
                    "/usr/lib/x86_64-linux-gnu/libcuda.so",
                    "/usr/lib/wsl/lib/libcuda.so"
                };

                if (linuxCandidates.Any(File.Exists))
                {
                    logger?.LogDebug("Detected CUDA runtime shared objects.");
                    return true;
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                var macCandidates = new[]
                {
                    "/usr/local/cuda/lib/libcudart.dylib",
                    "/Library/Frameworks/CUDA.framework/CUDA"
                };

                if (macCandidates.Any(File.Exists))
                {
                    logger?.LogDebug("Detected CUDA libraries on macOS.");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed while probing for CUDA runtime files.");
        }

        return false;
    }

    private static bool CheckNvidiaSmi(ILogger? logger)
    {
        try
        {
            var fileName = OperatingSystem.IsWindows() ? "nvidia-smi.exe" : "nvidia-smi";

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = "--query-gpu=name --format=csv,noheader",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                return false;
            }

            if (!process.WaitForExit((int)TimeSpan.FromSeconds(1.5).TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                return false;
            }

            if (process.ExitCode == 0)
            {
                var stdout = process.StandardOutput.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    logger?.LogDebug("nvidia-smi detected GPUs: {Output}", stdout.Trim());
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "nvidia-smi probe failed.");
        }

        return false;
    }

    private static bool CheckWindowsManagementInstrumentation(ILogger? logger)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("select Name, AdapterCompatibility from Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                var adapter = obj["AdapterCompatibility"]?.ToString();
                var name = obj["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(adapter) &&
                    adapter.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    logger?.LogDebug("WMI detected NVIDIA adapter: {Name}", name ?? adapter);
                    return true;
                }
            }
        }
        catch (PlatformNotSupportedException)
        {
            // Ignore on non-Windows platforms
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "WMI probe failed.");
        }

        return false;
    }

    private static bool CheckVendorIdentifiers(ILogger? logger)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var output = RunProcessAndCapture("lspci", "-nn", TimeSpan.FromSeconds(1));
                if (!string.IsNullOrWhiteSpace(output) && output.Contains("10de", StringComparison.OrdinalIgnoreCase))
                {
                    logger?.LogDebug("lspci detected NVIDIA vendor ID 10DE.");
                    return true;
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                var output = RunProcessAndCapture("system_profiler", "SPDisplaysDataType", TimeSpan.FromSeconds(2));
                if (!string.IsNullOrWhiteSpace(output) && output.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    logger?.LogDebug("system_profiler detected NVIDIA GPU.");
                    return true;
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                var env = Environment.GetEnvironmentVariable("GPU_DEVICE_0");
                if (!string.IsNullOrEmpty(env) && env.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase))
                {
                    logger?.LogDebug("Environment reported NVIDIA GPU via GPU_DEVICE_0.");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Vendor ID probe failed.");
        }

        return false;
    }

    private static string? RunProcessAndCapture(string fileName, string arguments, TimeSpan timeout)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                return null;
            }

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                return null;
            }

            if (process.ExitCode == 0)
            {
                return process.StandardOutput.ReadToEnd();
            }
        }
        catch
        {
        }

        return null;
    }

    private static void StartBackgroundDownload(ILogger? logger)
    {
        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();
        var token = _downloadCts.Token;

        _downloadTask = Task.Run(async () =>
        {
            try
            {
                await DownloadAndStageGpuRuntimeAsync(logger, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Unexpected error during GPU runtime download.");
            }
        }, token);

        logger?.LogInformation("GPU runtime download started in the background.");
    }

    private static async Task DownloadAndStageGpuRuntimeAsync(ILogger? logger, CancellationToken cancellationToken)
    {
        var stagePath = GetGpuStagePath();
        var tempFile = Path.Combine(Path.GetTempPath(), $"easyocrsharp-gpu-{Guid.NewGuid():N}.zip");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stagePath)!);

            var downloadUrl = ResolveCudaRuntimeUrl();
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                logger?.LogWarning("GPU runtime URL is not configured. Cannot download GPU payload.");
                return;
            }

            logger?.LogInformation("Downloading GPU PyTorch runtime from {Url}", downloadUrl);

            using var response = await HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            if (Directory.Exists(stagePath))
            {
                Directory.Delete(stagePath, recursive: true);
            }

            Directory.CreateDirectory(stagePath);
            ZipFile.ExtractToDirectory(tempFile, stagePath);

            File.WriteAllText(Path.Combine(stagePath, StageMarkerFile), DateTime.UtcNow.ToString("O"));

            _runtimeDownloaded = true;
            _gpuReady = true;
            logger?.LogInformation("GPU runtime downloaded to {StagePath}.", stagePath);
        }
        catch (OperationCanceledException)
        {
            logger?.LogInformation("GPU runtime download cancelled.");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to download GPU runtime payload. CPU mode will remain active.");
        }
        finally
        {
            _downloadTask = null;
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
            }
        }
    }

    private static string ResolveCudaRuntimeUrl()
    {
        var overrideUrl = Environment.GetEnvironmentVariable("EASYOCRSHARP_GPU_RUNTIME_URL");
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            return overrideUrl;
        }

        if (OperatingSystem.IsWindows())
        {
            return "https://download.pytorch.org/libtorch/cu121/libtorch-win-shared-with-deps-2.4.0.zip";
        }

        if (OperatingSystem.IsLinux())
        {
            return "https://download.pytorch.org/libtorch/cu121/libtorch-shared-with-deps-2.4.0.zip";
        }

        if (OperatingSystem.IsMacOS())
        {
            return string.Empty; // CUDA GPUs are generally unsupported on modern macOS
        }

        return string.Empty;
    }

    private static bool IsGpuRuntimeStaged()
    {
        var stageMarker = Path.Combine(GetGpuStagePath(), StageMarkerFile);
        return File.Exists(stageMarker);
    }

    private static string GetGpuStagePath()
    {
        return Path.Combine(PythonInitializer.CacheRoot, StageFolderName);
    }

    private static bool IsGpuRuntimeApplied()
    {
        if (string.IsNullOrWhiteSpace(_pythonHome))
        {
            return false;
        }

        var markerPath = Path.Combine(_pythonHome, InstallMarkerFile);
        return File.Exists(markerPath);
    }

    private static void CopyDirectory(string sourceDir, string targetDir, ILogger? logger)
    {
        foreach (var directory in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, directory);
            var targetSubDir = Path.Combine(targetDir, relative);
            Directory.CreateDirectory(targetSubDir);
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var targetFile = Path.Combine(targetDir, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: true);
        }

        logger?.LogDebug("Copied GPU runtime payload from {Source} to {Target}.", sourceDir, targetDir);
    }
}

