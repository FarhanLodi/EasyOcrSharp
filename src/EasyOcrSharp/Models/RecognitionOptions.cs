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

    /// <summary>
    /// Restrict OCR to a rectangular sub-region of the image (e.g. only the bottom banner of a sign).
    /// When set, detection and recognition run only inside this region, which is also faster.
    /// Recognized bounding boxes are reported in the original image's coordinates. Null = whole image.
    /// </summary>
    public OcrRegion? Region { get; init; }

    /// <summary>
    /// Image clean-up (deskew, orientation correction, binarize, denoise) applied before OCR.
    /// Defaults to <see cref="PreprocessingOptions.None"/>.
    /// </summary>
    public PreprocessingOptions Preprocessing { get; init; } = PreprocessingOptions.None;

    /// <summary>
    /// When true, the script/language is detected automatically and the <c>languages</c> argument is
    /// ignored. Detection samples a few regions across <see cref="AutoDetectCandidates"/> and keeps the
    /// best-scoring packs. Note: the candidate models are downloaded on first use.
    /// </summary>
    public bool AutoDetectLanguage { get; init; }

    /// <summary>
    /// Candidate language codes considered during <see cref="AutoDetectLanguage"/>. Null uses a
    /// built-in common set (Latin, Cyrillic, Chinese, Japanese, Korean). Widen it to include heavier
    /// scripts (e.g. "ar", "hi") when you expect them.
    /// </summary>
    public IReadOnlyList<string>? AutoDetectCandidates { get; init; }

    /// <summary>The default options (line grouping, full parallelism, contrast retry on).</summary>
    public static RecognitionOptions Default { get; } = new();
}
