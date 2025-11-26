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
    /// Attempts to find a version-compatible runtime package first.
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
            var currentVersion = GetCurrentEasyOcrSharpVersion();
            var runtimePath = FindVersionCompatibleRuntime(packageDir, currentVersion);
            
            if (!string.IsNullOrEmpty(runtimePath))
            {
                _cachedRuntimePath = runtimePath;
                return _cachedRuntimePath;
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
    /// Gets the current version of the EasyOcrSharp assembly.
    /// </summary>
    private static string GetCurrentEasyOcrSharpVersion()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
        }
        catch
        {
            return "1.0.0"; // Fallback version
        }
    }

    /// <summary>
    /// Finds the best version-compatible runtime from available packages.
    /// Priority: 1) Exact match, 2) Compatible version (same major.minor), 3) Latest available
    /// </summary>
    private static string? FindVersionCompatibleRuntime(string packageDir, string currentVersion)
    {
        if (!Directory.Exists(packageDir))
            return null;

        var versionDirs = Directory.GetDirectories(packageDir)
            .Select(dir => new { Path = dir, Version = Path.GetFileName(dir) })
            .Where(item => Version.TryParse(item.Version, out _))
            .OrderByDescending(item => Version.Parse(item.Version))
            .ToList();

        if (!versionDirs.Any())
            return null;

        // Try to find exact version match first
        var exactMatch = versionDirs.FirstOrDefault(item => 
            string.Equals(item.Version, currentVersion, StringComparison.OrdinalIgnoreCase));
        
        if (exactMatch != null)
        {
            var exactPath = Path.Combine(exactMatch.Path, "tools", "python_runtime");
            if (Directory.Exists(exactPath) && IsValidPythonRuntime(exactPath))
            {
                return Path.GetFullPath(exactPath);
            }
        }

        // Try to find compatible version (same major.minor, higher patch)
        if (Version.TryParse(currentVersion, out var currentVer))
        {
            var compatibleMatch = versionDirs.FirstOrDefault(item =>
            {
                if (Version.TryParse(item.Version, out var itemVer))
                {
                    // Same major.minor version, patch can be equal or higher
                    return itemVer.Major == currentVer.Major && 
                           itemVer.Minor == currentVer.Minor &&
                           itemVer.Build >= currentVer.Build;
                }
                return false;
            });

            if (compatibleMatch != null)
            {
                var compatiblePath = Path.Combine(compatibleMatch.Path, "tools", "python_runtime");
                if (Directory.Exists(compatiblePath) && IsValidPythonRuntime(compatiblePath))
                {
                    return Path.GetFullPath(compatiblePath);
                }
            }
        }

        // Fallback: use the latest available version
        var latestMatch = versionDirs.FirstOrDefault();
        if (latestMatch != null)
        {
            var latestPath = Path.Combine(latestMatch.Path, "tools", "python_runtime");
            if (Directory.Exists(latestPath) && IsValidPythonRuntime(latestPath))
            {
                return Path.GetFullPath(latestPath);
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

