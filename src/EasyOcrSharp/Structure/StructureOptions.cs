using EasyOcrSharp.Structure.Engine.Models;

namespace EasyOcrSharp.Structure;

/// <summary>
/// Per-call configuration for <see cref="StructureEngine.AnalyzeAsync"/> /
/// <see cref="Engine.Services.IStructureService"/>'s document analysis. Controls document pre-processing
/// (orientation / unwarp), which sub-recognizers run (tables, formulas, seals), the layout model, and the
/// recognition language list passed through to the text recognizer.
/// </summary>
internal sealed record StructureOptions
{
    /// <summary>
    /// Run whole-document orientation correction (0/90/180/270°) before layout detection. Default false.
    /// </summary>
    public bool UseDocOrientation { get; init; }

    /// <summary>
    /// Run document unwarping (UVDoc dewarp) before layout detection. Default false.
    /// </summary>
    public bool UseUnwarp { get; init; }

    /// <summary>
    /// Recognize the structure (HTML) of detected table regions. Default true.
    /// </summary>
    public bool RecognizeTables { get; init; } = true;

    /// <summary>
    /// Recognize the LaTeX of detected formula regions. Default true.
    /// </summary>
    public bool RecognizeFormulas { get; init; } = true;

    /// <summary>
    /// Recognize the text of detected seal regions. Default true.
    /// </summary>
    public bool RecognizeSeals { get; init; } = true;

    /// <summary>
    /// Which layout-detection model to use. Default <see cref="LayoutModel.RtDetrL"/> — the RT-DETR slot is
    /// served by the hosted PP-DocLayoutV3 model (the PicoDet S/M variants are not hosted yet).
    /// </summary>
    public LayoutModel LayoutModel { get; init; } = LayoutModel.RtDetrL;

    /// <summary>
    /// Confidence floor (0-1) for layout detections: a region whose model score is at or below this is
    /// discarded. Default 0.5 — the confidence floor both PP-DocLayoutV3 and PP-DocLayout-S/M ship in
    /// their own model configs. Lower it to keep faint
    /// regions the detector is unsure about (at the cost of false positives), raise it to keep only
    /// confident ones. Applies to whichever <see cref="LayoutModel"/> is selected.
    /// <para>
    /// This is the <i>global</i> floor: classes named in <see cref="LayoutClassThresholds"/> (or, when that
    /// dictionary is null, in the built-in PP-StructureV3 per-class defaults) use their own floor instead —
    /// see <see cref="LayoutClassThresholds"/>.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Every kept region carries its own <see cref="LayoutRegion.Score"/>, so callers that prefer to filter
    /// themselves can set this low and post-filter per block type.
    /// </remarks>
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

    /// <summary>
    /// The default <see cref="LayoutScoreThreshold"/> (0.5), matching the shipped layout model configs.
    /// </summary>
    public const float DefaultLayoutScoreThreshold = 0.5f;

    /// <summary>
    /// Per-class confidence floors keyed by the model's own (paddle) label name — e.g.
    /// <c>"paragraph_title"</c>, <c>"text"</c>, <c>"seal"</c> — overriding
    /// <see cref="LayoutScoreThreshold"/> for the named classes; classes absent from the dictionary keep
    /// using the global threshold. Mirrors PaddleX's dict-typed <c>threshold</c>
    /// (<c>object_detection/processors.py</c>, <c>DetPostProcess.apply</c>).
    /// <para>
    /// Default <c>null</c>: the engine applies the PP-StructureV3 pipeline's per-class defaults
    /// (<c>PP-StructureV3.yaml</c>) — <c>paragraph_title</c> 0.3, <c>text</c> 0.4, <c>formula</c> 0.3
    /// (also applied to the PP-DocLayoutV3 vocabulary's <c>display_formula</c> / <c>inline_formula</c>) and
    /// <c>seal</c> 0.45; every other class uses <see cref="LayoutScoreThreshold"/>, exactly as the yaml's
    /// remaining classes all sit at its 0.5 global. These defaults are active for <b>all</b> layout models.
    /// Pass an empty dictionary to disable the per-class defaults and threshold every class at
    /// <see cref="LayoutScoreThreshold"/> alone. Label names are matched case/format-insensitively
    /// (normalized to lower-case <c>snake_case</c>).
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, float>? LayoutClassThresholds { get; init; }

    /// <summary>
    /// Drop overlapping layout regions before returning them, mirroring PaddleX's
    /// <c>remove_overlap_blocks</c> (<c>layout_parsing/utils.py</c>, PP-StructureV3 runs it with
    /// <c>threshold=0.5, smaller=True</c>): when two regions overlap by more than 50% of the
    /// <b>smaller</b> region's area, the smaller region is dropped — except when exactly one of the pair is
    /// labelled <c>image</c>, in which case the image region loses regardless of size (a text/table block
    /// detected on top of a picture wins over the picture). Default true.
    /// </summary>
    public bool FilterOverlappingRegions { get; init; } = true;

    /// <summary>
    /// Run non-maximum suppression over the layout regions, suppressing a lower-scoring region that
    /// overlaps a kept one by more than 0.6 IoU when both share a class, or 0.98 when they do not.
    /// Complements <see cref="FilterOverlappingRegions"/>, which measures overlap against the smaller box
    /// rather than the union. Default <b>true</b> — the PP-StructureV3 pipeline config ships
    /// <c>layout_nms: True</c> for its layout model.
    /// </summary>
    public bool LayoutNms { get; init; } = true;

