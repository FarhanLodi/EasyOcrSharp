using EasyOcrSharp.Internal;

namespace EasyOcrSharp.Services;

/// <summary>
/// Configuration for <see cref="EasyOcrService"/> — passed to its constructor or to
/// <see cref="ServiceCollectionExtensions.AddEasyOcrSharp"/>. Every option is additive and optional;
/// an instance with no changes behaves exactly like the parameterless service.
/// </summary>
public sealed class EasyOcrServiceOptions
{
    /// <summary>Optional model cache directory (defaults to LocalAppData or EASYOCRSHARP_CACHE).</summary>
    public string? ModelCachePath { get; set; }

    /// <summary>
    /// Reject an image whose decoded pixel count (width × height) exceeds this value, checked from the
    /// image header <b>before</b> the pixels are decoded into memory when loading from a file, stream, or
    /// byte buffer. Guards against decompression-bomb / pixel-flood denial of service when OCR-ing
    /// untrusted input. Default 100,000,000 (100 MP). Set to 0 to disable. Already-decoded
    /// <see cref="SixLabors.ImageSharp.Image{TPixel}"/> inputs (the caller's own allocation) are not checked.
    /// </summary>
    public long MaxImagePixels { get; set; } = 100_000_000;

    /// <summary>
    /// Convenience flag kept for backwards compatibility: when true (and <see cref="ExecutionProvider"/>
    /// has not been set to an explicit provider) the CUDA provider is forced. Prefer leaving
    /// <see cref="ExecutionProvider"/> at <see cref="OcrExecutionProvider.Auto"/>, which already enables a
    /// GPU when one is present, or setting it directly.
    /// </summary>
    public bool UseGpu { get; set; }

    /// <summary>
    /// Which ONNX Runtime execution provider to use. Defaults to <see cref="OcrExecutionProvider.Auto"/>,
    /// which probes the loaded runtime and uses the best available accelerator (CUDA / DirectML / CoreML),
    /// falling back to CPU when none is installed.
    /// </summary>
    public OcrExecutionProvider ExecutionProvider { get; set; } = OcrExecutionProvider.Auto;

    /// <summary>
    /// ONNX Runtime intra-op thread count (parallelism inside a single model run). Null = runtime
    /// default. Set to a small number to cap CPU use in busy multi-tenant servers.
    /// </summary>
    public int? IntraOpNumThreads { get; set; }

    /// <summary>ONNX Runtime inter-op thread count. Null = runtime default.</summary>
    public int? InterOpNumThreads { get; set; }

    /// <summary>How model files are downloaded and cached (retries, progress, offline, proxy, mirror).</summary>
    public ModelDownloadOptions Download { get; set; } = new();

    /// <summary>
    /// User-supplied recognizers (locally exported ONNX models) registered for specific language codes.
    /// A custom recognizer takes precedence over the built-in pack for the languages it declares and is
    /// loaded from disk rather than downloaded. EasyOCR's custom <c>recog_network</c>.
    /// </summary>
    public IList<CustomRecognizer> CustomRecognizers { get; } = new List<CustomRecognizer>();

    /// <summary>
    /// Use the int8-quantized recognizer models instead of the float ones — EasyOCR's
    /// <c>quantize=True</c>. Smaller and typically faster on CPU at a small accuracy cost. The engine
    /// fetches <c>&lt;pack&gt;.int8.onnx</c> variants; built-in packs only. Default false.
    /// </summary>
    public bool Quantize { get; set; }

    /// <summary>
    /// When <c>true</c>, a one-time startup <b>warning</b> is logged if a usable GPU is physically present
    /// but OCR is running on CPU (it names the exact provider package to install, e.g.
    /// <c>EasyOcrSharp.Gpu</c>). Default <c>false</c> — the hint is silent, so nothing is logged.
    /// Regardless of this flag, <see cref="EasyOcrService.GpuAccelerationHint"/> is still populated, so an
    /// app that wants the nudge can read and surface it itself.
    /// </summary>
    public bool LogGpuHint { get; set; }

    /// <summary>Maps the public options to the engine's internal configuration record.</summary>
    internal EngineOptions ToEngineOptions()
    {
        var provider = ExecutionProvider;
        // UseGpu predates Auto and meant "force CUDA". Honor it unless the caller picked an explicit
        // provider; Auto (the default) and the legacy Cpu value both defer to it.
        if (UseGpu && provider is OcrExecutionProvider.Auto or OcrExecutionProvider.Cpu)
        {
            provider = OcrExecutionProvider.Cuda;
        }

        return new EngineOptions
        {
            ModelCachePath = string.IsNullOrWhiteSpace(ModelCachePath) ? null : Path.GetFullPath(ModelCachePath),
            ExecutionProvider = provider,
            IntraOpNumThreads = IntraOpNumThreads,
            InterOpNumThreads = InterOpNumThreads,
            Download = Download,
            CustomRecognizers = CustomRecognizers.ToArray(),
            Quantize = Quantize,
            LogGpuHint = LogGpuHint,
        };
    }
}
