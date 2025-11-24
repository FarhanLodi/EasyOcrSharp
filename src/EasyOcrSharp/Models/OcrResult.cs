using System;
using System.Collections.Generic;

namespace EasyOcrSharp.Models;

/// <summary>
/// Represents the result of an OCR operation.
/// </summary>
public sealed record OcrResult
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
    /// Gets the duration of the OCR operation.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Gets a value indicating whether GPU acceleration was used.
    /// </summary>
    public bool UsedGpu { get; init; }

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
