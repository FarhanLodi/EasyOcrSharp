using EasyOcrSharp.Internal;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EasyOcrSharp.Services;

/// <summary>
/// Health check that reports whether EasyOcrSharp can serve requests: the model cache directory is
/// accessible, and the models for the configured languages are present (so the first real request
/// won't block on a download). Register via <see cref="ServiceCollectionExtensions.AddEasyOcrHealthCheck"/>.
/// </summary>
public sealed class EasyOcrHealthCheck : IHealthCheck
{
    private readonly EasyOcrServiceOptions _options;
    private readonly string[] _languages;
    private readonly HealthStatus _failureStatus;

    /// <summary>Creates a health check for the given service options and expected languages.</summary>
    public EasyOcrHealthCheck(EasyOcrServiceOptions options, IEnumerable<string>? languages = null, HealthStatus failureStatus = HealthStatus.Degraded)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _languages = languages?.ToArray() ?? Array.Empty<string>();
        _failureStatus = failureStatus;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string cacheRoot;
        try
        {
            cacheRoot = ModelDownloadManager.ResolveCacheRoot(_options.ModelCachePath);
            Directory.CreateDirectory(cacheRoot);
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Model cache directory is not accessible.", ex));
        }

        var data = new Dictionary<string, object> { ["cachePath"] = cacheRoot };
        var missing = new List<string>();

        // The CRAFT detector is required for every language.
        AddIfMissing(cacheRoot, ModelRegistry.Detector.FileName, missing);

        foreach (var lang in _languages)
        {
            var def = ModelRegistry.FindByLanguage(lang);
            if (def is null)
            {
                missing.Add($"{lang} (unsupported language)");
                continue;
            }
            AddIfMissing(cacheRoot, def.Model.FileName, missing);
            AddIfMissing(cacheRoot, def.Vocab.FileName, missing);
        }

        if (missing.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                _languages.Length > 0 ? "Models present; ready to serve." : "Model cache accessible.",
                data));
        }

        data["missing"] = missing;
        var description = _options.Download.Offline
            ? "Offline mode: required models are missing from the cache."
            : "Some models are not cached yet; they will download on first use.";

        // In offline mode, missing models mean the service cannot run at all.
        var status = _options.Download.Offline ? HealthStatus.Unhealthy : _failureStatus;
        return Task.FromResult(new HealthCheckResult(status, description, data: data));
    }

    private static void AddIfMissing(string cacheRoot, string fileName, List<string> missing)
    {
        if (!File.Exists(Path.Combine(cacheRoot, fileName)))
        {
            missing.Add(fileName);
        }
    }
}
