using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Services;

/// <summary>
/// The coordinate frames a page passes through before detection, and the way back. OCR may run on a
/// square-pixel copy (<see cref="PreprocessingOptions.CorrectNonSquarePixels"/>) and, inside that, on a deskewed
/// canvas (<see cref="PreprocessingOptions.Deskew"/>); every line, word and character coordinate is mapped back
/// through both onto the caller's image before it is returned. Shared by the buffered and streaming pipelines.
/// </summary>
public sealed partial class EasyOcrService
{
    /// <summary>
    /// The square-pixel copy to OCR in place of <paramref name="image"/>, or null when the option is off or the
    /// image's pixels are square or of unknown shape (the overwhelmingly common case, which then takes the
    /// unchanged path on the caller's own image). The caller owns and disposes the copy.
    /// </summary>
    private static Image<Rgb24>? CreateSquarePixelInput(Image<Rgb24> image, RecognitionOptions options)
        => options.Preprocessing.CorrectNonSquarePixels ? NonSquarePixels.CreateSquarePixelCopy(image) : null;

    /// <summary>
    /// The square-pixel → caller-grid mapping for the frame OCR reports in, after document orientation turned
    /// the page by <paramref name="documentRotation"/>; null when no square-pixel copy was made.
    /// </summary>
    private static SquarePixelMapping? SquarePixelMappingFor(Image<Rgb24> image, Image<Rgb24>? squared, int documentRotation)
        => squared is null
            ? null
            : SquarePixelMapping.For(image.Width, image.Height, squared.Width, squared.Height, documentRotation);

    /// <summary>Re-expresses a caller's pixel <see cref="RecognitionOptions.Region"/> on the square-pixel copy.</summary>
    private static RecognitionOptions WithRegionOnSquarePixels(RecognitionOptions options, SquarePixelMapping? aspect)
        => aspect is { } mapping && options.Region is { } region
            ? options with { Region = mapping.RegionToSquare(region) }
            : options;

    /// <summary>
    /// Maps a finished result from the frame OCR ran in onto the caller's image: off the deskewed canvas
    /// (<paramref name="deskew"/>), then off the square-pixel copy (<paramref name="aspect"/>, which also restates
    /// <see cref="OcrResult.SourceWidth"/>/<see cref="OcrResult.SourceHeight"/> on the caller's grid). Line order
    /// and text are untouched. <paramref name="deskewAngle"/> is reported as <see cref="OcrResult.DeskewAngle"/>.
    /// Returns <paramref name="result"/> itself when there is nothing to map.
    /// </summary>
    internal static OcrResult ToCallerFrame(OcrResult result, DeskewCanvas? deskew, float deskewAngle, SquarePixelMapping? aspect)
    {
        if (deskew is null && aspect is null && deskewAngle == 0f) return result;

        var lines = result.Lines;
        if (deskew is { } canvas) lines = canvas.LinesToSource(lines);

        int sourceWidth = result.SourceWidth, sourceHeight = result.SourceHeight;
        if (aspect is { } mapping)
        {
            lines = LineGeometry.Transform(lines, mapping.ToSource);
            sourceWidth = mapping.SourceWidth(sourceWidth);
            sourceHeight = mapping.SourceHeight(sourceHeight);
        }

        return result with
        {
            Lines = lines,
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
            DeskewAngle = deskewAngle,
        };
    }

    /// <summary>One streamed line through the same mapping as <see cref="ToCallerFrame"/>.</summary>
    private static OcrLine LineToCallerFrame(OcrLine line, DeskewCanvas? deskew, SquarePixelMapping? aspect)
    {
        if (deskew is { } canvas) line = canvas.LineToSource(line);
        if (aspect is { } mapping) line = LineGeometry.Transform(line, mapping.ToSource);
        return line;
    }

    /// <summary>
    /// Re-expresses the caller's region on the frame the streaming pipeline detects in: the square-pixel copy,
    /// then the deskewed canvas.
    /// </summary>
    private static RecognitionOptions WithRegionOnWorkingFrame(RecognitionOptions options, SquarePixelMapping? aspect, DeskewCanvas? deskew)
    {
        options = WithRegionOnSquarePixels(options, aspect);
        return deskew is { } canvas && options.Region is { } region
            ? options with { Region = canvas.RegionToCanvas(region) }
            : options;
    }
}
