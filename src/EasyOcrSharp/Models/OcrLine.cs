using System.Collections.Generic;

namespace EasyOcrSharp.Models;

/// <summary>
/// Represents a single line recognized in the OCR result.
/// </summary>
public sealed record OcrLine
{
    /// <summary>
    /// Gets the recognized text.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets the confidence score (0-1 range).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Gets the coordinates of the bounding polygon associated with the text line.
    /// </summary>
    public IReadOnlyList<OcrPoint> BoundingPolygon { get; init; } = Array.Empty<OcrPoint>();

    /// <summary>
    /// Gets the axis-aligned bounding box computed from the polygon.
    /// </summary>
    public OcrBoundingBox BoundingBox { get; init; } = OcrBoundingBox.Empty;

    /// <summary>
    /// Gets the individual words of this line with their own geometry, in reading order. Empty unless
    /// <see cref="RecognitionOptions.WordLevelDetail"/> was set to <see cref="WordLevelDetail.Words"/>
    /// or <see cref="WordLevelDetail.Characters"/> — word geometry costs nothing extra to compute but
    /// is opt-in so existing results stay byte-for-byte what they were.
    /// </summary>
    public IReadOnlyList<OcrWord> Words { get; init; } = Array.Empty<OcrWord>();

    /// <summary>
    /// Gets the individual characters of this line with their own geometry, in reading order. Empty
    /// unless <see cref="RecognitionOptions.WordLevelDetail"/> was set to
    /// <see cref="WordLevelDetail.Characters"/>. Includes the whitespace characters that separate the
    /// <see cref="Words"/>, so concatenating <see cref="OcrChar.Value"/> reproduces <see cref="Text"/>.
    /// </summary>
    public IReadOnlyList<OcrChar> Characters { get; init; } = Array.Empty<OcrChar>();
}

