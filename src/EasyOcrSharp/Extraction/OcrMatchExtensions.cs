using System.Text.RegularExpressions;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Extraction;

/// <summary>
/// Finds values (e-mails, dates, amounts, IBANs, your own expressions, …) in an <see cref="OcrResult"/>
/// and reports where each one sits on the page. Pure and model-free: works on any result, including one
/// deserialized from JSON or built by hand.
/// </summary>
/// <remarks>
/// Complements <see cref="FieldExtractionExtensions"/>: field extraction answers "what value sits next to
/// the label <i>Total</i>?", pattern matching answers "which IBANs, dates and amounts are on this page, and
/// where?" — no label required.
/// </remarks>
public static class OcrMatchExtensions
{
    /// <summary>
    /// Finds every match of <paramref name="pattern"/> in the result's lines, in line order. Each match is
    /// reported with <see cref="OcrMatchKind.Custom"/> and no normalized form.
    /// </summary>
    /// <param name="result">The OCR result to search.</param>
    /// <param name="pattern">The expression matched against each line's text.</param>
    /// <returns>The matches, ordered by line and then by position within the line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> or <paramref name="pattern"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<OcrMatch> FindMatches(this OcrResult result, Regex pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return FindMatches(result, new OcrPattern(pattern));
    }

    /// <summary>
    /// Finds every validated match of <paramref name="pattern"/> (for example <see cref="OcrPatterns.Iban"/>)
    /// in the result's lines, in line order.
    /// </summary>
    /// <param name="result">The OCR result to search.</param>
    /// <param name="pattern">The pattern to apply.</param>
    /// <returns>The matches, ordered by line and then by position within the line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> or <paramref name="pattern"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<OcrMatch> FindMatches(this OcrResult result, OcrPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(pattern);

        var matches = new List<OcrMatch>();
        Collect(result, pattern, matches);
        return matches;
    }

    /// <summary>
    /// Applies several patterns (for example <see cref="OcrPatterns.All"/>) and returns their matches merged
    /// in page order. Overlapping matches from different patterns are all kept.
    /// </summary>
    /// <param name="result">The OCR result to search.</param>
    /// <param name="patterns">The patterns to apply.</param>
    /// <returns>The matches, ordered by line, position within the line, and longest first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> or <paramref name="patterns"/> (or an element of it) is <see langword="null"/>.</exception>
    public static IReadOnlyList<OcrMatch> FindMatches(this OcrResult result, IEnumerable<OcrPattern> patterns)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(patterns);

        var matches = new List<OcrMatch>();
        foreach (var pattern in patterns)
        {
            ArgumentNullException.ThrowIfNull(pattern, nameof(patterns));
            Collect(result, pattern, matches);
        }

        return matches
            .OrderBy(m => m.LineIndex)
            .ThenBy(m => m.Index)
            .ThenByDescending(m => m.Length)
            .ToList();
    }

    private static void Collect(OcrResult result, OcrPattern pattern, List<OcrMatch> matches)
    {
        for (int i = 0; i < result.Lines.Count; i++)
        {
            var line = result.Lines[i];
            if (string.IsNullOrEmpty(line.Text)) continue;

            foreach (Match match in pattern.Regex.Matches(line.Text))
            {
                if (match.Length == 0) continue;
                if (pattern.Resolve(match) is not { } resolution) continue;

                int length = Math.Clamp(resolution.Length, 1, match.Length);
                matches.Add(new OcrMatch
                {
                    Value = line.Text.Substring(match.Index, length),
                    Normalized = resolution.Normalized,
                    Kind = pattern.Kind,
                    Line = line,
                    LineIndex = i,
                    Index = match.Index,
                    Length = length,
                    Confidence = line.Confidence,
                    BoundingBox = TextGeometry.EstimateRangeBox(line, match.Index, length),
                });
            }
        }
    }
}
