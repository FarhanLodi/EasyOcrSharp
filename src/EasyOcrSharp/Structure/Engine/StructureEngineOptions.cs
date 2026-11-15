using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure.Engine.Services;

namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// Immutable runtime configuration handed to <see cref="StructureTextEngine"/>. Built by
/// <see cref="EasyOcrSharp.Structure.Engine.Services.StructureService"/> from <see cref="StructureServiceOptions"/>,
/// so the public surface and the engine share one configuration code path.
/// </summary>
internal sealed record StructureEngineOptions
{
    /// <summary>
    /// Optional model cache directory (defaults to LocalAppData or EASYOCRSHARP_STRUCTURE_CACHE).
    /// </summary>
    public string? ModelCachePath { get; init; }

    /// <summary>
    /// The execution provider to attempt. Defaults to <see cref="OcrExecutionProvider.Auto"/>.
    /// </summary>
    public OcrExecutionProvider ExecutionProvider { get; init; } = OcrExecutionProvider.Auto;

    /// <summary>
    /// Zero-based accelerator device index used by the CUDA / DirectML providers. Default 0.
    /// </summary>
    public int DeviceId { get; init; } = 0;

    /// <summary>
    /// Which PP-OCRv5 detection network to load (mobile default, server opt-in).
    /// </summary>
    public OcrModelVariant DetectionModel { get; init; } = OcrModelVariant.Mobile;

    /// <summary>
    /// Which PP-OCRv5 recognition network to load for the default (ch/en/ja) pack.
    /// Per-script packs have no server variant and always stay mobile.
    /// </summary>
    public OcrModelVariant RecognitionModel { get; init; } = OcrModelVariant.Mobile;

    /// <summary>
    /// Local detection ONNX file that replaces the registry detector entirely (no download, no checksum).
    /// </summary>
    public string? DetectionModelPath { get; init; }

    /// <summary>
    /// Local recognition ONNX file that replaces the default-pack recognizer (no download, no checksum).
    /// </summary>
    public string? RecognitionModelPath { get; init; }

    /// <summary>
    /// Local character dictionary paired with <see cref="RecognitionModelPath"/>; when null the default
    /// pack's dictionary is used.
    /// </summary>
    public string? RecognitionDictionaryPath { get; init; }

    /// <summary>
    /// Intra-op thread count for ONNX Runtime (null = runtime default). 1 = single-threaded ops.
    /// </summary>
    public int? IntraOpNumThreads { get; init; }

    /// <summary>
    /// Inter-op thread count for ONNX Runtime (null = runtime default).
    /// </summary>
    public int? InterOpNumThreads { get; init; }

    /// <summary>
    /// ONNX Runtime <c>session.intra_op.allow_spinning</c> (null = runtime default).
    /// </summary>
    public bool? AllowIntraOpSpinning { get; init; }

    /// <summary>
    /// CUDA provider <c>cudnn_conv_algo_search</c> (null = runtime default).
    /// </summary>
    public EasyOcrSharp.Services.CudnnConvolutionAlgorithmSearch? CudnnConvAlgoSearch { get; init; }

    /// <summary>
    /// How model files are downloaded and cached.
    /// </summary>
    public ModelDownloadOptions Download { get; init; } = new();

    /// <summary>
    /// Run the text-line orientation classifier (PaddleOCR's <c>use_textline_orientation</c>). Null (unset)
    /// keeps the per-call default (on); <c>false</c> or <c>true</c> becomes the default for every call that
    /// does not set <see cref="RecognitionOptions.UseTextLineOrientation"/> explicitly, and an explicit
    /// per-call value always wins (see <see cref="StructureTextEngine.ResolveUseTextLineOrientation"/>).
    /// </summary>
    public bool? UseTextLineOrientation { get; init; }

    /// <summary>
    /// Log the GPU upgrade hint as a one-time startup warning (default false). The hint string is always
    /// computed and exposed via <see cref="StructureTextEngine.GpuHint"/> regardless of this flag.
    /// </summary>
    public bool LogGpuHint { get; init; }
}
