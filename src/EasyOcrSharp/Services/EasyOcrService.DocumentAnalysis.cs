using EasyOcrSharp.Models;
using Microsoft.Extensions.Logging;
using PaddleOcrNet.Structure;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace EasyOcrSharp.Services;

/// <summary>
/// Document-structure analysis (PP-StructureV3: layout regions, tables recovered as HTML, formulas,
/// seals, reading order), delegated to the <c>PaddleOcrNet</c> engine. The analyzer — and every model
/// behind it — is created lazily on the first <c>AnalyzeDocumentAsync</c> call, so services that never
/// analyze documents pay nothing. It shares this service's execution provider, thread limits, cache
/// path (when one is set) and download resilience, and is disposed with the service.
/// </summary>
public sealed partial class EasyOcrService
{
    private volatile PaddleOcrNet.Services.PaddleOcrService? _documentAnalyzer;
    private readonly object _documentAnalyzerLock = new();

    /// <inheritdoc cref="IEasyOcrService.AnalyzeDocumentAsync(string, DocumentAnalysisOptions?, CancellationToken)" />
    public async Task<StructureResult> AnalyzeDocumentAsync(
        string imagePath,
        DocumentAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var op = BeginOperation();
        var analyzer = GetOrCreateDocumentAnalyzer();
        return await analyzer.AnalyzeDocumentAsync(imagePath, ToStructureOptions(options, _logger), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IEasyOcrService.AnalyzeDocumentAsync(Stream, DocumentAnalysisOptions?, CancellationToken)" />
    public async Task<StructureResult> AnalyzeDocumentAsync(
        Stream imageStream,
        DocumentAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var op = BeginOperation();
        var analyzer = GetOrCreateDocumentAnalyzer();
        return await analyzer.AnalyzeDocumentAsync(imageStream, ToStructureOptions(options, _logger), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IEasyOcrService.AnalyzeDocumentAsync(byte[], DocumentAnalysisOptions?, CancellationToken)" />
    public async Task<StructureResult> AnalyzeDocumentAsync(
        byte[] imageBytes,
        DocumentAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var op = BeginOperation();
        var analyzer = GetOrCreateDocumentAnalyzer();
        return await analyzer.AnalyzeDocumentAsync(imageBytes, ToStructureOptions(options, _logger), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IEasyOcrService.AnalyzeDocumentAsync(ReadOnlyMemory{byte}, DocumentAnalysisOptions?, CancellationToken)" />
    public async Task<StructureResult> AnalyzeDocumentAsync(
        ReadOnlyMemory<byte> imageBytes,
        DocumentAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var op = BeginOperation();
        var analyzer = GetOrCreateDocumentAnalyzer();
        return await analyzer.AnalyzeDocumentAsync(imageBytes, ToStructureOptions(options, _logger), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IEasyOcrService.AnalyzeDocumentAsync(Image{Rgb24}, DocumentAnalysisOptions?, CancellationToken)" />
    public async Task<StructureResult> AnalyzeDocumentAsync(
        Image<Rgb24> image,
        DocumentAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var op = BeginOperation();
        ArgumentNullException.ThrowIfNull(image);
        var analyzer = GetOrCreateDocumentAnalyzer();
        return await analyzer.AnalyzeDocumentAsync(image, ToStructureOptions(options, _logger), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the underlying PaddleOcrNet service on first use (double-checked lock). Creation is
    /// cheap — its models load lazily inside <c>AnalyzeDocumentAsync</c> — but the instance carries
    /// ONNX sessions once used, so exactly one is kept and disposed with this service. Callers hold an
    /// operation scope, and <see cref="DisposeAsync"/> drains scopes before disposing, so the analyzer
    /// can never be created after (or disposed under) an in-flight call.
    /// </summary>
    private PaddleOcrNet.Services.PaddleOcrService GetOrCreateDocumentAnalyzer()
    {
        if (_documentAnalyzer is { } existing) return existing;

        lock (_documentAnalyzerLock)
        {
            return _documentAnalyzer ??= new PaddleOcrNet.Services.PaddleOcrService(BuildAnalyzerOptions());
        }
    }

    /// <summary>
    /// Maps this service's configuration onto the analyzer's. The cache path is shared only when the
    /// caller set one explicitly (otherwise each library keeps its own default cache); the model-source
    /// mirror override is intentionally NOT propagated because it points at a mirror of the EasyOCR
    /// models — mirror the structure models via PaddleOcrNet's own override instead.
    /// </summary>
    private PaddleOcrNet.Services.PaddleOcrServiceOptions BuildAnalyzerOptions() => new()
    {
        ModelCachePath = _engineOptions.ModelCachePath,
        MaxImagePixels = _maxImagePixels,
        ExecutionProvider = MapProvider(_engineOptions.ExecutionProvider),
        IntraOpNumThreads = _engineOptions.IntraOpNumThreads,
        InterOpNumThreads = _engineOptions.InterOpNumThreads,
        Download = MapDownload(_engineOptions.Download),
    };

    private static PaddleOcrNet.Services.OcrExecutionProvider MapProvider(OcrExecutionProvider provider) => provider switch
    {
        OcrExecutionProvider.Cpu => PaddleOcrNet.Services.OcrExecutionProvider.Cpu,
        OcrExecutionProvider.Cuda => PaddleOcrNet.Services.OcrExecutionProvider.Cuda,
        OcrExecutionProvider.DirectMl => PaddleOcrNet.Services.OcrExecutionProvider.DirectMl,
        OcrExecutionProvider.CoreMl => PaddleOcrNet.Services.OcrExecutionProvider.CoreMl,
        _ => PaddleOcrNet.Services.OcrExecutionProvider.Auto,
    };

    private static PaddleOcrNet.Services.ModelDownloadOptions MapDownload(ModelDownloadOptions download) => new()
    {
        MaxRetries = download.MaxRetries,
        RetryBaseDelay = download.RetryBaseDelay,
        Offline = download.Offline,
        HttpClientFactory = download.HttpClientFactory,
        AllowInsecureModelSource = download.AllowInsecureModelSource,
        AllowUnverifiedModels = download.AllowUnverifiedModels,
        Progress = download.Progress is { } progress ? new ProgressAdapter(progress) : null,
    };

    /// <summary>Forwards the analyzer's download progress into the caller's EasyOcrSharp-typed sink.</summary>
    private sealed class ProgressAdapter(IProgress<ModelDownloadProgress> inner)
        : IProgress<PaddleOcrNet.Services.ModelDownloadProgress>
    {
        public void Report(PaddleOcrNet.Services.ModelDownloadProgress value)
            => inner.Report(new ModelDownloadProgress(value.FileName, value.BytesDownloaded, value.TotalBytes));
    }

    /// <summary>
    /// Maps the public per-call options onto PaddleOcrNet's <see cref="StructureOptions"/>. Language
    /// codes that don't map to a known recognizer pack are skipped with a warning (mirroring how
    /// unsupported OCR languages are handled elsewhere in the service).
    /// </summary>
    internal static StructureOptions ToStructureOptions(DocumentAnalysisOptions? options, ILogger? logger)
    {
        options ??= DocumentAnalysisOptions.Default;
        var structure = new StructureOptions
        {
            UseDocOrientation = options.DocumentOrientation,
            UseUnwarp = options.DocumentUnwarp,
            RecognizeTables = options.RecognizeTables,
            RecognizeFormulas = options.RecognizeFormulas,
            RecognizeSeals = options.RecognizeSeals,
            TableModel = options.TableModel == DocumentTableModel.SlaNeXt
                ? TableRecognitionModel.SlaNeXt
                : TableRecognitionModel.SlanetPlus,
        };

        if (options.Languages is { Count: > 0 })
        {
            var languages = new List<PaddleOcrNet.Models.OcrLanguage>(options.Languages.Count);
            foreach (var code in options.Languages)
            {
                if (PaddleOcrNet.Models.OcrLanguageExtensions.TryFromCode(code, out var language))
                {
                    languages.Add(language);
                }
                else
                {
                    logger?.LogWarning("Language '{Lang}' is not a known document-analysis language code; skipping.", code);
                }
            }
            if (languages.Count > 0)
            {
                structure = structure with { Languages = languages };
            }
        }

        return structure;
    }
}
