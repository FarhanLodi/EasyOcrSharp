using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Resolves the local on-disk path for an ONNX model asset, downloading from the
/// configured base URL if not already cached. Downloads are atomic (write to .part, rename)
/// and SHA256-verified when the registry supplies a checksum.
/// </summary>
internal static class ModelDownloadManager
{
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static readonly HttpClient Http = CreateHttpClient();
    private static string? _modelCacheRoot;

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromMinutes(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EasyOcrSharp/2.0");
        return client;
    }

    /// <summary>
    /// Returns the absolute path to a cached copy of <paramref name="asset"/>, downloading it
    /// if not already present. Safe for concurrent callers — only one download per file runs at a time.
    /// </summary>
    public static async Task<string> EnsureModelAsync(
        ModelAsset asset,
        string? customCachePath,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var cacheDir = GetModelCachePath(customCachePath);
        Directory.CreateDirectory(cacheDir);

        var finalPath = Path.Combine(cacheDir, asset.FileName);

        if (File.Exists(finalPath))
        {
            return finalPath;
        }

        await CacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(finalPath))
            {
                return finalPath;
            }

            var url = ResolveUrl(asset);
            logger?.LogInformation("Downloading model {Name} from {Url}", asset.FileName, url);

            var tempPath = finalPath + ".part";
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? -1L;
                long downloaded = 0;
                var lastReport = DateTime.UtcNow;

                await using var http = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                int read;
                while ((read = await http.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    downloaded += read;

                    if (logger is not null && (DateTime.UtcNow - lastReport).TotalSeconds >= 2)
                    {
                        ReportProgress(logger, asset.FileName, downloaded, total);
                        lastReport = DateTime.UtcNow;
                    }
                }

                if (logger is not null) ReportProgress(logger, asset.FileName, downloaded, total);
            }

            if (!string.IsNullOrEmpty(asset.Sha256))
            {
                var actual = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tempPath);
                    throw new EasyOcrSharpException(
                        $"Downloaded model '{asset.FileName}' failed SHA256 verification. Expected {asset.Sha256}, got {actual}.");
                }
            }

            File.Move(tempPath, finalPath, overwrite: false);
            logger?.LogInformation("Model {Name} cached at {Path}", asset.FileName, finalPath);
            return finalPath;
        }
        finally
        {
            CacheLock.Release();
        }
    }

    private static string ResolveUrl(ModelAsset asset)
    {
        var baseOverride = Environment.GetEnvironmentVariable("EASYOCRSHARP_MODEL_BASE_URL");
        if (!string.IsNullOrWhiteSpace(baseOverride))
        {
            return $"{baseOverride.TrimEnd('/')}/{asset.FileName}";
        }
        return asset.Url;
    }

    private static void ReportProgress(ILogger logger, string name, long downloaded, long total)
    {
        if (total > 0)
        {
            var pct = downloaded * 100.0 / total;
            logger.LogInformation("  {Name}: {Downloaded:N0} / {Total:N0} bytes ({Pct:F1}%)", name, downloaded, total, pct);
        }
        else
        {
            logger.LogInformation("  {Name}: {Downloaded:N0} bytes", name, downloaded);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string GetModelCachePath(string? customCachePath)
    {
        if (!string.IsNullOrWhiteSpace(customCachePath))
        {
            _modelCacheRoot = Path.GetFullPath(customCachePath);
            return _modelCacheRoot;
        }

        if (_modelCacheRoot is not null)
        {
            return _modelCacheRoot;
        }

        var envOverride = Environment.GetEnvironmentVariable("EASYOCRSHARP_CACHE");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            _modelCacheRoot = Path.GetFullPath(envOverride);
            return _modelCacheRoot;
        }

        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyOcrSharp",
            "models");
        _modelCacheRoot = defaultPath;
        return _modelCacheRoot;
    }

    public static string ModelCacheRootPath => GetModelCachePath(null);
}
