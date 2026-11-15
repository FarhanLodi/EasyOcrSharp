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
    /// Upper bound on the CPU threads the recognition stage uses at once: rectifying the detected regions
    /// into crops, resizing and normalizing crops for the text-line classifier and the recognizer, and — on
    /// the CPU execution provider only — how many recognition batches run concurrently on the shared model
    /// session (at most a small, measured cap, so ONNX Runtime's own intra-op threads are not oversubscribed).
    /// Defaults to the processor count; values ≤ 0 also mean the processor count. Set to 1 to force fully
    /// sequential recognition. Parallelism never changes results: batch composition is fixed.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Number of text boxes fed through the recognizer in a single ONNX run. PaddleOCR's
    /// <c>rec_batch_num</c>. Boxes of similar aspect ratio are batched together for throughput
    /// (especially on GPU). Default 6; values &lt;= 0 fall back to the default. Applied per call —
    /// changing it never reloads the cached recognizer session.
    /// </summary>
    public int BatchSize { get; init; } = 6;

    /// <summary>
    /// Drop recognized lines whose confidence is below this threshold (0–1). PaddleOCR's
    /// <c>drop_score</c> / PaddleX's <c>text_rec_score_thresh</c>. Default 0.0 — everything is returned,
    /// matching the Python OCR pipeline. Deliberate deviation: lines whose text is empty or
    /// whitespace-only are still dropped (Python keeps them), since an empty reading carries no
    /// information for callers.
    /// </summary>
    public double DropScore { get; init; } = 0.0;

    /// <summary>
    /// Run the text-line orientation classifier (180° flip detection) before recognition, rotating boxes
    /// that the classifier marks as upside-down. PaddleOCR's <c>use_textline_orientation</c>. Default true
    /// (the Python pipeline default). When false, the classifier model is never loaded for this call.
    /// <para>
    /// Precedence with <see cref="Services.StructureServiceOptions.UseTextLineOrientation"/>: a value set
    /// explicitly on these options always wins; when it is left at its default, the service-level option
    /// applies if it was set, and otherwise the classifier runs.
    /// </para>
    /// </summary>
    public bool UseTextLineOrientation
    {
        get => _useTextLineOrientation;
        init
        {
            _useTextLineOrientation = value;
            UseTextLineOrientationSpecified = true;
        }
    }

    private readonly bool _useTextLineOrientation = true;

    /// <summary>
    /// True when <see cref="UseTextLineOrientation"/> was assigned explicitly (carried through <c>with</c>
    /// copies), so the engine can let a service-level setting apply to calls that left it at its default.
    /// </summary>
    internal bool UseTextLineOrientationSpecified { get; private init; }

    /// <summary>
    /// Minimum confidence the text-line orientation classifier's 180° label must reach before a crop is
    /// actually flipped. PaddleOCR 2.x's <c>cls_thresh</c>; PaddleX 3.x dropped it and rotates on plain
    /// <c>argmax</c>. Default 0.9 — a deliberate deviation, because an ungated argmax measurably destroys
    /// upright text: on a Devanagari page the classifier flipped most lines (mean recognition confidence
    /// 0.97 with the classifier off versus 0.83 with it on, the affected lines reading as transliterated
    /// gibberish), and the same misfire hit 5 of 17 lines on an upright multi-column English page. The
    /// misfires are low-confidence, so gating removes them while genuine upside-down lines — which score
    /// very close to 1.0 — are still corrected. Set 0 for exact PaddleX 3.x parity.
    /// </summary>
    public double TextLineOrientationThreshold { get; init; } = 0.9;

    /// <summary>
    /// Confirm the text-line orientation classifier's 180° verdicts by recognizing the flagged crop in
    /// both orientations and keeping the more confident reading. Default true — the classifier misfires
    /// on upright lines even at high confidence, and acting on a wrong verdict replaces a clean reading
    /// with gibberish, whereas recognition confidence tells the two apart reliably. Costs one extra
    /// recognition per flagged line only. Set false for PaddleX 3.x behavior (act on the verdict).
    /// </summary>
    public bool VerifyOrientationByRecognition { get; init; } = true;

    /// <summary>
    /// Run the whole-document orientation classifier (PP-LCNet doc-ori, 0/90/180/270°) before detection
    /// and rotate the page upright, so dense rotated scans are read correctly. PaddleX's
    /// <c>use_doc_orientation_classify</c> (part of <c>use_doc_preprocessor</c>) — Python's OCR pipeline
    /// default is on, and so is ours: default <c>true</c>. The classifier model is downloaded/loaded
    /// lazily on first use; if it cannot be obtained, the pipeline logs a warning and proceeds without it.
    /// Applies to detection-based OCR only (full-page or region-of-interest); caller-supplied region
    /// polygons (<c>RecognizeRegionsAsync</c>) are never re-oriented. The detected page rotation is
    /// reported via <see cref="OcrResult.DetectedOrientation"/>, and all returned coordinates are mapped
    /// back to the <b>original</b> image's orientation (a deliberate deviation from Python, which reports
    /// them in the rotated frame).
    /// </summary>
    public bool UseDocOrientation { get; init; } = true;

    /// <summary>
    /// Run the UVDoc document unwarp (dewarp) model on the page before detection — PaddleX's
    /// <c>use_doc_unwarping</c>. <b>Documented deviation:</b> Python's OCR pipeline defaults this to on
    /// (both doc-preprocessor stages true); here it defaults to <c>false</c> for performance, since the
    /// near-full-resolution UVDoc pass is expensive and flat scans/screenshots don't need it. Enable it
    /// for photographed or warped pages. When unwarping ran, returned coordinates stay in the
    /// <b>unwarped</b> frame (there is no closed-form inverse of the dewarp; Python behaves the same).
    /// </summary>
    public bool UseDocUnwarp { get; init; }

    /// <summary>
    /// White border (px) added around each rectified text-line crop before orientation classification and
    /// recognition — some tight detections read better with a little breathing room (10–20 px). Default 0
    /// (no padding). Note: Python PaddleOCR does not pad crops; the parity-true lever for tight boxes is
    /// <see cref="DetectionOptions.UnclipRatio"/> (grow the detected boxes themselves).
    /// </summary>
    public int CropPadding { get; init; } = 0;

    /// <summary>
    /// Locate every word inside each recognized line and return it in <see cref="EasyOcrSharp.Models.OcrLine.Words"/> — Python
    /// PaddleOCR 3.x's <c>return_word_box</c>. Word positions come from the CTC timesteps at which the
    /// recognizer emitted each character, mapped back through the line's rectification onto the page, so a
    /// word box follows the line's slant. Words are split at spaces and every CJK character is its own
    /// word. Default false (<see cref="EasyOcrSharp.Models.OcrLine.Words"/> stays empty); enabling it does not
    /// change any line text, confidence or box.
    /// </summary>
    public bool ReturnWordBoxes { get; init; }

    /// <summary>
    /// Recovers word spaces the recognizer only narrowly dropped. PP-OCRv5 often decides the space between
    /// words — especially between Chinese and Latin/digits, as in <c>结算 Pay 21190</c> — by a few percent
    /// against the CTC blank, so the space can come and go with tiny input changes (resampler, batch tensor
    /// width). When greater than 0, a space is inserted between two recognized characters if the model gave
    /// the space class at least this probability anywhere in the gap between them. Only spaces are ever
    /// added, and line confidences are unchanged. A space is never added at either end of a line, next to an
    /// existing space, inside a number (between digits, or in <c>3838.17</c>, <c>1,280</c>, <c>12:30</c>),
    /// just inside a bracket or between a CJK character and a bracket, before <c>, . : ; ! ? %</c>, around
    /// <c>@</c>, after an apostrophe, or next to CJK/fullwidth punctuation.
    /// <para>
    /// Default 0 (off): recognition matches Python PaddleOCR exactly. <see cref="RecommendedSpaceRecoveryThreshold"/>
    /// (0.15) is the suggested value for mixed Chinese/Latin documents, tables and forms: on 1,100 labelled
    /// synthetic lines it cut the spaces the model dropped from 208 to 34 (exact lines 902 → 1,042) and added
    /// 2 spurious ones. On handwritten or badly degraded text it can also split misread words. Lower values
    /// recover more spaces and add more spurious ones.
    /// </para>
    /// </summary>
    public double SpaceRecoveryThreshold { get; init; }

    /// <summary>
    /// The suggested <see cref="SpaceRecoveryThreshold"/> for printed mixed Chinese/Latin text: 0.15.
    /// </summary>
    public const double RecommendedSpaceRecoveryThreshold = 0.15;

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
    /// The default options (line grouping, full parallelism, drop_score 0.0, text-line orientation on,
    /// document orientation on, document unwarp off).
    /// </summary>
    public static RecognitionOptions Default { get; } = new();
}
