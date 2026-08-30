namespace EasyOcrSharp.Models;

/// <summary>
/// Which table-structure model recovers table regions during document analysis.
/// </summary>
public enum DocumentTableModel
{
    /// <summary>SLANet-plus — one end-to-end model (the default; smallest download).</summary>
    SlanetPlus = 0,

    /// <summary>
    /// SLANeXt (PP-StructureV3 v2 path) — a wired/wireless table classifier picks the matching
    /// SLANeXt model. More accurate on clearly bordered/borderless tables; downloads three extra
    /// models on first use.
    /// </summary>
    SlaNeXt = 1,
}

/// <summary>
/// How nested layout regions are resolved during document analysis. A region counts as nested when at
/// least 90% of its own area falls inside another; formulas are never absorbed by a non-formula region.
/// </summary>
public enum DocumentLayoutMergeMode
{
    /// <summary>Leave nesting alone — both the container and the contained block are returned. The default.</summary>
    None = 0,

    /// <summary>
    /// Keep them all. Behaves exactly like <see cref="None"/>; named so the three strategies can be spelled
    /// out explicitly.
    /// </summary>
    Union = 1,

    /// <summary>
    /// Keep the enclosing block: every region contained by another is dropped — a table instead of the table
    /// plus its inner text regions.
    /// </summary>
    Large = 2,

    /// <summary>
    /// Keep the inner blocks: a region survives when it contains nothing, or is itself contained by another.
    /// </summary>
    Small = 3,
}

/// <summary>
/// Which source decides the reading order of the analyzed blocks.
/// </summary>
public enum DocumentReadingOrder
{
    /// <summary>
    /// The model's own predicted order when the layout model emits one (PP-DocLayoutV3 does), otherwise the
    /// geometric XY-cut orderer. The default.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Always use the geometric XY-cut orderer, ignoring any order the model predicted — keeps ordering
    /// identical across layout models.
    /// </summary>
    XyCut = 1,

    /// <summary>
    /// Prefer the model's predicted order; identical to <see cref="Auto"/>, since a model that predicts no
    /// order leaves nothing to use but XY-cut.
    /// </summary>
    Model = 2,
}

/// <summary>
/// Per-call configuration for
/// <see cref="Services.EasyOcrService.AnalyzeDocumentAsync(string, DocumentAnalysisOptions?, CancellationToken)"/> —
/// document-structure analysis (layout regions, tables, formulas, seals, reading order), powered by
/// PaddleOCR's PP-StructureV3 models via the built-in structure engine. All models download on demand and
/// are SHA256-verified.
/// </summary>
public sealed record DocumentAnalysisOptions
{
    /// <summary>
    /// Correct whole-page orientation (0/90/180/270°) with the document-orientation classifier before
    /// layout detection. Default false.
    /// </summary>
    public bool DocumentOrientation { get; init; }

    /// <summary>
    /// Dewarp a curved/folded page (UVDoc) before layout detection. Default false.
    /// </summary>
    public bool DocumentUnwarp { get; init; }

    /// <summary>Recognize the structure (HTML) of detected table regions. Default true.</summary>
    public bool RecognizeTables { get; init; } = true;

    /// <summary>Recognize the LaTeX of detected formula regions. Default true.</summary>
    public bool RecognizeFormulas { get; init; } = true;

    /// <summary>Recognize the text of detected seal (stamp) regions. Default true.</summary>
    public bool RecognizeSeals { get; init; } = true;

    /// <summary>
    /// Which table-structure model recovers table regions. Default
    /// <see cref="DocumentTableModel.SlanetPlus"/>. Only consulted when
    /// <see cref="RecognizeTables"/> is true.
    /// </summary>
    public DocumentTableModel TableModel { get; init; } = DocumentTableModel.SlanetPlus;

