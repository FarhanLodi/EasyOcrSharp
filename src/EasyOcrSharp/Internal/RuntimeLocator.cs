using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Locates the embedded Python runtime from the EasyOcrSharp.Runtime NuGet package.
/// </summary>
internal static class RuntimeLocator
{
    private static string? _cachedRuntimePath;

    /// <summary>
    /// Gets the path to the embedded Python runtime from the EasyOcrSharp.Runtime package.
    /// </summary>
    /// <returns>The path to the Python runtime directory, or null if not found.</returns>
    internal static string? GetEmbeddedRuntimePath()
    {
        if (_cachedRuntimePath != null)
        {
            return _cachedRuntimePath;
        }

        // First, try NuGet cache location (production)
        var nugetCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var packageDir = Path.Combine(nugetCache, "easyocrsharp.runtime");
        
        if (Directory.Exists(packageDir))
        {
            // Find the version directory (e.g., "1.0.0")
            var versionDirs = Directory.GetDirectories(packageDir);
            foreach (var versionDir in versionDirs)
            {
                var toolsPath = Path.Combine(versionDir, "tools", "python_runtime");
                if (Directory.Exists(toolsPath) && IsValidPythonRuntime(toolsPath))
                {
                    _cachedRuntimePath = Path.GetFullPath(toolsPath);
                    return _cachedRuntimePath;
                }
            }
        }

        // Second, try development source directory
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrEmpty(assemblyLocation))
        {
            assemblyLocation = AppContext.BaseDirectory;
        }

        var baseDir = Path.GetDirectoryName(assemblyLocation);
        if (!string.IsNullOrEmpty(baseDir))
        {
            // Try to find solution root by looking for .sln file
            var currentDir = baseDir;
            for (int i = 0; i < 5 && currentDir != null; i++)
            {
                var slnFiles = Directory.GetFiles(currentDir, "*.sln");
                if (slnFiles.Length > 0)
                {
                    var devPath = Path.Combine(currentDir, "src", "EasyOcrSharp.Runtime", "tools", "python_runtime");
                    if (Directory.Exists(devPath) && IsValidPythonRuntime(devPath))
                    {
                        _cachedRuntimePath = Path.GetFullPath(devPath);
                        return _cachedRuntimePath;
                    }
                    break;
                }
                currentDir = Directory.GetParent(currentDir)?.FullName;
            }
        }

        return null;
    }

    /// <summary>
    /// Validates that the directory contains a valid Python runtime.
    /// </summary>
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
        
        return Directory.Exists(libDir) || File.Exists(pythonZip);
    }

    /// <summary>
    /// Gets the Python executable path for the given runtime directory.
    /// </summary>
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

}

