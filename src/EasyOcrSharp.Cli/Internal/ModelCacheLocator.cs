namespace EasyOcrSharp.Cli.Internal;

/// <summary>
/// Resolves the directory the library caches ONNX models in. The library's own resolver is internal,
/// so the CLI reproduces its documented contract: an explicit path wins, then the
/// <c>EASYOCRSHARP_CACHE</c> environment variable, then a per-user folder under local application
/// data. Keeping this in one place means <c>models path</c>, <c>models list</c> and <c>info</c> all
/// report the same directory the OCR commands actually use.
/// </summary>
internal static class ModelCacheLocator
{
    /// <summary>The environment variable that overrides the default cache location.</summary>
    public const string CacheEnvironmentVariable = "EASYOCRSHARP_CACHE";

    /// <summary>Resolves the effective cache directory for an optional <c>--cache</c> override.</summary>
    public static string Resolve(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(CacheEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyOcrSharp",
            "models");
    }

    /// <summary>
    /// The model files currently in the cache, sorted by name. Returns an empty list when the cache
    /// directory does not exist yet (nothing has been downloaded) rather than throwing.
    /// </summary>
    public static IReadOnlyList<FileInfo> CachedFiles(string cacheRoot)
    {
        if (!Directory.Exists(cacheRoot)) return [];

        var files = new DirectoryInfo(cacheRoot)
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(f => !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                     && !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .ToList();

        files.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return files;
    }
}
