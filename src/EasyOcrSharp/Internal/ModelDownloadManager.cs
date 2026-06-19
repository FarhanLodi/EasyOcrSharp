using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using EasyOcrSharp.Diagnostics;
using EasyOcrSharp.Services;
using Microsoft.Extensions.Logging;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Resolves the local on-disk path for an ONNX model asset, downloading from the
/// configured base URL if not already cached. Downloads are atomic (write to .part, rename),
/// resumable (HTTP range), retried with exponential backoff on transient failures, and
/// SHA256-verified when the registry supplies a checksum.
/// </summary>
internal static class ModelDownloadManager
{
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static readonly HttpClient SharedHttp = CreateHttpClient();
    private static string? _modelCacheRoot;

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EasyOcrSharp/2.2");
        return client;
    }

    /// <summary>
    /// Returns the absolute path to a cached copy of <paramref name="asset"/>, downloading it
    /// if not already present. Safe for concurrent callers — only one download runs at a time.
    /// </summary>
    public static async Task<string> EnsureModelAsync(
        ModelAsset asset,
        string? customCachePath,
        ModelDownloadOptions options,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var cacheDir = GetModelCachePath(customCachePath);
        Directory.CreateDirectory(cacheDir);

        // The asset's file name is concatenated into the cache directory; require it to be a single path
        // segment so a registry/mirror name can never traverse out of the cache (Zip-Slip-style write).
        if (asset.FileName.Length == 0 ||
            !string.Equals(Path.GetFileName(asset.FileName), asset.FileName, StringComparison.Ordinal))
        {
            throw new ModelDownloadException($"Refusing to cache model with an unexpected file name '{asset.FileName}'.");
        }

        var finalPath = Path.Combine(cacheDir, asset.FileName);
        if (File.Exists(finalPath))
        {
            return finalPath;
        }

        if (options.Offline)
        {
            throw new OfflineModelMissingException(
                $"Model '{asset.FileName}' is not present in the cache ('{cacheDir}') and offline mode is enabled. " +
                "Pre-seed the cache with the required .onnx/.vocab.json files, or disable ModelDownloadOptions.Offline.");
        }

        await CacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(finalPath))
            {
                return finalPath;
            }

            await DownloadWithRetryAsync(asset, finalPath, options, logger, cancellationToken).ConfigureAwait(false);
            logger?.LogInformation("Model {Name} cached at {Path}", asset.FileName, finalPath);
            return finalPath;
        }
        finally
        {
            CacheLock.Release();
        }
    }

    private static async Task DownloadWithRetryAsync(
        ModelAsset asset, string finalPath, ModelDownloadOptions options, ILogger? logger, CancellationToken ct)
    {
        var url = ResolveUrl(asset, options);
        var tempPath = finalPath + ".part";
        int maxAttempts = Math.Max(1, options.MaxRetries + 1);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, asset, tempPath, options, logger, ct).ConfigureAwait(false);
                await VerifyChecksumAsync(asset, tempPath, options, ct).ConfigureAwait(false);
                File.Move(tempPath, finalPath, overwrite: false);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMilliseconds(options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                logger?.LogWarning(ex, "Download of {Name} failed (attempt {Attempt}/{Max}); retrying in {Delay:0.0}s.",
                    asset.FileName, attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task DownloadOnceAsync(
        string url, ModelAsset asset, string tempPath, ModelDownloadOptions options, ILogger? logger, CancellationToken ct)
    {
        var http = options.HttpClientFactory?.Invoke() ?? SharedHttp;

        // Resume a partial download where possible (the model repo supports HTTP range requests).
        long existing = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        bool resuming = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (existing > 0 && !resuming)
        {
            // Server ignored the range (sent full 200) — restart cleanly.
            existing = 0;
            File.Delete(tempPath);
        }
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength is { } len ? len + existing : -1L;
        long downloaded = existing;
        logger?.LogInformation("Downloading model {Name} from {Url}{Resume}",
            asset.FileName, url, resuming ? $" (resuming at {existing:N0} bytes)" : string.Empty);

        var fileMode = resuming ? FileMode.Append : FileMode.Create;
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = new FileStream(tempPath, fileMode, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            var lastReport = DateTime.UtcNow;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                downloaded += read;
                EasyOcrDiagnostics.ModelDownloadBytes.Add(read);

                if ((DateTime.UtcNow - lastReport).TotalSeconds >= 1)
                {
                    Report(logger, options, asset.FileName, downloaded, total);
                    lastReport = DateTime.UtcNow;
                }
            }
        }

        Report(logger, options, asset.FileName, downloaded, total);
    }

    private static async Task VerifyChecksumAsync(ModelAsset asset, string tempPath, ModelDownloadOptions options, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(asset.Sha256))
        {
            // Fail closed: a downloaded model parsed by native ONNX Runtime must be integrity-verified
            // unless the caller has explicitly opted into unverified assets from a trusted mirror.
            if (options.AllowUnverifiedModels) return;
            File.Delete(tempPath);
            throw new ModelChecksumException(
                $"Downloaded model '{asset.FileName}' has no known SHA256 checksum to verify against. " +
                "Set ModelDownloadOptions.AllowUnverifiedModels = true to allow unverified models from a trusted source.");
        }

        var actual = await ComputeSha256Async(tempPath, ct).ConfigureAwait(false);
        if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tempPath);
            throw new ModelChecksumException(
                $"Downloaded model '{asset.FileName}' failed SHA256 verification. Expected {asset.Sha256}, got {actual}.");
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        EasyOcrSharpException => false,                 // checksum mismatch — retrying won't help
        HttpRequestException => true,
        IOException => true,
        TaskCanceledException => true,                  // request timeout (user cancel filtered by caller)
        _ => false,
    };

    private static void Report(ILogger? logger, ModelDownloadOptions options, string name, long downloaded, long total)
    {
        options.Progress?.Report(new ModelDownloadProgress(name, downloaded, total));
        if (logger is null) return;
        if (total > 0)
            logger.LogInformation("  {Name}: {Downloaded:N0} / {Total:N0} bytes ({Pct:F1}%)", name, downloaded, total, downloaded * 100.0 / total);
        else
            logger.LogInformation("  {Name}: {Downloaded:N0} bytes", name, downloaded);
    }

    private static string ResolveUrl(ModelAsset asset, ModelDownloadOptions options)
    {
        var baseUrl = options.BaseUrlOverride;
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = Environment.GetEnvironmentVariable("EASYOCRSHARP_MODEL_BASE_URL");

        if (string.IsNullOrWhiteSpace(baseUrl))
            return asset.Url; // built-in host is https.

        var url = $"{baseUrl.TrimEnd('/')}/{asset.FileName}";
        EnsureSecureSource(url, options);
        return url;
    }

    /// <summary>
    /// Rejects a non-HTTPS model source unless the caller has explicitly opted in. The download is the
    /// supply-chain trust root, so a cleartext override (which an attacker on the path or who can set an
    /// env var could influence) is refused by default.
    /// </summary>
    private static void EnsureSecureSource(string url, ModelDownloadOptions options)
    {
        if (options.AllowInsecureModelSource) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelDownloadException(
                $"Refusing to download a model from a non-HTTPS source '{url}'. Use an https:// mirror, or set " +
                "ModelDownloadOptions.AllowInsecureModelSource = true to override for a trusted on-host mirror.");
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
        // An explicit cache path is always honored verbatim and never mutates the shared default,
        // so services configured with different cache paths don't interfere with one another.
        if (!string.IsNullOrWhiteSpace(customCachePath))
        {
            return Path.GetFullPath(customCachePath);
        }

        return _modelCacheRoot ??= ResolveDefaultCacheRoot();
    }

    private static string ResolveDefaultCacheRoot()
    {
        var envOverride = Environment.GetEnvironmentVariable("EASYOCRSHARP_CACHE");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            return Path.GetFullPath(envOverride);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyOcrSharp",
            "models");
    }

    /// <summary>Resolves the effective model cache directory for the given (optional) override.</summary>
    public static string ResolveCacheRoot(string? customCachePath) => GetModelCachePath(customCachePath);

    public static string ModelCacheRootPath => GetModelCachePath(null);
}
