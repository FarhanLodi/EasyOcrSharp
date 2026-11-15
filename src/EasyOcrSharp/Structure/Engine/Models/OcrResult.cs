using System;
using System.Collections.Generic;
using EasyOcrSharp.Structure.Engine.Services;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Engine.Models;

/// <summary>
/// Represents the result of an OCR operation.
/// </summary>
internal sealed record OcrResult
{
    /// <summary>
    /// Gets the concatenated text extracted from the image.
    /// </summary>
    public required string FullText { get; init; }

    /// <summary>
    /// Gets the collection of detailed line results.
    /// </summary>
    public required IReadOnlyList<OcrLine> Lines { get; init; }

    /// <summary>
    /// Gets the languages that were used during recognition.
    /// </summary>
    public required IReadOnlyList<string> Languages { get; init; }

    /// <summary>
    /// Gets the language code(s) inferred by automatic language detection, ordered by how many text crops
    /// each won (most first). Populated only when auto-detection ran (the request set
    /// <see cref="RecognitionOptions.AutoDetectLanguage"/> or passed the language code <c>"auto"</c>);
    /// otherwise empty.
    /// </summary>
    public IReadOnlyList<string> DetectedLanguages { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets the page orientation detected by the document-orientation classifier (or the brute-force
    /// orientation pass): the clockwise degrees (0, 90, 180 or 270) by which the input page was found
    /// rotated away from upright. 0 when the page was upright or orientation detection did not run
    /// (<see cref="RecognitionOptions.UseDocOrientation"/> off, or the classifier model unavailable).
    /// The pipeline internally rotated the page by <c>(360 − DetectedOrientation) % 360</c>° clockwise
    /// before reading it; the returned coordinates are mapped back to the original image's orientation.
    /// </summary>
    public int DetectedOrientation { get; init; }

    /// <summary>
    /// Gets the clockwise rotation (degrees) that <see cref="PreprocessingOptions.Deskew"/> applied to
    /// straighten the page — the page's own skew was the negation of this. 0 when deskew is off or the skew
    /// was negligible. Deskew reads the page on a rotated, enlarged canvas; the returned line and word
    /// coordinates are already mapped back onto the original, unrotated image, so this is informational.
    /// </summary>
    public float DeskewAngle { get; init; }

    /// <summary>
    /// Gets the duration of the OCR operation.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Gets a value indicating whether GPU acceleration was actually used for this operation —
    /// equivalent to <see cref="ExecutionProvider"/> being a non-CPU provider. Reflects the provider
    /// live at result-build time, so an accelerator that failed to attach and fell back to CPU reports
    /// <c>false</c> here.
    /// </summary>
    public bool UsedGpu { get; init; }

    /// <summary>
    /// Gets the execution provider the ONNX sessions were actually running on when this result was
    /// produced. <see cref="OcrExecutionProvider.Cpu"/> when an accelerator was requested but failed to
    /// attach (see <see cref="Services.StructureService.GpuAccelerationHint"/> for why).
    /// </summary>
    public OcrExecutionProvider ExecutionProvider { get; init; } = OcrExecutionProvider.Cpu;

    /// <summary>
    /// Gets the width (px) of the image OCR ran on, or 0 if unknown. Useful for exporters (hOCR/ALTO) and
    /// for normalizing bounding boxes without having to carry the source image alongside the result.
    /// </summary>
    public int SourceWidth { get; init; }

    /// <summary>
    /// Gets the height (px) of the image OCR ran on, or 0 if unknown.
    /// </summary>
    public int SourceHeight { get; init; }

    /// <summary>
    /// Creates an empty result instance.
    /// </summary>
    public static OcrResult Empty { get; } = new()
    {
        FullText = string.Empty,
        Lines = Array.Empty<OcrLine>(),
        Languages = Array.Empty<string>(),
        Duration = TimeSpan.Zero,
        UsedGpu = false
    };
}
