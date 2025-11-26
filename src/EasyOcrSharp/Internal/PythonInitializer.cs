using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Python.Included;
using Python.Runtime;

namespace EasyOcrSharp.Internal;

internal static class PythonInitializer
{
    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static readonly Lazy<string> DataRoot = new(ResolveDataRoot, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly string[] RequiredModules = { "torch", "torchvision", "torchaudio", "Pillow" };
    private static bool? _gpuAvailable;
    private static string? _recommendedTorchVersion;
    private static EasyOcrRuntime.RuntimeStatus? _lastRuntimeStatus;
    private static readonly ConcurrentDictionary<string, bool> InstalledModules = new(StringComparer.OrdinalIgnoreCase);

    private static bool _initialized;

    internal static string CacheRoot => DataRoot.Value;

    internal static async Task<TimingTracker> EnsureInitializedAsync(ILogger? logger, CancellationToken cancellationToken, string? customPythonPath = null)
    {
        var timingTracker = new TimingTracker(logger);
        EasyOcrRuntime.Initialize(logger);
        var pythonHome = customPythonPath ?? (await GetEmbeddedRuntimeAsync(logger, cancellationToken).ConfigureAwait(false));
        
        if (_initialized && string.IsNullOrWhiteSpace(customPythonPath))
        {
            using (timingTracker.StartTiming("ModuleVerification", "Verifying required modules"))
            {
                if (!string.IsNullOrEmpty(pythonHome))
                {
                    EasyOcrRuntime.RegisterPythonHome(pythonHome, logger);
                    var promotedToGpu = EasyOcrRuntime.TryActivateGpuRuntime(logger);
                    if (promotedToGpu)
                    {
                        ResetTorchInstallState(logger);
                    }

                    await EnsureRequiredModulesAsync(logger, cancellationToken, pythonHome, timingTracker).ConfigureAwait(false);
                }
            }
            return timingTracker;
        }

        await InitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized && string.IsNullOrWhiteSpace(customPythonPath))
            {
                using (timingTracker.StartTiming("ModuleVerification", "Verifying required modules"))
                {
                    if (!string.IsNullOrEmpty(pythonHome))
                    {
                        EasyOcrRuntime.RegisterPythonHome(pythonHome, logger);
                        var promotedToGpu = EasyOcrRuntime.TryActivateGpuRuntime(logger);
                        if (promotedToGpu)
                        {
                            ResetTorchInstallState(logger);
                        }

                        await EnsureRequiredModulesAsync(logger, cancellationToken, pythonHome, timingTracker).ConfigureAwait(false);
                    }
                }
                return timingTracker;
            }

            logger?.LogInformation("Starting Python runtime initialization...");

            using (timingTracker.StartTiming("EnvironmentSetup", "Preparing environment and cache directories"))
            {
                PrepareEnvironmentVariables(logger);
            }

            if (!string.IsNullOrWhiteSpace(customPythonPath))
            {
                using (timingTracker.StartTiming("CustomPythonSetup", "Setting up custom Python runtime"))
                {
                    pythonHome = await SetupCustomPythonAsync(logger, cancellationToken, customPythonPath, timingTracker).ConfigureAwait(false);
                }
            }
            else
            {
                var defaultPythonPath = Path.Combine(DataRoot.Value, "python_runtime");
                using (timingTracker.StartTiming("DefaultPythonSetup", "Setting up Python runtime in writable location"))
                {
                    pythonHome = await SetupCustomPythonAsync(logger, cancellationToken, defaultPythonPath, timingTracker).ConfigureAwait(false);
                }
            }

            if (!string.IsNullOrWhiteSpace(pythonHome))
            {
                EasyOcrRuntime.RegisterPythonHome(pythonHome, logger);
                var promotedToGpu = EasyOcrRuntime.TryActivateGpuRuntime(logger);
                if (promotedToGpu)
                {
                    ResetTorchInstallState(logger);
                }
            }

        using (timingTracker.StartTiming("PipSetup", "Installing pip (not included in Runtime package)"))
        {
            await EnsurePipAsync(logger, cancellationToken, pythonHome).ConfigureAwait(false);
        }
            
            using (timingTracker.StartTiming("PythonEngine", "Initializing Python engine"))
            {
                InitializePythonEngine(logger, pythonHome);
            }

            using (timingTracker.StartTiming("ModuleInstallation", "Installing/verifying required modules"))
            {
                await EnsureRequiredModulesAsync(logger, cancellationToken, pythonHome, timingTracker).ConfigureAwait(false);
            }

            _initialized = true;
            logger?.LogInformation("Python runtime initialized successfully at {Path}.", pythonHome);
            return timingTracker;
        }
        catch (Exception ex) when (ex is not EasyOcrSharpException)
        {
            logger?.LogError(ex, "Failed to initialize Python runtime.");
            throw new EasyOcrSharpException("Failed to initialize the Python runtime.", ex);
        }
        finally
        {
            InitLock.Release();
        }
    }

    private static async Task<string?> GetEmbeddedRuntimeAsync(ILogger? logger, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var embeddedPath = RuntimeLocator.GetEmbeddedRuntimePath();
            if (string.IsNullOrEmpty(embeddedPath))
            {
                logger?.LogError("Failed to locate embedded Python runtime from NuGet cache.");
                return null;
            }
            
            if (!Directory.Exists(embeddedPath))
            {
                logger?.LogError("Embedded Python runtime path does not exist: {EmbeddedPath}", embeddedPath);
                return null;
            }

            if (!IsValidPythonRuntime(embeddedPath))
            {
                logger?.LogError("Invalid Python runtime at: {EmbeddedPath}", embeddedPath);
                return null;
            }

            logger?.LogInformation("Using Python runtime directly from NuGet cache: {EmbeddedPath}", embeddedPath);
            return embeddedPath;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void PrepareEnvironmentVariables(ILogger? logger)
    {
        Environment.SetEnvironmentVariable("PYTHONNOUSERSITE", "1");
        Environment.SetEnvironmentVariable("PYTHONUTF8", "1");
    }

    private static void InitializePythonEngine(ILogger? logger, string pythonHome)
    {
        if (PythonEngine.IsInitialized)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(pythonHome))
        {
            throw new EasyOcrSharpException("Python home directory could not be resolved.");
        }

        var pythonDll = TryResolvePythonDll(pythonHome);
        if (string.IsNullOrEmpty(pythonDll) || !File.Exists(pythonDll))
        {
            pythonDll = TryResolvePythonDllAlternative(pythonHome);
            if (string.IsNullOrEmpty(pythonDll) || !File.Exists(pythonDll))
            {
                throw new EasyOcrSharpException(
                    $"Python DLL not found in {pythonHome}. " +
                    $"The embedded Python runtime may be incomplete. " +
                    $"Expected: python311.dll (Windows) or libpython3.11.so (Linux) or libpython3.11.dylib (macOS).");
            }
        }

        Environment.SetEnvironmentVariable("PYTHONHOME", pythonHome);
        Environment.SetEnvironmentVariable("PYTHONUTF8", "1");
        Environment.SetEnvironmentVariable("PYTHONNOUSERSITE", "1");
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var torchLibDir = Path.Combine(pythonHome, "Lib", "site-packages", "torch", "lib");
            if (Directory.Exists(torchLibDir))
            {
                Environment.SetEnvironmentVariable("TORCH_LIB_DIR", torchLibDir);
            }
        }

        var pythonPaths = new List<string> { pythonHome };
        
        var pythonZip = Path.Combine(pythonHome, "python311.zip");
        if (File.Exists(pythonZip))
        {
            pythonPaths.Add(pythonZip);
            logger?.LogDebug("Found Python standard library zip: {PythonZip}", pythonZip);
        }
        
        pythonPaths.Add(Path.Combine(pythonHome, "DLLs"));
        pythonPaths.Add(Path.Combine(pythonHome, "Lib"));
        pythonPaths.Add(Path.Combine(pythonHome, "Lib", "site-packages"));

        var pythonPath = string.Join(Path.PathSeparator, pythonPaths.Where(p => Directory.Exists(p) || File.Exists(p)));
        Environment.SetEnvironmentVariable("PYTHONPATH", pythonPath);

        var libDir = Path.Combine(pythonHome, "Lib");
        if (!Directory.Exists(libDir) && !File.Exists(pythonZip))
        {
            logger?.LogWarning(
                "Python standard library not found. Expected either '{LibDir}' directory or '{PythonZip}' file. " +
                "The Python installation may be incomplete.",
                libDir, pythonZip);
        }

        Runtime.PythonDLL = pythonDll;
        logger?.LogDebug("Resolved Python DLL at {DllPath}.", pythonDll);

        AppendProcessPath(pythonHome);

        try
        {
            PythonEngine.PythonHome = pythonHome;
            PythonEngine.PythonPath = pythonPath;

            PythonEngine.Initialize();
            PythonEngine.BeginAllowThreads();
            logger?.LogDebug("Python engine initialized with home {PythonHome}.", pythonHome);
            
            using (Py.GIL())
            {
                PatchPyTorchDllLoading(pythonHome, logger);
            }
        }
        catch (TypeInitializationException ex) when (ex.InnerException is MissingMethodException mme && 
                                                      mme.Message.Contains("PyThreadState_GetUnchecked"))
        {
            throw new EasyOcrSharpException(
                $"Failed to initialize Python engine. The Python installation at '{pythonHome}' is incompatible. " +
                $"This may indicate Python 3.13 is being used, which is not supported. " +
                $"Expected Python 3.11.", ex);
        }
        catch (Exception ex)
        {
            throw new EasyOcrSharpException(
                $"Failed to initialize Python engine. " +
                $"Python DLL: {pythonDll}, Python Home: {pythonHome}. " +
                $"This may indicate a version mismatch between Python and pythonnet. " +
                $"Error: {ex.Message}", ex);
        }
    }

    private static bool IsValidPythonRuntime(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        var pythonExe = GetPythonExecutablePath(path);
        if (string.IsNullOrEmpty(pythonExe) || !File.Exists(pythonExe))
        {
            return false;
        }

        var libDir = Path.Combine(path, "Lib");
        var pythonZip = Path.Combine(path, "python311.zip");
        
        if (!Directory.Exists(libDir) && !File.Exists(pythonZip))
        {
            return false;
        }

        var sitePackages = Path.Combine(path, "Lib", "site-packages");
        if (Directory.Exists(sitePackages))
        {
            var easyocrPath = Path.Combine(sitePackages, "easyocr");
            if (!Directory.Exists(easyocrPath) && !File.Exists(Path.Combine(sitePackages, "easyocr.py")))
            {
                return false;
            }
        }

        return true;
    }

    private static string? GetPythonExecutablePath(string pythonHome)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var exePath = Path.Combine(pythonHome, "python.exe");
            if (File.Exists(exePath))
            {
                return exePath;
            }

            exePath = Path.Combine(pythonHome, "python3.exe");
            if (File.Exists(exePath))
            {
                return exePath;
            }
        }
        else
        {
            var binDir = Path.Combine(pythonHome, "bin");
            var exePath = Path.Combine(binDir, "python3");
            if (File.Exists(exePath))
            {
                return exePath;
            }

            exePath = Path.Combine(binDir, "python");
            if (File.Exists(exePath))
            {
                return exePath;
            }
        }

        return null;
    }

    private static string? TryResolvePythonDll(string pythonHome)
    {
        if (!Directory.Exists(pythonHome))
        {
            return null;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var allDlls = Directory.EnumerateFiles(pythonHome, "python*.dll", SearchOption.TopDirectoryOnly).ToList();
                
                var versionSpecificDll = allDlls
                    .Where(dll => Regex.IsMatch(Path.GetFileName(dll), @"^python3\d+\.dll$", RegexOptions.IgnoreCase))
                    .OrderByDescending(dll => Path.GetFileName(dll))
                    .FirstOrDefault();
                
                if (versionSpecificDll != null)
                {
                    return versionSpecificDll;
                }
                
                var genericDll = allDlls
                    .Where(dll => Path.GetFileName(dll).StartsWith("python3", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetCreationTimeUtc)
                    .FirstOrDefault();
                
                if (genericDll != null)
                {
                    return genericDll;
                }

                return allDlls
                    .FirstOrDefault(dll => Path.GetFileName(dll).Equals("python.dll", StringComparison.OrdinalIgnoreCase));
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return Directory.EnumerateFiles(pythonHome, "libpython3*.so*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetCreationTimeUtc)
                    .FirstOrDefault();
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return Directory.EnumerateFiles(pythonHome, "libpython3*.dylib", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetCreationTimeUtc)
                    .FirstOrDefault();
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? TryResolvePythonDllAlternative(string pythonHome)
    {
        if (!Directory.Exists(pythonHome))
        {
            return null;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var dllsDir = Path.Combine(pythonHome, "DLLs");
                if (Directory.Exists(dllsDir))
                {
                    var allDlls = Directory.EnumerateFiles(dllsDir, "python*.dll", SearchOption.TopDirectoryOnly).ToList();
                    
                    var versionSpecificDll = allDlls
                        .Where(dll => Regex.IsMatch(Path.GetFileName(dll), @"^python3\d+\.dll$", RegexOptions.IgnoreCase))
                        .OrderByDescending(dll => Path.GetFileName(dll))
                        .FirstOrDefault();
                    
                    if (versionSpecificDll != null)
                    {
                        return versionSpecificDll;
                    }
                    
                    var genericDll = allDlls
                        .Where(dll => Path.GetFileName(dll).StartsWith("python3", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(File.GetCreationTimeUtc)
                        .FirstOrDefault();
                    
                    if (genericDll != null)
                    {
                        return genericDll;
                    }
                }

                var allRecursiveDlls = Directory.EnumerateFiles(pythonHome, "python*.dll", SearchOption.AllDirectories).ToList();
                
                var recursiveVersionSpecificDll = allRecursiveDlls
                    .Where(dll => Regex.IsMatch(Path.GetFileName(dll), @"^python3\d+\.dll$", RegexOptions.IgnoreCase))
                    .OrderByDescending(dll => Path.GetFileName(dll))
                    .FirstOrDefault();
                
                if (recursiveVersionSpecificDll != null)
                {
                    return recursiveVersionSpecificDll;
                }
                
                return allRecursiveDlls
                    .Where(dll => Path.GetFileName(dll).StartsWith("python3", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetCreationTimeUtc)
                    .FirstOrDefault();
            }
        }
        catch
        {
        }

        return null;
    }

    private static void AppendProcessPath(string pythonHome)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathEntries = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!pathEntries.Any(entry => string.Equals(entry, pythonHome, StringComparison.OrdinalIgnoreCase)))
        {
            pathEntries.Insert(0, pythonHome);
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var dllsDir = Path.Combine(pythonHome, "DLLs");
            if (Directory.Exists(dllsDir) && 
                !pathEntries.Any(entry => string.Equals(entry, dllsDir, StringComparison.OrdinalIgnoreCase)))
            {
                pathEntries.Insert(0, dllsDir);
            }
            var torchLibDir = Path.Combine(pythonHome, "Lib", "site-packages", "torch", "lib");
            if (Directory.Exists(torchLibDir) && 
                !pathEntries.Any(entry => string.Equals(entry, torchLibDir, StringComparison.OrdinalIgnoreCase)))
            {
                pathEntries.Insert(0, torchLibDir);
            }
        }

        var newPath = string.Join(Path.PathSeparator.ToString(), pathEntries);
        Environment.SetEnvironmentVariable("PATH", newPath);
    }

    private static string ResolveDataRoot()
    {
        var overridePath = Environment.GetEnvironmentVariable("EASYOCRSHARP_CACHE");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return EnsureDirectory(Path.GetFullPath(overridePath));
        }

        string? basePath = null;
        try
        {
            basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(basePath))
        {
            try
            {
                basePath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            catch
            {
            }
        }

        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = AppContext.BaseDirectory;
        }

        return EnsureDirectory(Path.Combine(basePath, "EasyOcrSharp"));
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static void PatchPyTorchDllLoading(string pythonHome, ILogger? logger)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            var sitePackages = Path.Combine(pythonHome, "Lib", "site-packages");
            if (!Directory.Exists(sitePackages))
            {
                return;
            }

            var patchCode = $@"
import os
import pathlib
import sys

# Store original function
_original_add_dll_directory = os.add_dll_directory

# Base paths for resolving relative paths
_site_packages_base = r'{sitePackages.Replace("\\", "\\\\")}'
_python_home = r'{pythonHome.Replace("\\", "\\\\")}'

def _patched_add_dll_directory(path):
    '''Patch os.add_dll_directory to handle relative paths and invalid paths'''
    try:
        # Convert to Path object to handle relative paths
        path_obj = pathlib.Path(path)
        
        # If it's a relative path, try to resolve it
        if not path_obj.is_absolute():
            base_path = pathlib.Path(_site_packages_base)
            torch_dir = base_path / 'torch'
            python_home = pathlib.Path(_python_home)
            
            # Try multiple resolution strategies
            candidates = [
                torch_dir / path,  # torch/bin
                torch_dir / 'lib' / path,  # torch/lib/bin
                python_home / path,  # python_home/bin
                python_home / 'DLLs' / path,  # python_home/DLLs/bin
            ]
            
            resolved_path = None
            for candidate in candidates:
                if candidate.exists() and candidate.is_dir():
                    resolved_path = candidate
                    break
            
            if resolved_path is None:
                # If can't resolve, skip this directory silently
                return None
            
            path = str(resolved_path)
        
        # Ensure path exists and is a directory
        if not os.path.isdir(path):
            return None
            
        # Call original function with absolute path
        return _original_add_dll_directory(path)
    
    except Exception:
        # If anything fails, skip silently (don't let PyTorch fail due to DLL loading)
        return None

# Replace the function
os.add_dll_directory = _patched_add_dll_directory
";
            
            PythonEngine.Exec(patchCode);
            logger?.LogDebug("PyTorch DLL loading patch applied successfully.");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to patch PyTorch DLL loading. PyTorch may fail to load.");
        }
    }

    private static async Task EnsurePipAsync(ILogger? logger, CancellationToken cancellationToken, string pythonHome)
    {
        var pythonExe = GetPythonExecutablePath(pythonHome);
        if (string.IsNullOrWhiteSpace(pythonExe) || !File.Exists(pythonExe))
        {
            throw new EasyOcrSharpException($"Python executable not found at {pythonExe}");
        }

        var existingPip = FindExistingPipExecutable(pythonHome);
        if (!string.IsNullOrEmpty(existingPip))
        {
            logger?.LogDebug("pip already bootstrapped at {Path}. Skipping get-pip.", existingPip);
            return;
        }

        logger?.LogInformation("Bootstrapping pip using get-pip.py (pip excluded from Runtime package)...");

        var getPipPath = Path.Combine(pythonHome, "Lib", "get-pip.py");
        if (!File.Exists(getPipPath))
        {
            logger?.LogError("get-pip.py not found in the Python installation. Cannot bootstrap pip.");
            throw new EasyOcrSharpException("pip is not available and cannot be bootstrapped.");
        }

        logger?.LogInformation("Running get-pip.py to bootstrap pip manually.");

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"\"{getPipPath}\" --force-reinstall",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = pythonHome
                }
            };

            process.Start();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                logger?.LogError("get-pip.py failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
                throw new EasyOcrSharpException($"Failed to bootstrap pip. Exit code: {process.ExitCode}");
            }

            logger?.LogInformation("get-pip.py completed successfully.");
        }
        catch (Exception ex) when (ex is not EasyOcrSharpException)
        {
            logger?.LogError(ex, "Failed to run get-pip.py to bootstrap pip.");
            throw new EasyOcrSharpException("Failed to bootstrap pip.", ex);
        }
    }

    private static string? FindExistingPipExecutable(string pythonHome)
    {
        var candidates = new[]
        {
            Path.Combine(pythonHome, "Scripts", "pip.exe"),
            Path.Combine(pythonHome, "Scripts", "pip3.exe"),
            Path.Combine(pythonHome, "Scripts", "pip3.11.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<string> SetupCustomPythonAsync(ILogger? logger, CancellationToken cancellationToken, string customPythonPath, TimingTracker timingTracker)
    {
        logger?.LogInformation("Using custom Python path: {Path}.", customPythonPath);

        if (IsRuntimeMarkedIncomplete(customPythonPath))
        {
            logger?.LogWarning("Previous runtime installation was interrupted. Cleaning up {Path} before retrying.", customPythonPath);
            TryDeleteDirectory(customPythonPath, logger);
            ClearRuntimeIncompleteMarker(customPythonPath);
        }

        if (!Directory.Exists(customPythonPath) || !IsValidPythonRuntime(customPythonPath))
        {
            if (Directory.Exists(customPythonPath))
            {
                logger?.LogWarning("Existing runtime at {Path} is incomplete. Reinstalling clean copy.", customPythonPath);
                TryDeleteDirectory(customPythonPath, logger);
            }

            logger?.LogInformation("Python runtime not found at custom path {Path}. Copying optimized runtime from package...", customPythonPath);
            
            using (timingTracker.StartTiming("RuntimeCopy", "Copying optimized runtime to custom location"))
            {
                await CopyRuntimeFromPackageAtomic(logger, customPythonPath).ConfigureAwait(false);
                
                var pythonExe = GetPythonExecutablePath(customPythonPath);
                if (string.IsNullOrWhiteSpace(pythonExe) || !File.Exists(pythonExe))
                {
                    throw new EasyOcrSharpException(
                        $"Failed to setup Python runtime at the specified path: {customPythonPath}\n" +
                        $"Please check that the path is writable and you have sufficient disk space.");
                }
                
                logger?.LogInformation("Optimized runtime (including EasyOCR) copied to {Path}.", customPythonPath);
            }
        }
        else
        {
            logger?.LogInformation("Found existing Python installation at {Path}.", customPythonPath);
        }

        return customPythonPath;
    }

    private static async Task CopyRuntimeFromPackageAtomic(ILogger? logger, string targetPath)
    {
        var finalPath = Path.GetFullPath(targetPath);
        var parent = Path.GetDirectoryName(finalPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new EasyOcrSharpException($"Unable to determine parent directory for runtime path '{finalPath}'.");
        }

        var tempPath = finalPath + ".tmp";
        var markerPath = GetRuntimeIncompleteMarker(finalPath);

        Directory.CreateDirectory(parent);
        TryDeleteDirectory(tempPath, logger);
        File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));

        try
        {
            await CopyRuntimeFromPackage(logger, tempPath).ConfigureAwait(false);

            if (!IsValidPythonRuntime(tempPath))
            {
                throw new EasyOcrSharpException("Copied runtime failed validation. Aborting installation.");
            }

            TryDeleteDirectory(finalPath, logger);
            Directory.Move(tempPath, finalPath);
        }
        finally
        {
            TryDeleteDirectory(tempPath, logger);
            ClearRuntimeIncompleteMarker(finalPath);
        }
    }

    private static async Task CopyRuntimeFromPackage(ILogger? logger, string targetPath)
    {
        var embeddedPath = await GetEmbeddedRuntimeAsync(logger, CancellationToken.None).ConfigureAwait(false);
        
        if (!string.IsNullOrEmpty(embeddedPath) && Directory.Exists(embeddedPath))
        {
            var versionInfo = ExtractVersionFromPath(embeddedPath);
            var currentVersion = GetCurrentEasyOcrSharpVersion();
            
            logger?.LogInformation("Found EasyOcrSharp.Runtime {RuntimeVersion} for EasyOcrSharp {CurrentVersion}", versionInfo, currentVersion);
            logger?.LogInformation("Copying optimized runtime from NuGet package: {Source} → {Target}", embeddedPath, targetPath);
            
            await CopyDirectoryAsync(embeddedPath, targetPath, logger).ConfigureAwait(false);
            
            logger?.LogInformation("Runtime package copied successfully - EasyOCR is already included!");
            LogVersionCompatibility(logger, currentVersion, versionInfo);
        }
        else
        {
            logger?.LogWarning("Runtime package not found. Falling back to basic Python download.");
            logger?.LogInformation("Downloading basic Python runtime to {Path}. This may take a few minutes...", targetPath);
            await InstallPython(targetPath).ConfigureAwait(false);
            logger?.LogInformation("Basic Python runtime downloaded. EasyOCR will be installed separately.");
        }
    }

    private static string ExtractVersionFromPath(string runtimePath)
    {
        try
        {
            var pathParts = runtimePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var runtimeIndex = Array.FindIndex(pathParts, part => 
                string.Equals(part, "easyocrsharp.runtime", StringComparison.OrdinalIgnoreCase));
            
            if (runtimeIndex >= 0 && runtimeIndex + 1 < pathParts.Length)
            {
                return pathParts[runtimeIndex + 1];
            }
        }
        catch
        {
        }
        
        return "unknown";
    }

    private static string GetCurrentEasyOcrSharpVersion()
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
        }
        catch
        {
            return "1.0.0"; // Fallback version
        }
    }

    private static async Task CopyDirectoryAsync(string sourceDir, string targetDir, ILogger? logger)
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(targetDir);
            
            var totalFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories).Length;
            var copiedFiles = 0;
            
            logger?.LogDebug("Copying {TotalFiles} files from runtime package...", totalFiles);
            foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDir, dirPath);
                var targetDirPath = Path.Combine(targetDir, relativePath);
                Directory.CreateDirectory(targetDirPath);
            }

            foreach (var filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDir, filePath);
                var targetFilePath = Path.Combine(targetDir, relativePath);
                
                try
                {
                    var targetFileDir = Path.GetDirectoryName(targetFilePath);
                    if (!string.IsNullOrEmpty(targetFileDir))
                    {
                        Directory.CreateDirectory(targetFileDir);
                    }
                    
                    File.Copy(filePath, targetFilePath, overwrite: true);
                    copiedFiles++;
                    
                    if (copiedFiles % 1000 == 0)
                    {
                        logger?.LogDebug("Copied {CopiedFiles}/{TotalFiles} files...", copiedFiles, totalFiles);
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to copy file {FilePath} to {TargetPath}", filePath, targetFilePath);
                }
            }
            
            logger?.LogDebug("Successfully copied {CopiedFiles}/{TotalFiles} files.", copiedFiles, totalFiles);
            
            var pipVendorPath = Path.Combine(targetDir, "Lib", "site-packages", "pip", "_vendor");
            var easyocrPath = Path.Combine(targetDir, "Lib", "site-packages", "easyocr");
            
            if (!Directory.Exists(pipVendorPath))
            {
                logger?.LogWarning("pip._vendor directory missing after copy. pip may not function correctly.");
            }
            else
            {
                logger?.LogDebug("pip._vendor directory verified.");
            }
            
            if (!Directory.Exists(easyocrPath))
            {
                logger?.LogWarning("easyocr directory missing after copy. EasyOCR may not be available.");
            }
            else
            {
                logger?.LogDebug("easyocr directory verified.");
            }
        }).ConfigureAwait(false);
    }

    private static async Task EnsureRequiredModulesAsync(ILogger? logger, CancellationToken cancellationToken, string pythonHome, TimingTracker? timingTracker = null)
    {
        SyncTorchPreference(logger);

        var sitePackages = Path.Combine(pythonHome, "Lib", "site-packages");
        Directory.CreateDirectory(sitePackages);

        var easyocrPath = Path.Combine(sitePackages, "easyocr");
        var hasEasyOcr = Directory.Exists(easyocrPath);
        
        var modulesToInstall = RequiredModules.ToList();
        if (!hasEasyOcr)
        {
            modulesToInstall.Insert(0, "easyocr");
            logger?.LogInformation("EasyOCR not found - will be downloaded along with PyTorch components.");
        }
        else
        {
            logger?.LogInformation("EasyOCR found (copied from Runtime package) - installing PyTorch components only.");
        }


        foreach (var module in modulesToInstall)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (InstalledModules.ContainsKey(module))
            {
                continue;
            }

            using (timingTracker?.StartTiming($"Module_{module}", $"Verifying/Installing {module}"))
            {
                bool isActuallyInstalled = false;
                var moduleArtifactsPresent = ModuleArtifactsExist(sitePackages, module);

                try
                {
                    using var gil = Py.GIL();
                    try
                    {
                        Py.Import(module);
                        isActuallyInstalled = true;
                        logger?.LogDebug("Python module '{Module}' is already installed and importable.", module);
                    }
                    catch
                    {
                        isActuallyInstalled = moduleArtifactsPresent;
                        if (moduleArtifactsPresent)
                        {
                            logger?.LogDebug("Module '{Module}' import failed but artifacts exist. Skipping re-download.", module);
                        }
                    }
                }
                catch
                {
                    isActuallyInstalled = moduleArtifactsPresent;
                }

                if (isActuallyInstalled)
                {
                    InstalledModules[module] = true;
                    continue;
                }

                var moduleSpec = GetModuleInstallationSpec(module, logger);
                logger?.LogInformation("Installing {ModuleSpec}. This may take a few minutes on first run.", moduleSpec.DisplayName);

                try
                {
                    var pythonExe = GetPythonExecutablePath(pythonHome);
                    if (string.IsNullOrWhiteSpace(pythonExe) || !File.Exists(pythonExe))
                    {
                        throw new EasyOcrSharpException($"Python executable not found at {pythonExe}");
                    }

                    var pipPath = Path.Combine(pythonHome, "Scripts", "pip.exe");
                    if (!File.Exists(pipPath))
                    {
                        pipPath = Path.Combine(pythonHome, "Scripts", "pip3.exe");
                    }
                    
                    using (timingTracker?.StartTiming($"Download_{module}", $"Downloading and installing {moduleSpec.DisplayName}", isSubComponent: true))
                    {
                        if (!File.Exists(pipPath))
                        {
                            await InstallModuleWithPipAsync(pythonExe, moduleSpec, sitePackages, pythonHome, logger, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await InstallModuleWithPipAsync(pipPath, moduleSpec, sitePackages, pythonHome, logger, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    if (moduleSpec.ModuleName.Equals("torch", StringComparison.OrdinalIgnoreCase))
                    {
                        InstalledModules["torch"] = true;
                        InstalledModules["torchvision"] = true;
                        InstalledModules["torchaudio"] = true;
                        
                        
                        logger?.LogInformation("PyTorch bundle (torch + torchvision + torchaudio) installed successfully.");
                    }
                    else
                    {
                        InstalledModules[moduleSpec.ModuleName] = true;
                        
                        
                        logger?.LogInformation("Python module '{Module}' installed successfully.", moduleSpec.DisplayName);
                    }
                }
                catch (Exception ex)
                {
                    throw new EasyOcrSharpException($"Failed to install required Python module '{moduleSpec.DisplayName}'.", ex);
                }
            }
        }

        using (timingTracker?.StartTiming("CacheInvalidation", "Invalidating Python import caches"))
        {
            InvalidatePythonImportCaches(logger);
        }

    }

    private static bool IsRuntimeMarkedIncomplete(string runtimePath)
    {
        var marker = GetRuntimeIncompleteMarker(runtimePath);
        var tempPath = Path.GetFullPath(runtimePath) + ".tmp";
        return File.Exists(marker) || Directory.Exists(tempPath);
    }

    private static string GetRuntimeIncompleteMarker(string runtimePath)
    {
        var fullPath = Path.GetFullPath(runtimePath);
        var directory = Path.GetDirectoryName(fullPath);
        var runtimeName = Path.GetFileName(fullPath) ?? "python_runtime";
        var markerName = $".{runtimeName}.installing";
        return Path.Combine(directory ?? fullPath, markerName);
    }

    private static void ClearRuntimeIncompleteMarker(string runtimePath)
    {
        var marker = GetRuntimeIncompleteMarker(runtimePath);
        if (File.Exists(marker))
        {
            File.Delete(marker);
        }
    }

    private static void TryDeleteDirectory(string? path, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to delete directory '{Path}'. Manual cleanup may be required.", path);
        }
    }

    private static void SyncTorchPreference(ILogger? logger)
    {
        var status = EasyOcrRuntime.CurrentStatus;
        _recommendedTorchVersion = GpuRuntimeManager.GetRecommendedTorchCommand();
        _gpuAvailable = status.GpuAvailable && !status.IsRunningOnCpu;

        if (_lastRuntimeStatus is null || !_lastRuntimeStatus.Value.Equals(status))
        {
            if (!status.GpuAvailable)
            {
                logger?.LogInformation("GPU runtime disabled: no CUDA-capable GPU detected.");
            }
            else if (!status.RuntimeDownloaded)
            {
                logger?.LogInformation("CUDA GPU detected. GPU PyTorch runtime download running in background.");
            }
            else if (!status.GpuReady)
            {
                logger?.LogInformation("GPU runtime payload downloaded. Activation will occur automatically on the next OCR call.");
            }
            else if (status.IsRunningOnCpu)
            {
                logger?.LogInformation("GPU runtime ready but OCR service is still running on CPU until the next initialization cycle.");
            }
            else
            {
                logger?.LogInformation("GPU runtime active. PyTorch will migrate to CUDA packages.");
            }
        }

        _lastRuntimeStatus = status;
    }

    private static void ResetTorchInstallState(ILogger? logger)
    {
        foreach (var module in new[] { "torch", "torchvision", "torchaudio" })
        {
            InstalledModules.TryRemove(module, out _);
        }

        logger?.LogInformation("GPU runtime ready. PyTorch modules will be reinstalled with CUDA support.");
    }

    private record ModuleInstallationSpec(string ModuleName, string InstallCommand, string DisplayName);

    private static ModuleInstallationSpec GetModuleInstallationSpec(string module, ILogger? logger)
    {
        return module.ToLowerInvariant() switch
        {
            "torch" => new ModuleInstallationSpec("torch", _recommendedTorchVersion ?? "torch", 
                _gpuAvailable == true ? "PyTorch (CUDA GPU version)" : "PyTorch (CPU version)"),
            "torchvision" => new ModuleInstallationSpec("torchvision", "", "torchvision"), // Installed with torch
            "torchaudio" => new ModuleInstallationSpec("torchaudio", "", "torchaudio"),   // Installed with torch  
            "pillow" => new ModuleInstallationSpec("Pillow", "Pillow", "Pillow (PIL)"),
            _ => new ModuleInstallationSpec(module, module, module)
        };
    }

    private static async Task InstallModuleWithPipAsync(string executablePath, ModuleInstallationSpec moduleSpec, string sitePackages, string pythonHome, ILogger? logger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(moduleSpec.InstallCommand))
        {
            logger?.LogDebug("Skipping {ModuleName} - installed as part of PyTorch bundle", moduleSpec.ModuleName);
            return;
        }

        var args = new List<string>();
        
        if (executablePath.EndsWith("pip.exe", StringComparison.OrdinalIgnoreCase) || executablePath.EndsWith("pip3.exe", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("install");
        }
        else
        {
            args.AddRange(["-m", "pip", "install"]);
        }

        if (moduleSpec.InstallCommand.Contains("--index-url") || moduleSpec.InstallCommand.Contains("--trusted-host"))
        {
            args.AddRange(moduleSpec.InstallCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        else
        {
            args.Add(moduleSpec.InstallCommand);
        }

        args.AddRange([
            "--no-cache-dir",
            "--disable-pip-version-check",
            "--target", sitePackages
        ]);

        var commandPreview = string.Join(" ", args.Take(8)) + (args.Count > 8 ? "..." : "");
        logger?.LogDebug("pip command: {Command}", commandPreview);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = string.Join(" ", args.Select(arg => arg.Contains(' ') ? $"\"{arg}\"" : arg)),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = pythonHome
                }
            };

            var output = new List<string>();
            var errors = new List<string>();

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    output.Add(e.Data);
                    logger?.LogDebug("[pip stdout] {Data}", e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errors.Add(e.Data);
                    logger?.LogDebug("[pip stderr] {Data}", e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var allErrors = string.Join(Environment.NewLine, errors);
                logger?.LogError("pip install failed for module '{Module}' with exit code {ExitCode}. Error output: {Errors}", moduleSpec.DisplayName, process.ExitCode, allErrors);
                throw new EasyOcrSharpException($"pip install failed for module '{moduleSpec.DisplayName}' with exit code {process.ExitCode}.");
            }
        }
        catch (Exception ex) when (ex is not EasyOcrSharpException)
        {
            logger?.LogError(ex, "Failed to execute pip install for module '{Module}'.", moduleSpec.DisplayName);
            throw new EasyOcrSharpException($"Failed to execute pip install for module '{moduleSpec.DisplayName}'.", ex);
        }
    }

    private static void InvalidatePythonImportCaches(ILogger? logger)
    {
        try
        {
            using var gil = Py.GIL();
            PythonEngine.Exec(@"
import sys
import importlib
if hasattr(importlib, 'invalidate_caches'):
    importlib.invalidate_caches()
if hasattr(sys, 'path_importer_cache'):
    sys.path_importer_cache.clear()
");
            logger?.LogDebug("Python import caches invalidated successfully.");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to invalidate Python import caches. This may cause import issues with newly installed modules.");
        }
    }

    private static async Task InstallPython(string targetPath)
    {
        await Task.Run(() =>
        {
            Installer.InstallPath = targetPath;
            Installer.SetupPython(true);
        }).ConfigureAwait(false);
    }

    private static bool ModuleArtifactsExist(string sitePackages, string module)
    {
        static string NormalizeFolderName(string moduleName) => moduleName.ToLowerInvariant() switch
        {
            "pillow" => "PIL",
            _ => moduleName
        };

        var normalized = NormalizeFolderName(module);
        var candidateDirs = new[]
        {
            Path.Combine(sitePackages, normalized),
            Path.Combine(sitePackages, module)
        };

        if (candidateDirs.Any(Directory.Exists))
        {
            return true;
        }

        var candidateFiles = new[]
        {
            Path.Combine(sitePackages, $"{module}.py"),
            Path.Combine(sitePackages, $"{module}.pyi"),
            Path.Combine(sitePackages, $"{module}.pth")
        };

        if (candidateFiles.Any(File.Exists))
        {
            return true;
        }

        // Handle wheel metadata directories (e.g., torch-2.1.0.dist-info)
        var distInfoPattern = $"{module.ToLowerInvariant()}-*.dist-info";
        try
        {
            var distInfo = Directory.EnumerateDirectories(sitePackages, distInfoPattern, SearchOption.TopDirectoryOnly);
            if (distInfo.Any())
            {
                return true;
            }
        }
        catch
        {
            // Ignore IO errors and treat as missing artifacts
        }

        return false;
    }

    private static void LogVersionCompatibility(ILogger? logger, string currentVersion, string runtimeVersion)
    {
        if (string.Equals(currentVersion, runtimeVersion, StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogInformation("Perfect match! EasyOcrSharp {CurrentVersion} ↔ Runtime {RuntimeVersion}", currentVersion, runtimeVersion);
        }
        else if (Version.TryParse(currentVersion, out var currentVer) && Version.TryParse(runtimeVersion, out var runtimeVer))
        {
            if (currentVer.Major == runtimeVer.Major && currentVer.Minor == runtimeVer.Minor)
            {
                if (runtimeVer.Build > currentVer.Build)
                {
                    logger?.LogInformation("⬆️  Using newer compatible runtime: EasyOcrSharp {CurrentVersion} ↔ Runtime {RuntimeVersion} (patch version higher)", currentVersion, runtimeVersion);
                }
                else if (runtimeVer.Build < currentVer.Build)
                {
                    logger?.LogInformation("⬇️  Using older compatible runtime: EasyOcrSharp {CurrentVersion} ↔ Runtime {RuntimeVersion} (patch version lower)", currentVersion, runtimeVersion);
                }
                else
                {
                    logger?.LogInformation("Compatible versions: EasyOcrSharp {CurrentVersion} ↔ Runtime {RuntimeVersion}", currentVersion, runtimeVersion);
                }
            }
            else
            {
                logger?.LogWarning("⚠️  Version mismatch detected: EasyOcrSharp {CurrentVersion} ↔ Runtime {RuntimeVersion} (different major/minor versions)", currentVersion, runtimeVersion);
                logger?.LogWarning("📋 Consider updating to matching versions for optimal compatibility.");
            }
        }
        else
        {
            logger?.LogInformation("🔗 Version pairing: EasyOcrSharp {CurrentVersion} ↔ Runtime {RuntimeVersion}", currentVersion, runtimeVersion);
        }
    }


}
