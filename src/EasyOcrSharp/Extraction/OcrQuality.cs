using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Extraction;

/// <summary>
/// Summary confidence statistics for an OCR result, from
/// <see cref="OcrQualityExtensions.GetQuality(OcrResult, double)"/>. Use it to route documents: accept
/// automatically, send to human review, or re-scan.
/// </summary>
public sealed record OcrQuality
{
    /// <summary>Number of recognized lines.</summary>
    public int LineCount { get; init; }

    /// <summary>Number of non-whitespace characters across all lines.</summary>
    public int CharacterCount { get; init; }

    /// <summary>Mean of the per-line confidences (0–1); 0 when there are no lines.</summary>
    public double MeanConfidence { get; init; }

    /// <summary>
    /// Mean confidence weighted by each line's non-whitespace character count, so long lines count more
    /// than short fragments (0–1); 0 when there are no lines. A line with no characters weighs 1.
    /// </summary>
    public double CharacterWeightedConfidence { get; init; }

    /// <summary>The lowest line confidence (0–1); 0 when there are no lines.</summary>
    public double MinConfidence { get; init; }

    /// <summary>Number of lines whose confidence is below <see cref="LowConfidenceThreshold"/>.</summary>
    public int LowConfidenceLineCount { get; init; }

    /// <summary>
    /// Number of non-whitespace characters in the lines counted by <see cref="LowConfidenceLineCount"/> —
    /// how much text a reviewer would have to re-read.
    /// </summary>
    public int LowConfidenceCharacterCount { get; init; }

    /// <summary><see cref="LowConfidenceLineCount"/> divided by <see cref="LineCount"/>; 0 when there are no lines.</summary>
    public double LowConfidenceRatio { get; init; }

    /// <summary>The threshold the low-confidence counts were computed with.</summary>
    public double LowConfidenceThreshold { get; init; }
}

/// <summary>
/// Confidence-based quality helpers for <see cref="OcrResult"/>: page-level statistics and the lines a
/// human should double-check. Pure and model-free.
/// </summary>
/// <remarks>
/// Line confidences are EasyOCR's own scores — the same values <see cref="RecognitionOptions.MinConfidence"/>
/// filters on — so a threshold tuned here transfers directly to that option.
/// </remarks>
public static class OcrQualityExtensions
{
    /// <summary>
    /// Computes confidence statistics for the result.
    /// </summary>
    /// <param name="result">The OCR result.</param>
    /// <param name="lowConfidenceThreshold">Lines below this confidence (0–1) count as low-confidence. Default 0.8.</param>
    /// <returns>The statistics; all zero (apart from the threshold) for a result with no lines.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lowConfidenceThreshold"/> is outside 0–1.</exception>
    public static OcrQuality GetQuality(this OcrResult result, double lowConfidenceThreshold = 0.8)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateThreshold(lowConfidenceThreshold, nameof(lowConfidenceThreshold));

        var lines = result.Lines;
        if (lines.Count == 0)
        {
            return new OcrQuality { LowConfidenceThreshold = lowConfidenceThreshold };
        }

        double sum = 0, min = double.MaxValue;
        int low = 0, characters = 0, lowCharacters = 0;
        foreach (var line in lines)
        {
            int count = TextGeometry.CountCharacters(line.Text);
            sum += line.Confidence;
            min = Math.Min(min, line.Confidence);
            characters += count;
            if (line.Confidence < lowConfidenceThreshold)
            {
                low++;
                lowCharacters += count;
            }
        }

        return new OcrQuality
        {
            LineCount = lines.Count,
            CharacterCount = characters,
            MeanConfidence = sum / lines.Count,
            CharacterWeightedConfidence = TextGeometry.WeightedConfidence(lines),
            MinConfidence = min,
            LowConfidenceLineCount = low,
            LowConfidenceCharacterCount = lowCharacters,
            LowConfidenceRatio = (double)low / lines.Count,
            LowConfidenceThreshold = lowConfidenceThreshold,
        };
    }

    /// <summary>
    /// Returns the lines whose confidence is below <paramref name="threshold"/>, in their original reading
    /// order — the lines worth a human look.
    /// </summary>
    /// <param name="result">The OCR result.</param>
    /// <param name="threshold">Confidence (0–1) below which a line needs review. Default 0.8.</param>
    /// <returns>The low-confidence lines; empty when every line meets the threshold.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is outside 0–1.</exception>
    public static IReadOnlyList<OcrLine> GetLinesForReview(this OcrResult result, double threshold = 0.8)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateThreshold(threshold, nameof(threshold));
        return result.Lines.Where(l => l.Confidence < threshold).ToList();
    }

    private static void ValidateThreshold(double threshold, string paramName)
    {
        if (double.IsNaN(threshold) || threshold < 0 || threshold > 1)
        {
            throw new ArgumentOutOfRangeException(paramName, threshold, "The threshold must be between 0 and 1.");
        }
    }
}
