namespace EasyOcrSharp.Structure.Engine.Models;

/// <summary>
/// Tunable options for a recognition call. Pass to
/// <see cref="Services.IStructureService"/>.
/// </summary>
internal sealed record RecognitionOptions
{
    /// <summary>
    /// How detected regions are grouped. Defaults to <see cref="TextGrouping.Line"/>.
    /// </summary>
    public TextGrouping Grouping { get; init; } = TextGrouping.Line;

    /// <summary>
    /// Maximum number of text regions recognized concurrently. Defaults to the processor count.
    /// Set to 1 to force sequential recognition.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Number of text boxes fed through the recognizer in a single ONNX run. PaddleOCR's
    /// <c>rec_batch_num</c>. Boxes of similar aspect ratio are batched together for throughput
    /// (especially on GPU). Default 6. If the model cannot run a batch, it falls back to per-box inference.
    /// </summary>
    public int BatchSize { get; init; } = 6;

    /// <summary>
    /// Drop recognized lines whose confidence is below this threshold (0–1). PaddleOCR's <c>drop_score</c>.
    /// Default 0.5.
    /// </summary>
    public double DropScore { get; init; } = 0.5;

    /// <summary>
    /// Run the text-line orientation classifier (180° flip detection) before recognition, rotating boxes
    /// that the classifier marks as upside-down. PaddleOCR's <c>use_textline_orientation</c>. Default false.
    /// When false, the classifier model is never loaded.
    /// </summary>
    public bool UseTextLineOrientation { get; init; }

    /// <summary>
    /// Automatically detect the script/language of the image instead of trusting the requested language
    /// code(s). When enabled, the detected text crops are recognized with every candidate pack in
    /// <see cref="AutoDetectCandidates"/> and, per crop, the highest-confidence reading is kept; the pack
    /// that wins the most crops (weighted by confidence) is reported in
    /// <see cref="OcrResult.DetectedLanguages"/>. The needed recognizer models are auto-downloaded on
    /// demand. Default false.
    /// <para>
    /// Passing the literal language code <c>"auto"</c> to an
    /// <see cref="Services.IStructureService"/>
    /// overload is an equivalent trigger and sets this behaviour without having to construct options.
    /// </para>
    /// <para>
    /// A fast path avoids downloading every candidate for clean Latin/CJK pages: the default PP-OCRv5
    /// recognizer is tried first and, if its mean confidence is high and the text is dominantly Latin or CJK,
    /// it is accepted and the other candidates are skipped.
    /// </para>
    /// </summary>
    public bool AutoDetectLanguage { get; init; }

    /// <summary>
    /// The shortlist of language codes whose recognizer packs are tried during
    /// <see cref="AutoDetectLanguage">auto-detection</see>. Each code is resolved to a recognizer pack and
    /// auto-downloaded on demand. <c>null</c> (the default) uses a curated cross-script shortlist: the
    /// default PP-OCRv5 pack plus <c>latin, cyrillic, arabic, devanagari, korean, japan, thai, greek,
    /// telugu, tamil</c>. Provide your own list to narrow the search (fewer downloads, faster) when the set
    /// of possible scripts is known.
    /// </summary>
    public IReadOnlyList<string>? AutoDetectCandidates { get; init; }

    /// <summary>
    /// Restrict recognition to this exact set of dictionary tokens (e.g.
    /// <c>["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"]</c> for an amount). Anything outside the set is
    /// never emitted, which sharply improves accuracy on constrained fields. Each entry is one dictionary
    /// token — usually a single character, but matched as a whole string so multi-character tokens (some
    /// PaddleOCR dicts have them) work too. Null/empty = allow every character.
    /// <para>
    /// The CTC blank is always kept (decoding requires it), and the space class stays available so words can
    /// still be separated even if you omit space from the allowlist — add space to <see cref="Blocklist"/> to
    /// suppress it. <see cref="Blocklist"/> is still honored when an allowlist is set and takes precedence on
    /// conflict (a token in both lists is blocked).
    /// </para>
    /// <para>
    /// Use the <see cref="FromCharacters(string)"/> helper to build a list from a flat string such as
    /// <c>"0123456789"</c>.
    /// </para>
    /// </summary>
    public IReadOnlyCollection<string>? Allowlist { get; init; }

    /// <summary>
    /// Forbid these dictionary tokens from being emitted (e.g. exclude punctuation). Each entry is one
    /// dictionary token, matched as a whole string. Null/empty = no block. Applies whether or not
    /// <see cref="Allowlist"/> is set, and wins over the allowlist on conflict (the CTC blank is never
    /// blocked). Use <see cref="FromCharacters(string)"/> to build a list from a flat string.
    /// </summary>
    public IReadOnlyCollection<string>? Blocklist { get; init; }

    /// <summary>
    /// Convenience helper that explodes a flat string of single characters (e.g. <c>"0123456789"</c>) into the
    /// per-token list shape expected by <see cref="Allowlist"/> / <see cref="Blocklist"/>. Returns an empty
    /// list for null/empty input. Use this for the common single-character case; build the list directly when
    /// you need multi-character dictionary tokens.
    /// </summary>
    /// <param name="characters">The characters to include, one token per character.</param>
    /// <returns>One single-character string per input character.</returns>
    public static IReadOnlyList<string> FromCharacters(string? characters)
    {
        if (string.IsNullOrEmpty(characters))
            return Array.Empty<string>();

        // Enumerate by Rune, not by char: a dictionary token is a whole character, so splitting a surrogate
        // pair here would produce two lone-surrogate tokens that match no vocabulary entry — the allowlist
        // would then exclude the very character the caller asked to allow (e.g. "𠮷", U+20BB7).
        var list = new List<string>(characters.Length);
        foreach (var rune in characters.EnumerateRunes())
            list.Add(rune.ToString());
        return list;
    }

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
    /// Low-level DB detection thresholds. Defaults match PaddleOCR; only tweak when text is missed
    /// or over-merged. See <see cref="DetectionOptions"/>.
    /// </summary>
    public DetectionOptions Detection { get; init; } = DetectionOptions.Default;

    /// <summary>
    /// The default options (line grouping, full parallelism, drop_score 0.5).
    /// </summary>
    public static RecognitionOptions Default { get; } = new();
}
