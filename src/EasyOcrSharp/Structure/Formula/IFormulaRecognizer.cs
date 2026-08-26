using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Structure.Formula;

/// <summary>
/// Recognizes the LaTeX source of a cropped mathematical-formula region. Implemented by
/// <see cref="LatexOcrRecognizer"/> (the LaTeX-OCR / RapidLaTeXOCR encoder-decoder ONNX model). Owns and
/// disposes its ONNX session(s).
/// </summary>
internal interface IFormulaRecognizer : IDisposable
{
    /// <summary>
    /// Recognizes the LaTeX of one formula crop.
    /// </summary>
    /// <param name="crop">The cropped formula region (caller retains ownership).</param>
    /// <returns>The recovered LaTeX string (empty when nothing is recognized).</returns>
    string RecognizeLatex(Image<Rgb24> crop);
}
