using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Python.Runtime;

namespace EasyOcrSharp.Internal;

internal static class PythonInitializer
{
    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static readonly Lazy<string> DataRoot = new(ResolveDataRoot, LazyThreadSafetyMode.ExecutionAndPublication);

    private static bool _initialized;

    internal static string CacheRoot => DataRoot.Value;

    internal static async Task EnsureInitializedAsync(ILogger? logger, CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await InitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var embeddedPath = await GetEmbeddedRuntimeAsync(logger, cancellationToken).ConfigureAwait(false);
            
            if (string.IsNullOrEmpty(embeddedPath))
            {
                throw new EasyOcrSharpException(
                    "Failed to locate embedded Python runtime from EasyOcrSharp.Runtime package. " +
                    $"Expected location: {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\\.nuget\\packages\\easyocrsharp.runtime\\<version>\\tools\\python_runtime. " +
                    "Ensure EasyOcrSharp.Runtime package is installed as a NuGet dependency.");
            }
            
            PrepareEnvironmentVariables(logger);
            InitializePythonEngine(logger, embeddedPath);

            _initialized = true;
            logger?.LogInformation("Python runtime initialized successfully at {Path}.", embeddedPath);
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
        
        // Set PyTorch DLL paths to help with DLL loading
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
            
            // Patch PyTorch DLL loading early to prevent errors
            // Use GIL to safely execute Python code
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

        // Check for Python executable
        var pythonExe = GetPythonExecutablePath(path);
        if (string.IsNullOrEmpty(pythonExe) || !File.Exists(pythonExe))
        {
            return false;
        }

        // Check for Lib directory or python zip
        var libDir = Path.Combine(path, "Lib");
        var pythonZip = Path.Combine(path, "python311.zip");
        
        if (!Directory.Exists(libDir) && !File.Exists(pythonZip))
        {
            return false;
        }

        // Check for site-packages with required modules
        var sitePackages = Path.Combine(path, "Lib", "site-packages");
        if (Directory.Exists(sitePackages))
        {
            // Verify key packages exist (at least easyocr should be there)
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
        
        // Add Python home directory
        if (!pathEntries.Any(entry => string.Equals(entry, pythonHome, StringComparison.OrdinalIgnoreCase)))
        {
            pathEntries.Insert(0, pythonHome);
        }
        
        // Add DLLs directory (Windows)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var dllsDir = Path.Combine(pythonHome, "DLLs");
            if (Directory.Exists(dllsDir) && 
                !pathEntries.Any(entry => string.Equals(entry, dllsDir, StringComparison.OrdinalIgnoreCase)))
            {
                pathEntries.Insert(0, dllsDir);
            }
            
            // Add PyTorch lib directory if it exists
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

        // Note: This method should be called with GIL already acquired
        try
        {
            var sitePackages = Path.Combine(pythonHome, "Lib", "site-packages");
            if (!Directory.Exists(sitePackages))
            {
                return;
            }

            // Execute Python code to patch os.add_dll_directory and create torch.testing stub
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
            // If patching fails, log but continue
            logger?.LogWarning(ex, "Failed to patch PyTorch DLL loading. PyTorch may fail to load.");
        }
    }
}