    /// <summary>
    /// Grow every layout region about its own centre by this ratio before recognition: 1.1 adds 10% to the
    /// width and height, 1.0 changes nothing. Useful when tight boxes clip ascenders/descenders out of the
    /// crops handed to the table and formula recognizers. Expanded regions are re-clamped to the page.
    /// Default <c>null</c> (no expansion).
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
    /// How nested layout regions are resolved <i>uniformly across all classes</i> — keep the enclosing
    /// block, the inner blocks, or both. Default <see cref="LayoutMergeMode.None"/>, which (together with a
    /// null <see cref="LayoutClassMergeModes"/>) activates the PP-StructureV3 <i>per-class</i> defaults
    /// instead — see <see cref="LayoutClassMergeModes"/>. Set <see cref="LayoutMergeMode.Union"/> to
    /// explicitly keep every nested region, or <see cref="LayoutMergeMode.Large"/> /
    /// <see cref="LayoutMergeMode.Small"/> to force one uniform strategy. Ignored when
    /// <see cref="LayoutClassMergeModes"/> is non-null.
    /// </summary>
    public LayoutMergeMode LayoutMergeMode { get; init; } = LayoutMergeMode.None;

    /// <summary>
    /// Per-class containment-merge strategies keyed by the model's own (paddle) label name, mirroring
    /// PaddleX's dict-typed <c>layout_merge_bboxes_mode</c>: for a class mapped to
    /// <see cref="LayoutMergeMode.Large"/> every region contained (≥ 90% of its own area) inside a region
    /// of that class is dropped; for <see cref="LayoutMergeMode.Small"/> a region containing a region of
    /// that class is dropped unless it is itself contained; <see cref="LayoutMergeMode.Union"/> (or absence
    /// from the dictionary) leaves the class's nesting alone.
    /// <para>
    /// Default <c>null</c>: when <see cref="LayoutMergeMode"/> is also left at
    /// <see cref="LayoutMergeMode.None"/> the engine applies the PP-StructureV3 pipeline defaults
    /// (<c>PP-StructureV3.yaml</c>) — <c>"large"</c> for <c>paragraph_title</c>, <c>image</c>,
    /// <c>formula</c> (also applied to <c>display_formula</c> / <c>inline_formula</c>) and <c>chart</c>,
    /// union for every other class — for <b>all</b> layout models. Pass an empty dictionary to disable
    /// containment merging entirely. Label names are matched case/format-insensitively (normalized to
    /// lower-case <c>snake_case</c>).
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, LayoutMergeMode>? LayoutClassMergeModes { get; init; }

    /// <summary>
    /// Which source decides the reading order written into <see cref="StructureBlock.Order"/>. Default
    /// <see cref="LayoutReadingOrder.Auto"/> resolves to <see cref="LayoutReadingOrder.XyCutEnhanced"/>
    /// (the XY-Cut++ orderer PP-StructureV3 uses). Set <see cref="LayoutReadingOrder.Model"/> to trust the
    /// layout model's own predicted order (PP-DocLayoutV3 emits one), or
    /// <see cref="LayoutReadingOrder.XyCut"/> for the legacy plain XY-cut.
    /// </summary>
    public LayoutReadingOrder ReadingOrder { get; init; } = LayoutReadingOrder.Auto;

    /// <summary>
    /// Assemble blocks in right-to-left reading order — the right-most column first, and within a row the
    /// right-most block first. Defaults to <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Applies to the geometric XY-cut ordering only. When the layout model emits its own reading-order
    /// index and <see cref="ReadingOrder"/> leaves it in use, that prediction is taken as-is — the model was
    /// trained on the document's real order and already accounts for direction.
    /// </remarks>
    public bool RightToLeft { get; init; }

    /// <summary>
    /// Which table-structure model recovers <see cref="StructureBlockType.Table"/> regions. Default
    /// <see cref="TableRecognitionModel.SlanetPlus"/> (single end-to-end model). Set
    /// <see cref="TableRecognitionModel.SlaNeXt"/> to use the PP-StructureV3 v2 path (a wired/wireless
    /// classifier picks the matching SLANeXt model) — more accurate on clearly bordered/borderless tables;
    /// downloads three extra models on first use. Only consulted when <see cref="RecognizeTables"/> is true.
    /// </summary>
    public TableRecognitionModel TableModel { get; init; } = TableRecognitionModel.SlanetPlus;

    /// <summary>
    /// Classify each table crop's orientation (0/90/180/270°) with the document-orientation classifier and
    /// rotate it upright before table-structure recognition, so sideways tables recognize correctly.
    /// Mirrors PP-StructureV3's <c>use_table_orientation_classify</c> (default true there too). Only
    /// consulted when <see cref="RecognizeTables"/> is true. Default true.
    /// </summary>
    public bool UseTableOrientationClassification { get; init; } = true;

    /// <summary>
    /// Text-recognition options applied to the OCR passes the structure engine runs (block text, table
    /// cells, captions, seal text) — batch size, drop score, text-line orientation, crop padding, etc.
    /// Default <c>null</c>: the engine uses its own defaults for structure OCR.
    /// </summary>
    public RecognitionOptions? Recognition { get; init; }

    /// <summary>
    /// Recognition languages passed through to the text recognizer for text/caption/seal regions. Takes
    /// strongly-typed <see cref="OcrLanguage"/> values. Defaults to a single-element list of
    /// <see cref="OcrLanguage.ChineseSimplified"/> (code <c>"ch"</c>, which also covers English/Japanese).
    /// Use <see cref="OcrLanguage.Auto"/> for auto-detect.
    /// </summary>
    public IReadOnlyList<OcrLanguage> Languages { get; init; } = new[] { OcrLanguage.ChineseSimplified };

    /// <summary>
    /// Default structure options.
    /// </summary>
    public static StructureOptions Default { get; } = new();
}
