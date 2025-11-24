using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Manages on-demand download and caching of EasyOCR language models.
/// Models are downloaded only when needed and cached permanently.
/// </summary>
internal static class ModelDownloadManager
{
    private static readonly SemaphoreSlim DownloadLock = new(1, 1);
    private static string? _modelCacheRoot;

    /// <summary>
    /// Ensures that the required language models are available in the cache.
    /// Downloads them on-demand if not present.
    /// </summary>
    /// <param name="languages">The language codes that require models.</param>
    /// <param name="customCachePath">Optional custom path for model cache. If null, uses LocalAppData\EasyOcrSharp\models.</param>
    /// <param name="logger">Optional logger for diagnostic messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when models are ready.</returns>
    internal static async Task EnsureModelsAvailableAsync(
        string[] languages,
        string? customCachePath,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        if (languages == null || languages.Length == 0)
        {
            return;
        }

        await DownloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cacheDir = GetModelCachePath(customCachePath);
            Directory.CreateDirectory(cacheDir);

            Environment.SetEnvironmentVariable("EASYOCR_CACHE_DIR", cacheDir);
            Environment.SetEnvironmentVariable("EASYOCR_MODULE_PATH", cacheDir);

            logger?.LogInformation("Model cache directory set to: {CacheDir}", cacheDir);
            logger?.LogInformation(
                "Models for languages [{Languages}] will be downloaded on first use if not already cached.",
                string.Join(", ", languages));
        }
        finally
        {
            DownloadLock.Release();
        }
    }

    private static string GetModelCachePath(string? customCachePath)
    {
        if (!string.IsNullOrWhiteSpace(customCachePath))
        {
            _modelCacheRoot = Path.GetFullPath(customCachePath);
            return _modelCacheRoot;
        }

        if (_modelCacheRoot != null)
        {
            return _modelCacheRoot;
        }

        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyOcrSharp",
            "models");
        _modelCacheRoot = EnsureDirectory(defaultPath);
        return _modelCacheRoot;
    }

    internal static string ModelCacheRootPath => _modelCacheRoot ?? GetModelCachePath(null);

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}

