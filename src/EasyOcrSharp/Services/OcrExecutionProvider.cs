namespace EasyOcrSharp.Services;

/// <summary>
/// Which ONNX Runtime execution provider EasyOcrSharp should try to use. Every non-CPU provider
/// needs the matching native runtime package installed; if it is missing or initialization fails,
/// EasyOcrSharp logs a warning and falls back to CPU rather than throwing.
/// </summary>
public enum OcrExecutionProvider
{
    /// <summary>Pure CPU. Always available.</summary>
    Cpu = 0,

    /// <summary>NVIDIA CUDA. Requires the <c>EasyOcrSharp.Gpu</c> package and CUDA 12+ on PATH.</summary>
    Cuda = 1,

    /// <summary>
    /// DirectML — GPU acceleration on <i>any</i> DirectX 12 GPU (NVIDIA, AMD, Intel) on Windows.
    /// Requires the <c>Microsoft.ML.OnnxRuntime.DirectML</c> package.
    /// </summary>
    DirectMl = 2,

    /// <summary>Apple CoreML (macOS / Apple Silicon). Requires a CoreML-enabled ONNX Runtime build.</summary>
    CoreMl = 3,

    /// <summary>
    /// The default. Probe the loaded ONNX Runtime for the best accelerator the host actually has and
    /// use it, falling back to CPU when none is present. The choice depends entirely on which provider
    /// package the consumer installed (only one ONNX Runtime native package can be referenced at a
    /// time): with <c>EasyOcrSharp.Gpu</c> a working CUDA GPU lights up CUDA; with
    /// <c>EasyOcrSharp.DirectMl</c> any DirectX 12 GPU lights up DirectML; with the base package only,
    /// this always resolves to <see cref="Cpu"/>.
    /// </summary>
    Auto = 4,
}
