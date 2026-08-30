using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EasyOcrSharp.Services;

/// <summary>
/// Downloads the model files and creates the ONNX sessions in the background at startup, so the first
/// real request does not pay cold-start latency (which, on an empty cache, includes a model download).
/// Register with <see cref="ServiceCollectionExtensions.AddEasyOcrWarmUp"/>.
/// </summary>
/// <remarks>
/// Warm-up runs off the startup path on purpose: a container that cannot reach the model host should
/// still start, report its state through <c>/health</c>, and let the orchestrator decide — not crash
/// loop. The outcome is published to the shared <see cref="EasyOcrWarmUpState"/> singleton so
/// <see cref="EasyOcrHealthCheck"/> (or the app's own status page) can report "still warming up" instead
/// of claiming readiness the moment the process starts.
/// </remarks>
public sealed class EasyOcrWarmUpService : BackgroundService
{
    private readonly IEasyOcrService _ocr;
    private readonly string[] _languages;
    private readonly EasyOcrWarmUpState _state;
    private readonly ILogger<EasyOcrWarmUpService>? _logger;

    /// <summary>Creates the warm-up service.</summary>
    /// <param name="ocrService">The OCR service whose models should be preloaded.</param>
    /// <param name="languages">Languages to preload. Empty means "nothing to do" — warm-up completes immediately.</param>
    /// <param name="state">Shared state to publish the outcome to. A private instance is used when null.</param>
    /// <param name="logger">Optional logger.</param>
    public EasyOcrWarmUpService(
        IEasyOcrService ocrService,
        IEnumerable<string> languages,
        EasyOcrWarmUpState? state = null,
        ILogger<EasyOcrWarmUpService>? logger = null)
    {
        _ocr = ocrService ?? throw new ArgumentNullException(nameof(ocrService));
        _languages = languages?.ToArray() ?? Array.Empty<string>();
        _state = state ?? new EasyOcrWarmUpState();
        _logger = logger;
    }

    /// <summary>The shared state this service publishes its outcome to.</summary>
    public EasyOcrWarmUpState State => _state;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // WarmUp may do a good deal of synchronous work before its first await; yielding first
        // guarantees host startup is never blocked by it.
        await Task.Yield();

        if (_languages.Length == 0)
        {
            // Nothing to preload: report Completed rather than leaving readiness stuck at NotStarted
            // forever, which would make a health check registered alongside it never report ready.
            _state.MarkCompleted(TimeSpan.Zero);
            return;
        }

        _state.MarkStarted(_languages);
        var started = TimeProvider.System.GetTimestamp();
        _logger?.LogInformation("Warming up OCR models for [{Languages}]...", string.Join(", ", _languages));

        try
        {
            await _ocr.WarmUp(_languages, stoppingToken).ConfigureAwait(false);
            var elapsed = TimeProvider.System.GetElapsedTime(started);
            _state.MarkCompleted(elapsed);
            _logger?.LogInformation("OCR models ready after {ElapsedMs:F0} ms.", elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down mid-warm-up is normal: not an error, and not a failure to report. The state
            // is deliberately left as InProgress — the process is going away, and flipping it to Failed
            // would make an ordinary shutdown look like a broken deployment to whatever scraped it last.
        }
        catch (Exception ex)
        {
            // Never fatal: the models can still be fetched lazily on the first request, and the health
            // check already reports both the missing models and this failure. Letting the exception
            // escape would take the whole host down (BackgroundServiceExceptionBehavior.StopHost).
            _state.MarkFailed(TimeProvider.System.GetElapsedTime(started), ex);
            _logger?.LogWarning(ex, "Model warm-up failed; models will be loaded on first use instead.");
        }
    }
}
