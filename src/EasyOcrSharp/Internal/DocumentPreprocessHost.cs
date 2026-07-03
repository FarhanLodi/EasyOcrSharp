using EasyOcrSharp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Lazily downloads and hosts the ONNX sessions behind the model-based document preprocessing steps
/// (<see cref="Models.PreprocessingOptions.DocumentOrientation"/> and
/// <see cref="Models.PreprocessingOptions.DocumentUnwarp"/>) and runs them via
/// <see cref="DocPreprocessor"/>. Engine-agnostic: both the EasyOCR and the PaddleOCR pipeline share
/// this host, and neither model is touched (downloaded or loaded) until the matching option is first
/// used. Thread-safe; sessions are created once and reused across calls.
/// </summary>
internal sealed class DocumentPreprocessHost : IDisposable
{
    private readonly EngineOptions _options;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private InferenceSession? _orientation;
    private InferenceSession? _unwarp;
    private SessionOptions? _sessionOptions;
    private volatile bool _disposed;

    public DocumentPreprocessHost(EngineOptions options, ILogger? logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Applies the requested document-preprocessing stages (orientation upright first, then unwarp,
    /// matching PaddleX's pipeline order) and returns a fresh corrected image the caller owns, plus
    /// the clockwise rotation (degrees) that was applied to upright the page. Missing models are
    /// downloaded on first use.
    /// </summary>
    public async Task<(Image<Rgb24> Image, int RotationApplied)> ApplyAsync(
        Image<Rgb24> image, bool orientation, bool unwarp, CancellationToken cancellationToken)
    {
        await EnsureSessionsAsync(orientation, unwarp, cancellationToken).ConfigureAwait(false);

        // DocPreprocessor is a thin, stateless wrapper over the sessions (it resolves input names in
        // its constructor); we intentionally do NOT dispose it so the shared sessions survive for the
        // next call — this host owns and disposes the sessions itself.
        var preprocessor = new DocPreprocessor(_orientation, _unwarp);
        return preprocessor.Apply(image, orientation, unwarp);
    }

    /// <summary>Downloads and opens the session for each requested stage that isn't loaded yet.</summary>
    private async Task EnsureSessionsAsync(bool orientation, bool unwarp, CancellationToken cancellationToken)
    {
        if ((!orientation || _orientation is not null) && (!unwarp || _unwarp is not null)) return;

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (orientation && _orientation is null)
            {
                var path = await ModelDownloadManager.EnsureModelAsync(
                    DocPreprocessModelRegistry.DocOrientationClassifier, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
                _orientation = CreateSession(path);
                _logger?.LogInformation("Document-orientation classifier loaded from {Path}", path);
            }
            if (unwarp && _unwarp is null)
            {
                var path = await ModelDownloadManager.EnsureModelAsync(
                    DocPreprocessModelRegistry.DocUnwarp, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
                _unwarp = CreateSession(path);
                _logger?.LogInformation("UVDoc unwarp model loaded from {Path}", path);
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Opens an ONNX session on the resolved provider, downgrading to CPU once if the accelerated
    /// session fails to initialize (same policy as the OCR engines' first model load).
    /// </summary>
    private InferenceSession CreateSession(string modelPath)
    {
        _sessionOptions ??= BuildSessionOptions(ExecutionProviderResolver.Resolve(_options.ExecutionProvider, _logger));
        try
        {
            return new InferenceSession(modelPath, _sessionOptions);
        }
        catch (Exception ex) when (_options.ExecutionProvider != OcrExecutionProvider.Cpu)
        {
            _logger?.LogWarning(ex,
                "Accelerated session initialization failed for document preprocessing; falling back to CPU.");
            _sessionOptions.Dispose();
            _sessionOptions = BuildSessionOptions(OcrExecutionProvider.Cpu);
            return new InferenceSession(modelPath, _sessionOptions);
        }
    }

    private SessionOptions BuildSessionOptions(OcrExecutionProvider provider)
        => ExecutionProviderResolver.BuildSessionOptions(provider, _options, _logger, perBoxParallel: false);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _orientation?.Dispose();
        _unwarp?.Dispose();
        _sessionOptions?.Dispose();
        _loadLock.Dispose();
    }
}
