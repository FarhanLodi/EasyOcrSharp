using System.Net.Http;

namespace EasyOcrSharp.Structure.Engine.Services;

/// <summary>
/// Controls how on-demand ONNX model files are fetched and cached. Sensible defaults are provided;
/// override for proxies, air-gapped environments, progress UIs, or custom resilience.
/// </summary>
internal sealed class ModelDownloadOptions
{
    /// <summary>
    /// Number of additional attempts after a transient network/IO failure (exponential backoff).
    /// Default 3. Set to 0 to disable retries.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Base delay for exponential backoff between retries. Default 2 seconds.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Strict offline mode. When true, a missing model is a hard error (<see cref="StructureEngineException"/>)
    /// instead of triggering a download — ideal for air-gapped deployments where the cache is pre-seeded.
    /// </summary>
    public bool Offline { get; set; }

    /// <summary>
    /// Optional progress callback invoked periodically during downloads (in addition to any logger).
    /// Handy for CLI/GUI progress bars.
    /// </summary>
    public IProgress<ModelDownloadProgress>? Progress { get; set; }

    /// <summary>
    /// Supplies the <see cref="HttpClient"/> used for downloads — e.g. one from
    /// <c>IHttpClientFactory</c> configured with a corporate proxy or custom handler. When null, a
    /// shared internal client is used. The returned client is not disposed by EasyOcrSharp.
    /// </summary>
    public Func<HttpClient>? HttpClientFactory { get; set; }

    /// <summary>
    /// Base URL to fetch models from, overriding the built-in Hugging Face host (and the
    /// <c>EASYOCRSHARP_STRUCTURE_MODEL_BASE_URL</c> environment variable). Use for a private/offline mirror.
    /// </summary>
    public string? BaseUrlOverride { get; set; }

    /// <summary>
    /// Allow downloading models over a plain (non-HTTPS) URL. Default <c>false</c>: a <c>BaseUrlOverride</c>
    /// or <c>EASYOCRSHARP_STRUCTURE_MODEL_BASE_URL</c> that is not <c>https://</c> is rejected, because the model
    /// download is the supply-chain trust root (a downloaded <c>.onnx</c> is parsed by native ONNX Runtime).
    /// Enable only for a trusted on-host/offline mirror you control.
    /// </summary>
    public bool AllowInsecureModelSource { get; set; }

    /// <summary>
    /// Allow loading a downloaded model that has no known SHA256 checksum in the registry. Default
    /// <c>false</c> (fail-closed): every remote model must be integrity-verified. Enable only when serving
    /// your own unlisted assets from a trusted <c>BaseUrlOverride</c>; local custom recognizers are never
    /// downloaded and are unaffected.
    /// </summary>
    public bool AllowUnverifiedModels { get; set; }
}
