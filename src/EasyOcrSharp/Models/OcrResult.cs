using System;
using System.Collections.Generic;

namespace EasyOcrSharp.Models;

/// <summary>
/// Represents the result of an OCR operation.
/// </summary>
public sealed record OcrResult
{
    /// <summary>
    /// Gets the concatenated text extracted from the image, one <see cref="Lines"/> entry per line joined
    /// with a single line feed (<c>\n</c>) on every platform.
    /// </summary>
    /// <remarks>
    /// LF, not <see cref="Environment.NewLine"/>: paragraph grouping joins merged lines with <c>\n</c> and the
    /// multi-frame and PDF aggregates join pages with <c>\n\n</c>, so a platform-dependent separator here
    /// produced a single string carrying two different line endings on Windows. It also inflated
    /// <c>TextAccuracyMetrics.CharacterErrorRate</c> by one spurious insertion per line break whenever the
    /// ground truth was read from an LF file.
    /// </remarks>
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
    /// Gets the duration of the OCR operation.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Gets a value indicating whether GPU acceleration was used.
    /// </summary>
    public bool UsedGpu { get; init; }

    /// <summary>
    /// Gets the width (px) of the image OCR ran on, or 0 if unknown. Useful for exporters (hOCR/ALTO) and
    /// for normalizing bounding boxes without having to carry the source image alongside the result.
    /// </summary>
    public int SourceWidth { get; init; }

    /// <summary>Gets the height (px) of the image OCR ran on, or 0 if unknown.</summary>
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
