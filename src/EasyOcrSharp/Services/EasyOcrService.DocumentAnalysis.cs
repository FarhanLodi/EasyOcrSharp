using EasyOcrSharp.Models;
using Microsoft.Extensions.Logging;
using EasyOcrSharp.Structure;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Services;

/// <summary>
/// Document-structure analysis (PP-StructureV3: layout regions, tables recovered as HTML, formulas,
/// seals, reading order), run by the in-repo structure engine. The analyzer — and every model
/// behind it — is created lazily on the first <c>AnalyzeDocumentAsync</c> call, so services that never
/// analyze documents pay nothing. It shares this service's execution provider, thread limits, cache
/// path (when one is set) and download resilience, and is disposed with the service.
/// </summary>
public sealed partial class EasyOcrService
{
    private volatile EasyOcrSharp.Structure.Engine.Services.StructureService? _documentAnalyzer;
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

        // The structure engine is part of this library now and shares its pixel types, so the decoded
        // image goes straight in — no PNG round-trip. The engine treats the image as caller-owned: it
        // clones before pre-processing rather than mutating, and never disposes it.
        return await analyzer.AnalyzeDocumentAsync(image, ToStructureOptions(options, _logger), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the underlying structure service on first use (double-checked lock). Creation is
    /// cheap — its models load lazily inside <c>AnalyzeDocumentAsync</c> — but the instance carries
    /// ONNX sessions once used, so exactly one is kept and disposed with this service. Callers hold an
    /// operation scope, and <see cref="DisposeAsync"/> drains scopes before disposing, so the analyzer
    /// can never be created after (or disposed under) an in-flight call.
    /// </summary>
    private EasyOcrSharp.Structure.Engine.Services.StructureService GetOrCreateDocumentAnalyzer()
    {
        if (_documentAnalyzer is { } existing) return existing;

        lock (_documentAnalyzerLock)
        {
            return _documentAnalyzer ??= new EasyOcrSharp.Structure.Engine.Services.StructureService(BuildAnalyzerOptions());
        }
    }

    /// <summary>
    /// Maps this service's configuration onto the analyzer's. The cache path is shared only when the
    /// caller set one explicitly (otherwise each library keeps its own default cache); the model-source
    /// mirror override is intentionally NOT propagated because it points at a mirror of the EasyOCR
    /// models — mirror the structure models via the structure engine's own override instead.
    /// </summary>
    private EasyOcrSharp.Structure.Engine.Services.StructureServiceOptions BuildAnalyzerOptions() => new()
    {
        ModelCachePath = _engineOptions.ModelCachePath,
        MaxImagePixels = _maxImagePixels,
        ExecutionProvider = MapProvider(_engineOptions.ExecutionProvider),
        IntraOpNumThreads = _engineOptions.IntraOpNumThreads,
        InterOpNumThreads = _engineOptions.InterOpNumThreads,
        Download = MapDownload(_engineOptions.Download),
    };

    private static EasyOcrSharp.Structure.Engine.Services.OcrExecutionProvider MapProvider(OcrExecutionProvider provider) => provider switch
    {
        OcrExecutionProvider.Cpu => EasyOcrSharp.Structure.Engine.Services.OcrExecutionProvider.Cpu,
        OcrExecutionProvider.Cuda => EasyOcrSharp.Structure.Engine.Services.OcrExecutionProvider.Cuda,
        OcrExecutionProvider.DirectMl => EasyOcrSharp.Structure.Engine.Services.OcrExecutionProvider.DirectMl,
        OcrExecutionProvider.CoreMl => EasyOcrSharp.Structure.Engine.Services.OcrExecutionProvider.CoreMl,
        _ => EasyOcrSharp.Structure.Engine.Services.OcrExecutionProvider.Auto,
    };

    private static EasyOcrSharp.Structure.Engine.Services.ModelDownloadOptions MapDownload(ModelDownloadOptions download) => new()
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
        : IProgress<EasyOcrSharp.Structure.Engine.Services.ModelDownloadProgress>
    {
        public void Report(EasyOcrSharp.Structure.Engine.Services.ModelDownloadProgress value)
            => inner.Report(new ModelDownloadProgress(value.FileName, value.BytesDownloaded, value.TotalBytes));
    }

    /// <summary>
    /// Maps the public per-call options onto the engine's <see cref="StructureOptions"/>. Language
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
            var languages = new List<EasyOcrSharp.Structure.Engine.Models.OcrLanguage>(options.Languages.Count);
            foreach (var code in options.Languages)
            {
                if (EasyOcrSharp.Structure.Engine.Models.OcrLanguageExtensions.TryFromCode(code, out var language))
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
