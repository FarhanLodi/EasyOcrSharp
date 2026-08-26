using EasyOcrSharp.Structure.Engine.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Structure.Engine.Recognition;

/// <summary>
/// Recognizes the text content of a rectified (upright) text-line crop. Implemented by
/// <see cref="SvtrRecognizer"/> (PaddleOCR's SVTR / CRNN recognizer + CTC decode).
/// </summary>
internal interface ITextRecognizer : IDisposable
{
    /// <summary>
    /// Recognizes a single rectified text-line crop.
    /// </summary>
    /// <param name="crop">The upright text-line crop (caller retains ownership).</param>
    /// <returns>The recognized <c>Text</c> and its mean per-character <c>Confidence</c> (0–1).</returns>
    (string Text, float Confidence) Recognize(Image<Rgb24> crop);

    /// <summary>
    /// Recognizes a batch of rectified crops in one or more ONNX runs (PaddleOCR batches boxes of similar
    /// aspect ratio for throughput). The result is positional: element <c>i</c> corresponds to
    /// <paramref name="crops"/>[i].
    /// </summary>
    /// <param name="crops">The upright text-line crops (caller retains ownership of each).</param>
    /// <returns>One (text, confidence) tuple per input crop, in the same order.</returns>
    IReadOnlyList<(string Text, float Confidence)> Recognize(IReadOnlyList<Image<Rgb24>> crops);

    /// <summary>
    /// Recognizes a batch of crops while honoring the per-call character filter
    /// (<see cref="RecognitionOptions.Allowlist"/> / <see cref="RecognitionOptions.Blocklist"/>) carried by
    /// <paramref name="options"/>. The filter is scoped to this call so a shared/cached recognizer stays safe
    /// to reuse with different options. Equivalent to <see cref="Recognize(IReadOnlyList{Image{Rgb24}})"/>
    /// when both lists are empty.
    /// </summary>
    /// <param name="crops">The upright text-line crops (caller retains ownership of each).</param>
    /// <param name="options">The recognition options whose allow/block lists to honor.</param>
    /// <returns>One (text, confidence) tuple per input crop, in the same order.</returns>
    IReadOnlyList<(string Text, float Confidence)> Recognize(IReadOnlyList<Image<Rgb24>> crops, RecognitionOptions options);
}
