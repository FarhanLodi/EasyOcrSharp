namespace EasyOcrSharp.Services;

/// <summary>
/// How the CUDA execution provider picks cuDNN convolution algorithms — ONNX Runtime's
/// <c>cudnn_conv_algo_search</c> provider option, set through
/// <see cref="EasyOcrServiceOptions.CudnnConvAlgoSearch"/>. Only used when the CUDA provider is active.
/// </summary>
public enum CudnnConvolutionAlgorithmSearch
{
    /// <summary>
    /// Benchmark every candidate algorithm the first time each input shape is seen (ONNX Runtime's own
    /// default, <c>EXHAUSTIVE</c>). Fastest steady state for fixed shapes, but every new shape pays a
    /// search.
    /// </summary>
    Exhaustive,

    /// <summary>
    /// Let cuDNN's heuristics pick an algorithm without benchmarking (<c>HEURISTIC</c>). Usually the better
    /// choice for OCR: the CRAFT detector's input size changes with every page and the recognizer's width
    /// changes with every batch's longest line, so an exhaustive search is re-run on almost every request.
    /// </summary>
    Heuristic,

    /// <summary>
    /// Always use cuDNN's default algorithm (<c>DEFAULT</c>) — no search at all, possibly slower kernels.
    /// </summary>
    Default,
}
