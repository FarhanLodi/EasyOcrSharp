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
/// CTC decoding strategy used to turn the recognizer's per-timestep logits into text. Mirrors
/// EasyOCR's <c>decoder</c> option.
/// </summary>
public enum DecoderType
{
    /// <summary>Per-timestep argmax then collapse repeats (EasyOCR's default; fastest).</summary>
    Greedy,

    /// <summary>CTC prefix beam search — explores several hypotheses, often more accurate on
    /// ambiguous or low-contrast text. Controlled by <see cref="RecognitionOptions.BeamWidth"/>.</summary>
    BeamSearch,

    /// <summary>Beam search constrained to words from <see cref="RecognitionOptions.Dictionary"/>
    /// (a lexicon). Falls back to plain <see cref="BeamSearch"/> when no dictionary is supplied.</summary>
    WordBeamSearch,
}

/// <summary>
/// How much sub-line geometry the recognizer reports. Word and character boxes are read off the CTC
/// alignment — the recognizer already knows which timestep emitted which glyph — so they are true
/// extents rather than the line width split proportionally to character count.
/// </summary>
public enum WordLevelDetail
{
    /// <summary>
    /// Line geometry only (the default): <see cref="OcrLine.Words"/> and
    /// <see cref="OcrLine.Characters"/> stay empty and results are exactly what they have always been.
    /// </summary>
    None,

    /// <summary>Also report <see cref="OcrLine.Words"/> — one entry per whitespace-delimited word.</summary>
    Words,

    /// <summary>
    /// Report <see cref="OcrLine.Words"/> and, in addition, the per-character
    /// <see cref="OcrLine.Characters"/> they were built from.
    /// </summary>
    Characters,
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
    /// The direction in which detected regions are assembled into a reading order. Defaults to
    /// <see cref="TextReadingDirection.Auto"/>, which reads right-to-left when every requested language is
    /// written in a right-to-left script (<c>ar</c>, <c>fa</c>, <c>ur</c>, <c>ug</c>, <c>he</c> …) and
    /// left-to-right otherwise.
    /// </summary>
    /// <remarks>
    /// Set this explicitly for a page whose flow the language codes do not describe — a bilingual
    /// Arabic/English form that reads right-to-left, or a right-to-left language passed through a custom
    /// recognizer under a code this library does not know. See <see cref="TextReadingDirection"/> for the
    /// boundary: this orders <i>boxes</i>, and is not the Unicode Bidirectional Algorithm.
    /// </remarks>
    public TextReadingDirection ReadingDirection { get; init; } = TextReadingDirection.Auto;

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
    /// Confidence below which a box is re-recognized with contrast stretching when
    /// <see cref="AdjustContrast"/> is on. EasyOCR's <c>contrast_ths</c>. Default 0.1.
    /// </summary>
    public double ContrastThreshold { get; init; } = 0.1;

    /// <summary>
    /// Target contrast for the grey-stretch second pass; patches below this contrast are stretched.
    /// EasyOCR's <c>adjust_contrast</c>. Default 0.5.
    /// </summary>
    public double AdjustContrastTarget { get; init; } = 0.5;

    /// <summary>
    /// CTC decoding strategy. <see cref="DecoderType.Greedy"/> (default) is fastest;
    /// <see cref="DecoderType.BeamSearch"/> / <see cref="DecoderType.WordBeamSearch"/> can improve
    /// accuracy on ambiguous text at extra cost. EasyOCR's <c>decoder</c>.
    /// </summary>
    public DecoderType Decoder { get; init; } = DecoderType.Greedy;

    /// <summary>
    /// Beam width used by <see cref="DecoderType.BeamSearch"/> / <see cref="DecoderType.WordBeamSearch"/>.
    /// EasyOCR's <c>beamWidth</c>. Higher = more thorough and slower. Default 5.
    /// </summary>
    public int BeamWidth { get; init; } = 5;

    /// <summary>
    /// Lexicon used by <see cref="DecoderType.WordBeamSearch"/> to constrain output to known words.
    /// Null/empty makes word beam search behave like plain beam search.
    /// </summary>
    public IReadOnlyCollection<string>? Dictionary { get; init; }

