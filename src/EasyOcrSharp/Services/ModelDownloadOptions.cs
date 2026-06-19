using System.Net.Http;

namespace EasyOcrSharp.Services;

/// <summary>
/// Progress for a single model-file download, reported via <see cref="ModelDownloadOptions.Progress"/>.
/// </summary>
/// <param name="FileName">The asset being downloaded (e.g. <c>latin_g2.onnx</c>).</param>
/// <param name="BytesDownloaded">Bytes received so far (including any resumed prefix).</param>
/// <param name="TotalBytes">Total size in bytes, or <c>-1</c> if the server didn't report it.</param>
public readonly record struct ModelDownloadProgress(string FileName, long BytesDownloaded, long TotalBytes)
{
    /// <summary>Completion fraction (0–1), or <c>null</c> when the total size is unknown.</summary>
    public double? Fraction => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes : null;
}

/// <summary>
/// Controls how on-demand ONNX model files are fetched and cached. Sensible defaults are provided;
/// override for proxies, air-gapped environments, progress UIs, or custom resilience.
/// </summary>
public sealed class ModelDownloadOptions
{
    /// <summary>
    /// Number of additional attempts after a transient network/IO failure (exponential backoff).
    /// Default 3. Set to 0 to disable retries.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay for exponential backoff between retries. Default 2 seconds.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Strict offline mode. When true, a missing model is a hard error (<see cref="EasyOcrSharpException"/>)
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
    /// <c>EASYOCRSHARP_MODEL_BASE_URL</c> environment variable). Use for a private/offline mirror.
    /// </summary>
    public string? BaseUrlOverride { get; set; }

    /// <summary>
    /// Allow downloading models over a plain (non-HTTPS) URL. Default <c>false</c>: a <c>BaseUrlOverride</c>
    /// or <c>EASYOCRSHARP_MODEL_BASE_URL</c> that is not <c>https://</c> is rejected, because the model
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
