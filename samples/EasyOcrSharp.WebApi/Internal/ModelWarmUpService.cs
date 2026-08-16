using EasyOcrSharp.Services;

namespace EasyOcrSharp.WebApi.Internal;

/// <summary>
/// Downloads the model files and creates the ONNX sessions in the background at startup, so the first
/// real request does not pay cold-start latency (which, on an empty cache, includes a model download).
/// </summary>
/// <remarks>
/// Warm-up runs off the startup path on purpose: a container that cannot reach the model host should
/// still start, report its state through <c>/health</c>, and let the orchestrator decide — not crash
/// loop. Registered only when <c>EasyOcr:WarmUpOnStart</c> is set.
/// </remarks>
internal sealed class ModelWarmUpService : BackgroundService
{
    private readonly IEasyOcrService _ocr;
    private readonly WebApiOptions _options;
    private readonly ILogger<ModelWarmUpService> _logger;

    /// <summary>Creates the warm-up service.</summary>
    public ModelWarmUpService(IEasyOcrService ocr, WebApiOptions options, ILogger<ModelWarmUpService> logger)
    {
        _ocr = ocr;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // WarmUp may do a good deal of synchronous work before its first await; yielding first
        // guarantees host startup is never blocked by it.
        await Task.Yield();

        var languages = _options.WarmUpLanguages;
        if (languages.Length == 0)
        {
            return;
        }

        var started = TimeProvider.System.GetTimestamp();
        _logger.LogInformation("Warming up OCR models for [{Languages}]...", string.Join(", ", languages));

        try
        {
            await _ocr.WarmUp(languages, stoppingToken).ConfigureAwait(false);
            _logger.LogInformation(
                "OCR models ready after {ElapsedMs:F0} ms.",
                TimeProvider.System.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down mid-warm-up is normal.
        }
        catch (Exception ex)
        {
            // Never fatal: the models can still be fetched lazily on the first request, and /health
            // already reports that they are missing.
            _logger.LogWarning(ex, "Model warm-up failed; models will be loaded on first use instead.");
        }
    }
}
