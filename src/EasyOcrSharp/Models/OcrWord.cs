using System;
using System.Collections.Generic;

namespace EasyOcrSharp.Models;

/// <summary>
/// A single whitespace-delimited word inside an <see cref="OcrLine"/>, with geometry derived from the
/// CTC alignment of the recognizer (which timestep emitted which character) rather than estimated from
/// character counts. Only populated when
/// <see cref="RecognitionOptions.WordLevelDetail"/> asks for it.
/// </summary>
public sealed record OcrWord
{
    /// <summary>
    /// Gets the word text. Never empty and never contains whitespace — words are split on whitespace
    /// and the separators themselves are dropped.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets the confidence score (0-1 range): the arithmetic mean of the confidences of the characters
    /// the word is made of. This deliberately differs from the line-level score, which is EasyOCR's
    /// <c>custom_mean</c> over the whole line, so word scores stay comparable to each other.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Gets the word's quadrilateral in source-image coordinates, ordered top-left, top-right,
    /// bottom-right, bottom-left along the line's text direction. For rotated or skewed lines this is a
    /// genuinely rotated quad — it follows the parent line's polygon rather than snapping to the axes.
    /// </summary>
    public IReadOnlyList<OcrPoint> BoundingPolygon { get; init; } = Array.Empty<OcrPoint>();

    /// <summary>
    /// Gets the axis-aligned bounding box computed from <see cref="BoundingPolygon"/>. This is what the
    /// hOCR / ALTO / TSV exporters emit, since those formats only carry axis-aligned rectangles.
    /// </summary>
    public OcrBoundingBox BoundingBox { get; init; } = OcrBoundingBox.Empty;
}

/// <summary>
/// A single recognized character inside an <see cref="OcrLine"/>, with the horizontal extent of the CTC
/// timestep span that produced it. Only populated when
/// <see cref="RecognitionOptions.WordLevelDetail"/> is <see cref="WordLevelDetail.Characters"/>.
/// </summary>
public sealed record OcrChar
{
    /// <summary>
    /// Gets the character. Modelled as a string rather than a <see cref="char"/> because the recognizer
    /// vocabulary is free to contain multi-code-unit entries; in practice this is a single UTF-16 code
    /// unit taken straight from the model's vocabulary.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// Gets the confidence score (0-1 range): the mean softmax probability of the winning class across
    /// the timesteps that emitted this character.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Gets the character's quadrilateral in source-image coordinates, ordered along the line's text
    /// direction (see <see cref="OcrWord.BoundingPolygon"/>). Spans are disjoint and strictly ordered
    /// along the line; they do not necessarily touch, because the timesteps the model decoded as blank
    /// (the gaps between glyphs) belong to no character.
    /// </summary>
    public IReadOnlyList<OcrPoint> BoundingPolygon { get; init; } = Array.Empty<OcrPoint>();

    /// <summary>
    /// Gets the axis-aligned bounding box computed from <see cref="BoundingPolygon"/>.
    /// </summary>
    public OcrBoundingBox BoundingBox { get; init; } = OcrBoundingBox.Empty;
}