    /// <summary>
    /// Confidence floor (0–1) for layout detections: a region whose score is at or below this is discarded.
    /// Default 0.5, the floor the PP-DocLayout models ship in their own configs. Lower it to keep regions
    /// the detector is unsure about (at the cost of false positives), raise it to keep only confident ones.
    /// </summary>
    public float LayoutScoreThreshold
    {
        get => _layoutScoreThreshold;
        init
        {
            // NaN would be especially bad silently: `score <= float.NaN` is false, so every one of the
            // detector's 300 top-k candidates would survive and be run through OCR/table/formula recognition.
            if (float.IsNaN(value) || value < 0f || value > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "LayoutScoreThreshold must be a number in [0, 1].");
            }
            _layoutScoreThreshold = value;
        }
    }

    private readonly float _layoutScoreThreshold = DefaultLayoutScoreThreshold;

    /// <summary>The default <see cref="LayoutScoreThreshold"/> (0.5).</summary>
    public const float DefaultLayoutScoreThreshold = 0.5f;

    /// <summary>
    /// Drop near-duplicate layout regions. The layout detectors emit a fixed top-k of candidates with no
    /// NMS, so the same area of the page is routinely proposed several times under different labels; this
    /// collapses each cluster to one region and drops sub-6px slivers. Barely visible at the default
    /// <see cref="LayoutScoreThreshold"/> and increasingly important as you lower it. Default true.
    /// </summary>
    public bool FilterOverlappingRegions { get; init; } = true;

    /// <summary>
    /// Additionally run non-maximum suppression over the layout regions (0.6 IoU within a class, 0.98
    /// across classes). Complements <see cref="FilterOverlappingRegions"/>, which measures overlap against
    /// the smaller box rather than the union. Default false.
    /// </summary>
    public bool LayoutNms { get; init; }

    /// <summary>
    /// Grow every layout region about its own centre by this ratio before recognition (1.1 adds 10% to the
    /// width and height); expanded regions are re-clamped to the page. Useful when tight boxes clip glyphs
    /// out of the crops handed to the table and formula recognizers. Default null (no expansion).
    /// </summary>
    public float? LayoutUnclipRatio
    {
        get => _layoutUnclipRatio;
        init
        {
            // A ratio below 1 would *shrink* every region — the opposite of what this option documents, and
            // it would clip glyphs out of the very crops it exists to protect.
            if (value is { } r && (float.IsNaN(r) || r < 1f))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "LayoutUnclipRatio must be a number >= 1 (1.0 changes nothing), or null for no expansion.");
            }
            _layoutUnclipRatio = value;
        }
    }

    private readonly float? _layoutUnclipRatio;

    /// <summary>
    /// How nested layout regions are resolved. Default <see cref="DocumentLayoutMergeMode.None"/>.
    /// </summary>
    public DocumentLayoutMergeMode LayoutMergeMode { get; init; } = DocumentLayoutMergeMode.None;

    /// <summary>
    /// Which source decides the reading order of the returned blocks. Default
    /// <see cref="DocumentReadingOrder.Auto"/>.
    /// </summary>
    public DocumentReadingOrder ReadingOrder { get; init; } = DocumentReadingOrder.Auto;

    /// <summary>
    /// The direction in which blocks and lines are assembled into a reading order. Defaults to
    /// <see cref="TextReadingDirection.Auto"/>, which reads right-to-left when every code in
    /// <see cref="Languages"/> names a right-to-left script (<c>ar</c>, <c>fa</c>, <c>ur</c>, <c>ug</c>,
    /// <c>he</c> …) and left-to-right otherwise.
    /// </summary>
    /// <remarks>
    /// Affects the geometric XY-cut ordering. When the layout model emits its own reading-order index and
    /// <see cref="ReadingOrder"/> leaves that in use, the model's prediction wins — it was trained on the
    /// document's real order and already accounts for direction.
    /// </remarks>
    public TextReadingDirection ReadingDirection { get; init; } = TextReadingDirection.Auto;

    /// <summary>
    /// Recognition language codes for text inside the document (PaddleOCR codes, e.g. <c>"en"</c>,
    /// <c>"ch"</c>, <c>"fr"</c>, <c>"ru"</c>, <c>"ar"</c>, or <c>"auto"</c> to auto-detect). Null or
    /// empty uses the analyzer's default pack, which covers Chinese + English + Japanese. Codes that
    /// don't map to a known pack are skipped with a warning.
    /// </summary>
    public IReadOnlyList<string>? Languages { get; init; }

    /// <summary>The default analysis options (tables, formulas and seals on; no page correction).</summary>
    public static DocumentAnalysisOptions Default { get; } = new();
}