    /// <summary>
    /// Angles (degrees) at which each detected box is additionally recognized; the highest-confidence
    /// orientation wins. Use e.g. <c>[90, 180, 270]</c> for images containing rotated/sideways text.
    /// EasyOCR's <c>rotation_info</c>. Null/empty = upright only.
    /// </summary>
    public IReadOnlyList<int>? RotationInfo { get; init; }

    /// <summary>
    /// Number of text boxes fed through the recognizer in a single ONNX run. EasyOCR's
    /// <c>batch_size</c>. 1 (default) recognizes boxes individually (concurrently, see
    /// <see cref="MaxDegreeOfParallelism"/>); larger values batch boxes for throughput on GPU.
    /// If the model cannot run a batch, it transparently falls back to per-box inference.
    /// </summary>
    public int BatchSize { get; init; } = 1;

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

    /// <summary>
    /// Restrict recognition to this exact set of characters (e.g. <c>"0123456789"</c> for an amount,
    /// or <c>"ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"</c> for a plate). Anything outside the set is never
    /// emitted, which sharply improves accuracy on constrained fields. Null = allow every character.
    /// Mutually exclusive with <see cref="Blocklist"/> (allowlist wins if both are set).
    /// </summary>
    public string? Allowlist { get; init; }

    /// <summary>
    /// Forbid these characters from being emitted (e.g. exclude punctuation). Null/empty = no block.
    /// Ignored when <see cref="Allowlist"/> is set.
    /// </summary>
    public string? Blocklist { get; init; }

    /// <summary>
    /// Whether the recognizer also reports per-word (and optionally per-character) geometry on every
    /// line, derived from the CTC alignment. Defaults to <see cref="WordLevelDetail.None"/>, which
    /// leaves <see cref="OcrLine.Words"/>/<see cref="OcrLine.Characters"/> empty exactly as before.
    /// Turning it on adds no extra model inference — only a little arithmetic per decoded character —
    /// and makes the hOCR / ALTO / TSV exporters emit true word boxes instead of an approximation.
    /// Character extents are exact for <see cref="DecoderType.Greedy"/>; the beam-search decoders do not
    /// expose their winning alignment, so their spans are distributed uniformly across the line.
    /// </summary>
    public WordLevelDetail WordLevelDetail { get; init; } = WordLevelDetail.None;

    /// <summary>
    /// Low-level CRAFT detection thresholds. Defaults match EasyOCR; only tweak when text is missed
    /// or over-merged. See <see cref="DetectionOptions"/>.
    /// </summary>
    public DetectionOptions Detection { get; init; } = DetectionOptions.Default;

    /// <summary>
    /// Thresholds controlling how detected boxes are merged into lines and paragraphs. Defaults
    /// preserve EasyOcrSharp's behaviour; see <see cref="GroupingOptions"/>.
    /// </summary>
    public GroupingOptions GroupingOptions { get; init; } = GroupingOptions.Default;

