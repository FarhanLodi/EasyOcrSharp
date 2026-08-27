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
    /// Intra-op thread count for ONNX Runtime (null = runtime default). 1 = single-threaded ops.
    /// </summary>
    public int? IntraOpNumThreads { get; init; }

    /// <summary>
    /// Inter-op thread count for ONNX Runtime (null = runtime default).
    /// </summary>
    public int? InterOpNumThreads { get; init; }

    /// <summary>
    /// How model files are downloaded and cached.
    /// </summary>
    public ModelDownloadOptions Download { get; init; } = new();

    /// <summary>
    /// Run the text-line orientation classifier (PaddleOCR's <c>use_textline_orientation</c>). When false,
    /// the classifier model is never loaded. Can also be requested per call via
    /// <see cref="RecognitionOptions.UseTextLineOrientation"/>.
    /// </summary>
    public bool UseTextLineOrientation { get; init; }

    /// <summary>
    /// Log the GPU upgrade hint as a one-time startup warning (default false). The hint string is always
    /// computed and exposed via <see cref="StructureTextEngine.GpuHint"/> regardless of this flag.
    /// </summary>
    public bool LogGpuHint { get; init; }
}
