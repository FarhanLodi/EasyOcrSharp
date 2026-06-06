using EasyOcrSharp.Services;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Immutable runtime configuration handed to <see cref="OnnxEasyOcrEngine"/>. Built by
/// <see cref="EasyOcrSharp.Services.EasyOcrService"/> from either the legacy positional constructor
/// or <see cref="EasyOcrServiceOptions"/>, so both entry points share one code path.
/// </summary>
internal sealed record EngineOptions
{
    public string? ModelCachePath { get; init; }

    public OcrExecutionProvider ExecutionProvider { get; init; } = OcrExecutionProvider.Auto;

    /// <summary>Intra-op thread count for ONNX Runtime (null = runtime default). 1 = single-threaded ops.</summary>
    public int? IntraOpNumThreads { get; init; }

    /// <summary>Inter-op thread count for ONNX Runtime (null = runtime default).</summary>
    public int? InterOpNumThreads { get; init; }

    public ModelDownloadOptions Download { get; init; } = new();

    /// <summary>User-supplied recognizers (local ONNX models) that override built-in packs by language.</summary>
    public IReadOnlyList<CustomRecognizer> CustomRecognizers { get; init; } = Array.Empty<CustomRecognizer>();

    /// <summary>Use the int8-quantized recognizer variants (EasyOCR's <c>quantize=True</c>).</summary>
    public bool Quantize { get; init; }
}