    /// <summary>
    /// Which model family runs this call. Defaults to <see cref="RecognitionEngine.EasyOcr"/> (CRAFT +
    /// CRNN), whose behaviour is unchanged by this option. <see cref="RecognitionEngine.PpOcrV5"/> opts
    /// into PaddleOCR's PP-OCRv5 detector and recognizers — the models behind
    /// <c>AnalyzeDocumentAsync</c> — and returns the same result types.
    /// </summary>
    /// <remarks>
    /// <para>Under <see cref="RecognitionEngine.PpOcrV5"/> the options apply as follows.</para>
    /// <para><b>Honored:</b> <see cref="Grouping"/>, <see cref="ReadingDirection"/>,
    /// <see cref="MinConfidence"/>, <see cref="Region"/>, <see cref="Preprocessing"/> (every step:
    /// denoise / deskew / sharpen / binarize, the 0/90/180/270° <see cref="PreprocessingOptions.DetectOrientation"/>
    /// sweep and the model-based document orientation / unwarp, exactly as on the default engine),
    /// <see cref="AutoDetectLanguage"/> / <see cref="AutoDetectCandidates"/> (each candidate's PP-OCRv5
    /// pack reads every line and the most confident reading wins; a confident Latin/CJK page stops after
    /// the default pack when it is the first candidate), <see cref="Allowlist"/> / <see cref="Blocklist"/>,
    /// <see cref="GroupingOptions"/> for paragraph merging, the engine-neutral detection opt-ins
    /// <see cref="DetectionOptions.TileLargeImages"/> / <see cref="DetectionOptions.MinTextHeight"/> /
    /// <see cref="DetectionOptions.EnhanceContrast"/> (applied to the DB detector), and
    /// <see cref="WordLevelDetail"/> (<see cref="OcrLine.Words"/> are placed from the recognizer's CTC
    /// timesteps; PP-OCRv5 has no per-character boxes, so <see cref="WordLevelDetail.Characters"/> also
    /// returns words only).</para>
    /// <para><b>Adapted:</b> <see cref="BatchSize"/> is PP-OCRv5's <c>rec_batch_num</c>; the default of 1
    /// becomes PP-OCRv5's own default of 6 lines per run. <see cref="MaxDegreeOfParallelism"/> bounds only
    /// <c>ExtractTextStreamAsync</c>'s concurrent chunks — buffered calls recognize batch by batch on ONNX
    /// Runtime's intra-op threads. <see cref="TextGrouping.Word"/> returns the detector's raw boxes, which
    /// are text-line segments rather than single words, and a streamed page yields those boxes one by one
    /// (the buffered <see cref="TextGrouping.Line"/> merge of same-line fragments needs the whole page).
    /// Languages are mapped to PP-OCRv5 packs (<c>en</c>/<c>ch_sim</c>/<c>ch_tra</c>/<c>ja</c> share the default pack;
    /// <c>ko</c>, <c>ar</c>/<c>fa</c>/<c>ur</c>/<c>ug</c>, <c>ru</c>/<c>uk</c>/<c>be</c>, the other
    /// Cyrillic codes, <c>hi</c> and the other Devanagari codes, the Latin-script codes, <c>th</c>,
    /// <c>ta</c> and <c>te</c> each have a script pack); a request spanning several packs reads every
    /// line with each and keeps the most confident reading, while <c>en</c> next to another script needs
    /// no pack of its own because every PP-OCRv5 pack also reads Latin letters.</para>
    /// <para><b>Ignored:</b> the CRAFT thresholds on <see cref="Detection"/> (PP-OCRv5 runs its DB
    /// detector with PaddleOCR's defaults), <see cref="GroupingOptions"/> line-merge thresholds,
    /// <see cref="Decoder"/> / <see cref="BeamWidth"/> / <see cref="Dictionary"/> (PP-OCRv5 decodes
    /// greedily), <see cref="AdjustContrast"/> / <see cref="ContrastThreshold"/> /
    /// <see cref="AdjustContrastTarget"/> (EasyOCR's contrast retry), <see cref="RotationInfo"/>
    /// (PP-OCRv5's text-line orientation classifier corrects upside-down lines instead, each verdict
    /// confirmed by recognizing both orientations).</para>
    /// <para><b>Not supported:</b> <c>bn</c>, <c>as</c>, <c>mni</c> and <c>kn</c> (no PP-OCRv5 pack), <c>vi</c>
    /// (the PP-OCRv5 Latin dictionary has none of Vietnamese's tone-marked vowels)
    /// and custom recognizers registered on <see cref="Services.EasyOcrServiceOptions"/> throw
    /// <see cref="ArgumentException"/>; use the default engine for them.
    /// <see cref="Services.EasyOcrService.WarmUp"/> and <see cref="Services.EasyOcrService.DetectLanguagesAsync(EasyImageSharp.Image{EasyImageSharp.PixelFormats.Rgb24}, IEnumerable{string}, CancellationToken)"/>
    /// always use the default engine.</para>
    /// </remarks>
    public RecognitionEngine Engine { get; init; } = RecognitionEngine.EasyOcr;

    /// <summary>The default options (line grouping, full parallelism, contrast retry on).</summary>
    public static RecognitionOptions Default { get; } = new();
}
