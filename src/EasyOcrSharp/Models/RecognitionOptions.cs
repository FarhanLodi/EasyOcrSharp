namespace EasyOcrSharp.Models;

/// <summary>
/// How recognized text regions are grouped in the result.
/// </summary>
public enum TextGrouping
{
    /// <summary>One result per raw detected box (roughly per word). No line merging.</summary>
    Word,

    /// <summary>Adjacent boxes on the same line are merged into one result (EasyOCR's default).</summary>
    Line,

    /// <summary>Lines are further merged into paragraph blocks by vertical proximity.</summary>
    Paragraph,
}

/// <summary>
/// Tunable options for a recognition call. Pass to
/// <see cref="Services.EasyOcrService.ExtractTextFromImage(string, System.Collections.Generic.IEnumerable{string}, RecognitionOptions, System.Threading.CancellationToken)"/>.
/// </summary>
public sealed record RecognitionOptions
{
    /// <summary>How detected regions are grouped. Defaults to <see cref="TextGrouping.Line"/>.</summary>
    public TextGrouping Grouping { get; init; } = TextGrouping.Line;

    /// <summary>
    /// Maximum number of text regions recognized concurrently. Defaults to the processor count.
    /// Set to 1 to force sequential recognition.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// When true (default), low-confidence regions are re-recognized with contrast stretching
    /// (EasyOCR's second pass). Disable for a small speed gain on clean, high-contrast input.
    /// </summary>
    public bool AdjustContrast { get; init; } = true;

    /// <summary>
    /// Drop recognized lines whose confidence is below this threshold (0–1). Default 0 (keep all).
    /// </summary>
    public double MinConfidence { get; init; }

    /// <summary>The default options (line grouping, full parallelism, contrast retry on).</summary>
    public static RecognitionOptions Default { get; } = new();
}
