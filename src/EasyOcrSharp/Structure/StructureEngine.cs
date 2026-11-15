using EasyOcrSharp.Structure.Engine;
using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure.Engine.Services;
using EasyOcrSharp.Structure.Formula;
using EasyOcrSharp.Structure.Layout;
using EasyOcrSharp.Structure.Preprocess;
using EasyOcrSharp.Structure.ReadingOrder;
using EasyOcrSharp.Structure.Seal;
using EasyOcrSharp.Structure.Table;
using EasyOcrSharp.Structure.Text;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure;

/// <summary>
/// Top-level coordinator for document-structure analysis ("PP-StructureV3"). Lazy-loads the document
/// pre-processor (orientation / unwarp), the layout detector, and the per-region recognizers (table,
/// formula, seal) on demand, reusing each ONNX session across calls; mirrors the lazy-session-cache /
/// dispose patterns of <see cref="StructureTextEngine"/>.
/// <para>
/// Pipeline (PP-StructureV3 semantics, filled by <see cref="AnalyzeAsync"/>): doc-preprocess → layout
/// detect + post-process → recognize formula regions and white them out → ONE whole-page OCR pass →
/// match the OCR lines into the layout blocks (re-recognizing "hurdle" lines that straddle block
/// borders) → per-block assembly (tables via <see cref="ITableRecognizer"/> fed the page-level lines,
/// vision blocks via the per-region path, text blocks via <see cref="TextLineAssembler"/>) → reading
/// order (XY-Cut++ by default) → assemble a <see cref="StructureResult"/>.
/// </para>
/// </summary>
internal sealed class StructureEngine : IAsyncDisposable
{
    private readonly StructureEngineOptions _options;
    private readonly ILogger? _logger;

    // The text-OCR path is the existing engine, reused for the whole-page pass, hurdle re-recognition and
    // (indirectly) for seals. When the parent service supplies its own engine we share it and never
    // dispose it; the parameterless-engine ctor builds and owns a private one.
    private readonly StructureTextEngine _ocrEngine;
    private readonly bool _ownsOcrEngine;

    // Session options resolved once for the structure models (single big graph per crop → not per-box parallel).
    private readonly SessionOptions _sessionOptions;
    private readonly OcrExecutionProvider _resolvedProvider;

    // Lazy single-instance caches, each guarded by its own lock (mirrors StructureTextEngine's detector/classifier).
    // The pre-processor cache is keyed on which sessions it holds: a later call that needs a session the
    // cached instance lacks rebuilds it with the union of the loaded sessions.
    private IDocPreprocessor? _preprocessor;
    private bool _preprocessorHasOrientation;
    private bool _preprocessorHasUnwarp;
    private readonly SemaphoreSlim _preprocessorLock = new(1, 1);

    // Keyed by model rather than a single slot: LayoutModel/TableRecognitionModel are per-call options, so a
    // one-slot cache would have to dispose the cached instance whenever the requested model changed — freeing
    // an ONNX session another thread is still running inference on. Every instance stays alive until
    // DisposeAsync. There are at most 3 layout models and 2 table models, so the cache is bounded.
    private readonly Dictionary<LayoutModel, ILayoutDetector> _layoutDetectors = [];
    private readonly SemaphoreSlim _layoutLock = new(1, 1);

    private readonly Dictionary<TableRecognitionModel, ITableRecognizer> _tableRecognizers = [];
    private readonly SemaphoreSlim _tableLock = new(1, 1);

    private IFormulaRecognizer? _formulaRecognizer;
    private readonly SemaphoreSlim _formulaLock = new(1, 1);

    // The seal recognizer holds the shared text recognizer for a specific language set, so the cache is
    // keyed on the (normalized) language codes and rebuilt when a call arrives with different ones.
    private ISealRecognizer? _sealRecognizer;
    private string? _sealRecognizerLanguageKey;
    private readonly SemaphoreSlim _sealLock = new(1, 1);

    // ---- OCR ↔ layout matching constants (PaddleX layout_parsing/pipeline_v2.py standardized_data) ----

    /// <summary>Overlap ratio (intersection / line area) at or above which an OCR line joins a block.</summary>
    private const double MatchOverlapRatio = 0.6;

    /// <summary>
    /// Overlap ratio at or above which a line belongs to one block outright, even when it also touches
    /// others — no hurdle splitting.
    /// </summary>
    private const double StrongMatchRatio = 0.9;

    /// <summary>Minimum intersection side (px) for a block to count as "overlapped" by a hurdle line.</summary>
    private const double HurdleMinOverlapPx = 3;

    /// <summary>Overlap ratio (intersection / formula area) at which a formula is spliced into a block.</summary>
    private const double FormulaAttachRatio = 0.5;

    /// <summary>
    /// Creates an engine that builds and owns its own <see cref="StructureTextEngine"/> for the text path.
    /// </summary>
    public StructureEngine(StructureEngineOptions options, ILogger? logger)
        : this(options, new StructureTextEngine(options, logger), ownsOcrEngine: true, logger)
    {
    }

    /// <summary>
    /// Creates an engine that shares the caller's <see cref="StructureTextEngine"/> for the text path — the
    /// caller retains ownership and disposes it (used by <see cref="StructureService"/> so the OCR and
    /// structure pipelines share one set of det/cls/rec sessions).
    /// </summary>
    internal StructureEngine(StructureEngineOptions options, StructureTextEngine ocrEngine, ILogger? logger)
        : this(options, ocrEngine, ownsOcrEngine: false, logger)
    {
    }

    private StructureEngine(StructureEngineOptions options, StructureTextEngine ocrEngine, bool ownsOcrEngine, ILogger? logger)
    {
        _options = options;
        _logger = logger;
        _ocrEngine = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));
        _ownsOcrEngine = ownsOcrEngine;
        _resolvedProvider = ExecutionProviderResolver.Resolve(options.ExecutionProvider, logger);
        _sessionOptions = ExecutionProviderResolver.BuildSessionOptions(_resolvedProvider, options, logger, perBoxParallel: false);
    }

    /// <summary>
    /// The OCR engine used for the text-recognition path (exposed so the parent service can share/warm it).
    /// </summary>
    public StructureTextEngine OcrEngine => _ocrEngine;

    /// <summary>
    /// One block awaiting the reading-order pass, carrying the paddle label string and the model's
    /// predicted order (-1 when the model emitted none) the orderers key off.
    /// </summary>
    private readonly record struct BlockEntry(StructureBlock Block, string Label, int ModelOrder);

    /// <summary>
    /// Runs the full document-structure pipeline over <paramref name="image"/> and returns the analyzed
    /// blocks in reading order.
    /// </summary>
    /// <param name="image">The page image to analyze (caller retains ownership).</param>
    /// <param name="options">Which stages / sub-recognizers to run and the recognition languages.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Pipeline (mirrors PaddleX PP-StructureV3's <c>pipeline_v2.py</c>): (1) doc-preprocess; (2) layout
    /// detection + post-processing + label post-fixes; (3) formula-family regions are recognized FIRST and
    /// their pixels whited out on a working copy; (4) ONE det+cls+rec pass over the whole page; (5) each
    /// OCR line is matched to the layout block with the largest overlap ratio — lines straddling ≥2 blocks
    /// with no dominant owner are cropped at the block borders and each fragment re-recognized ("hurdle"
    /// lines); (6) per-block assembly — tables via <see cref="ITableRecognizer"/> (fed the page-level
    /// lines inside the table box plus <c>$…$</c> formula lines; rotated tables are uprighted and re-OCR'd
    /// locally), vision blocks (image/chart/seal) via <see cref="AnalyzeRegionAsync"/>, text blocks via
    /// <see cref="TextLineAssembler"/>, with a per-block crop-OCR fallback for text blocks that matched no
    /// lines; (7) reading order — XY-Cut++ (<see cref="XyCutEnhancedOrderer"/>) by default. When layout
    /// detection finds nothing, one <c>text</c> block is synthesized per OCR line. The caller retains
    /// ownership of <paramref name="image"/>; any processed copy is disposed here.
    /// </remarks>
    public async Task<StructureResult> AnalyzeAsync(Image<Rgb24> image, StructureOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // ---- (1) document pre-processing (orientation / unwarp) -------------------------------------
        // DocPreprocessor.Apply returns a NEW image when it does any work; track whether we own it so the
        // caller's original is never disposed. When both toggles are off we skip loading the pre-processor
        // entirely and analyze the original image in place.
        Image<Rgb24> page = image;
        Image<Rgb24>? owned = null;
        var orphanLines = new List<OcrLine>();
        try
        {
            if (options.UseDocOrientation || options.UseUnwarp)
            {
                var preprocessor = await GetOrLoadPreprocessorAsync(options.UseDocOrientation, options.UseUnwarp, ct).ConfigureAwait(false);
                var preprocessed = preprocessor.Apply(image, options.UseDocOrientation, options.UseUnwarp);
                page = preprocessed.Image;
                if (preprocessed.OwnsImage) owned = preprocessed.Image;
                _logger?.LogInformation("Document pre-processing applied (rotation={Rotation} deg).", preprocessed.RotationApplied);
            }

            // ---- (2) layout detection ----------------------------------------------------------------
            var layoutDetector = await GetOrLoadLayoutDetectorAsync(options.LayoutModel, ct).ConfigureAwait(false);
            // Detect at the LOWEST floor any class may keep (per-class floors can sit below the global
            // threshold, e.g. paragraph_title 0.3); LayoutPostProcessor.Apply then re-thresholds each
            // candidate at its exact class floor.
            IReadOnlyList<LayoutRegion> detected;
            using (StructureDiagnostics.MeasureStage("layout"))
                detected = layoutDetector.Detect(page, LayoutPostProcessor.DetectionScoreFloor(options));

            // The detectors emit a fixed top-k of candidates with no NMS, so the same area of the page is
            // routinely proposed several times under different labels. Clean that up (and apply the optional
            // NMS / unclip / merge passes) before anything is recognized, then run the label post-fixes
            // (footnote relabel + lone-title promotion) PaddleX applies after its overlap filtering.
            var regions = Layout.LayoutPostProcessor.Apply(detected, options, page.Width, page.Height);
            regions = Layout.LayoutPostProcessor.ApplyLabelPostFixes(regions, page.Width, page.Height);
            _logger?.LogInformation(
                "Layout detector found {Count} region(s) ({Raw} before post-processing).",
                regions.Count, detected.Count);

            // ---- (3) formula recognition + (4) whole-page OCR (pipeline_v2.py:1084-1105) ---------------
            // Formula-family regions (formula / inline_formula / formula_number) are recognized by the
            // LaTeX recognizer and whited out on a working copy so the text recognizer never reads garbage
            // where a formula sits. The mask needs only the formula RECTANGLES, which come from layout, not
            // the LaTeX, so the masked page is built first and the (slow, autoregressive) LaTeX decode runs
            // concurrently with the page OCR. The two stages read different images (formula crops come from
            // the unmasked page, OCR reads the mask) and write disjoint results, so the output is identical to
            // running them one after the other — which is what DirectML still does.
            int n = regions.Count;
            var formulaLatex = new string?[n];
            var formulaIndices = new List<int>();
            if (options.RecognizeFormulas)
            {
                for (int i = 0; i < n; i++)
                {
                    if (IsFormulaFamily(regions[i])) formulaIndices.Add(i);
                }
            }

            Image<Rgb24>? masked = null;
            try
            {
                var ocrPage = page;
                if (formulaIndices.Count > 0)
                {
                    masked = page.Clone();
                    foreach (int i in formulaIndices)
                    {
                        var rect = ClampToImage(regions[i].Bounds, page.Width, page.Height);
                        if (rect.Width > 0 && rect.Height > 0) WhiteOut(masked, rect);
                    }
                    ocrPage = masked;
                }

                IReadOnlyList<OcrLine> pageLines;
                if (formulaIndices.Count == 0)
                {
                    pageLines = await RecognizePageTextAsync(ocrPage, options, ct).ConfigureAwait(false);
                }
                else if (AllowConcurrentStages)
                {
                    // Start the formula decode on the pool first so it is already running while the page OCR
                    // does its synchronous detection work on this thread. WhenAll observes both tasks, so a
                    // failure or cancellation in either surfaces here (formula first, matching the old order).
                    var formulaTask = Task.Run(() => RecognizeFormulasAsync(page, regions, formulaIndices, formulaLatex, ct), ct);
                    var ocrTask = RecognizePageTextAsync(ocrPage, options, ct);
                    await Task.WhenAll(formulaTask, ocrTask).ConfigureAwait(false);
                    pageLines = await ocrTask.ConfigureAwait(false);
                }
                else
                {
                    await RecognizeFormulasAsync(page, regions, formulaIndices, formulaLatex, ct).ConfigureAwait(false);
                    pageLines = await RecognizePageTextAsync(ocrPage, options, ct).ConfigureAwait(false);
                }
                _logger?.LogInformation("Whole-page OCR recognized {Count} line(s).", pageLines.Count);

                // ---- layout found nothing: synthesize one text block per OCR line --------------------
                if (n == 0)
                {
                    return WithOrphanLines(BuildResultFromOcrOnly(pageLines, options, page.Width, page.Height));
                }

                // ---- (5) OCR-line → block matching (incl. hurdle re-recognition) ---------------------
                var matchable = new bool[n];
                var isTable = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    matchable[i] = IsTextMatchable(regions[i], options);
                    isTable[i] = regions[i].Type == StructureBlockType.Table && options.RecognizeTables;
                }

                var matchedLines = new List<OcrLine>?[n];
                foreach (var line in pageLines)
                {
                    ct.ThrowIfCancellationRequested();
                    double lineArea = line.BoundingBox.Width * line.BoundingBox.Height;
                    if (!double.IsFinite(lineArea) || lineArea <= 0)
                    {
                        orphanLines.Add(line);
                        continue;
                    }

                    int best = -1;
                    double bestRatio = 0;
                    List<int>? overlapping = null;
                    for (int i = 0; i < n; i++)
                    {
                        if (!matchable[i]) continue;
                        var inter = Intersect(line.BoundingBox, regions[i].Bounds);
                        if (inter.Width > HurdleMinOverlapPx && inter.Height > HurdleMinOverlapPx)
                        {
                            (overlapping ??= new List<int>(2)).Add(i);
                        }
                        double ratio = inter.Width * inter.Height / lineArea;
                        if (ratio > bestRatio)
                        {
                            bestRatio = ratio;
                            best = i;
                        }
                    }

                    if (best >= 0 && bestRatio >= StrongMatchRatio)
                    {
                        // Dominant owner — assign outright even when the line grazes other blocks.
                        (matchedLines[best] ??= new List<OcrLine>()).Add(line);
                    }
                    else if (overlapping is { Count: >= 2 })
                    {
                        // Keep the source reading even when fragments succeed: no single block owns the
                        // complete original and failed/partial fragment recovery must not erase evidence.
                        orphanLines.Add(line);
                        // "Hurdle" line straddling several blocks: crop the line's intersection with each
                        // block and re-recognize each fragment so every block gets its own share of the text.
                        foreach (int i in overlapping)
                        {
                            var fragment = Intersect(line.BoundingBox, regions[i].Bounds);
                            if (fragment.Width <= HurdleMinOverlapPx || fragment.Height <= HurdleMinOverlapPx) continue;
                            var fragmentLines = await RecognizeFragmentAsync(ocrPage, fragment, options, ct).ConfigureAwait(false);
                            if (fragmentLines.Count == 0) continue;
                            (matchedLines[i] ??= new List<OcrLine>()).AddRange(fragmentLines);
                        }
                    }
                    else if (best >= 0 && bestRatio >= MatchOverlapRatio)
                    {
                        (matchedLines[best] ??= new List<OcrLine>()).Add(line);
                    }
                    else
                    {
                        orphanLines.Add(line);
                    }
                }

                // ---- formula splicing: inline formulas become spans of their owning block ------------
                // A recognized formula overlapping a text block (or carrying the inline_formula label)
                // becomes a $-wrapped BlockSpan of that block; one inside a table box becomes a $...$ OCR
                // line fed to the table recognizer. Everything else stays a standalone formula block.
                var attachedSpans = new List<BlockSpan>?[n];
                var formulaConsumed = new bool[n];
                foreach (int i in formulaIndices)
                {
                    if (regions[i].Type != StructureBlockType.Formula) continue; // formula_number stays standalone
                    var latex = formulaLatex[i];
                    if (string.IsNullOrWhiteSpace(latex)) continue;

                    var bounds = regions[i].Bounds;
                    double formulaArea = bounds.Width * bounds.Height;
                    if (formulaArea <= 0) continue;

                    int bestText = -1, bestTable = -1;
                    double bestTextRatio = 0, bestTableRatio = 0;
                    for (int j = 0; j < n; j++)
                    {
                        if (j == i) continue;
                        bool textCandidate = matchable[j] && !isTable[j];
                        if (!textCandidate && !isTable[j]) continue;
                        var inter = Intersect(bounds, regions[j].Bounds);
                        double ratio = inter.Width * inter.Height / formulaArea;
                        if (isTable[j])
                        {
                            if (ratio > bestTableRatio) { bestTableRatio = ratio; bestTable = j; }
                        }
                        else if (ratio > bestTextRatio)
                        {
                            bestTextRatio = ratio;
                            bestText = j;
                        }
                    }

                    bool inlineLabel = regions[i].RawLabel == "inline_formula";
                    if (bestTable >= 0 && bestTableRatio >= FormulaAttachRatio)
                    {
                        (matchedLines[bestTable] ??= new List<OcrLine>()).Add(MakeFormulaLine(bounds, latex!));
                        formulaConsumed[i] = true;
                    }
                    else if (bestText >= 0 && (bestTextRatio >= FormulaAttachRatio || (inlineLabel && bestTextRatio > 0)))
                    {
                        (attachedSpans[bestText] ??= new List<BlockSpan>()).Add(new BlockSpan(
                            (float)bounds.MinX, (float)bounds.MinY, (float)bounds.MaxX, (float)bounds.MaxY,
                            latex!, IsFormula: true));
                        formulaConsumed[i] = true;
                    }
                }

                // ---- (6) per-block assembly ----------------------------------------------------------
                // Table and seal regions are the expensive per-region recognizers, and each depends only on
                // the page, its own region and the lines matched into it — so they are computed first
                // (concurrently where allowed) into index-addressed slots, and the loop below assembles every
                // block in layout order exactly as before.
                var recognizerBlocks = await AnalyzeRecognizerBlocksAsync(page, regions, matchedLines, options, ct).ConfigureAwait(false);
                var entries = new List<BlockEntry>(n);
                for (int i = 0; i < n; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var region = regions[i];
                    string label = PaddleLabel(region);
                    int modelOrder = region.OrderIndex ?? -1;

                    if (options.RecognizeFormulas && IsFormulaFamily(region))
                    {
                        if (formulaConsumed[i]) continue; // spliced into its owning block/table
                        entries.Add(new BlockEntry(
                            new StructureBlock(region.Type, region.Bounds, Order: 0, Latex: formulaLatex[i], Score: region.Score),
                            label, modelOrder));
                        continue;
                    }

                    StructureBlock block;
                    switch (region.Type)
                    {
                        case StructureBlockType.Table when options.RecognizeTables:
                        case StructureBlockType.Seal when options.RecognizeSeals:
                            block = recognizerBlocks[i]!; // computed by AnalyzeRecognizerBlocksAsync
                            break;

                        case StructureBlockType.Figure:
                        case StructureBlockType.Chart:
                            block = await AnalyzeRegionAsync(page, region, options, ct).ConfigureAwait(false);
                            break;

                        default:
                            block = await AssembleTextBlockAsync(
                                page, region, label, matchedLines[i], attachedSpans[i], options, ct).ConfigureAwait(false);
                            break;
                    }
                    entries.Add(new BlockEntry(block, label, modelOrder));
                }

                // ---- (7) reading order + result ------------------------------------------------------
                return WithOrphanLines(AssembleResult(entries, options, page.Width, page.Height));
            }
            finally
            {
                masked?.Dispose();
            }
        }
        finally
        {
            owned?.Dispose();
        }

        // Lines the block matching could not give to exactly one block are kept as evidence instead of being
        // dropped (PaddleOcrNet 2.4.0). A page without any keeps the result object exactly as assembled.
        StructureResult WithOrphanLines(StructureResult result) => orphanLines.Count == 0 ? result : new()
        {
            Blocks = result.Blocks, SourceWidth = result.SourceWidth, SourceHeight = result.SourceHeight,
            OrphanLines = orphanLines.ToArray(),
        };
    }

    /// <summary>
    /// The empty-layout fallback (pipeline_v2.py:516-526): every whole-page OCR line becomes its own
    /// <c>text</c> block, ordered by the configured reading-order pass.
    /// </summary>
    private StructureResult BuildResultFromOcrOnly(
        IReadOnlyList<OcrLine> pageLines, StructureOptions options, int pageWidth, int pageHeight)
    {
        if (pageLines.Count == 0)
        {
            return new StructureResult
            {
                Blocks = Array.Empty<StructureBlock>(),
                SourceWidth = pageWidth,
                SourceHeight = pageHeight,
            };
        }

        var entries = new List<BlockEntry>(pageLines.Count);
        foreach (var line in pageLines)
        {
            entries.Add(new BlockEntry(
                new StructureBlock(
                    StructureBlockType.Text, line.BoundingBox, Order: 0,
                    Text: string.IsNullOrWhiteSpace(line.Text) ? null : line.Text,
                    Lines: new[] { line }, Score: (float)line.Confidence),
                "text", ModelOrder: -1));
        }
        return AssembleResult(entries, options, pageWidth, pageHeight);
    }

    /// <summary>
    /// Runs the configured reading-order pass over the assembled blocks, writes the rank into each block's
    /// <see cref="StructureBlock.Order"/>, and assembles the final <see cref="StructureResult"/>.
    /// </summary>
    private StructureResult AssembleResult(
        List<BlockEntry> entries, StructureOptions options, int pageWidth, int pageHeight)
    {
        var order = ComputeReadingOrder(entries, options, pageWidth, pageHeight);
        var ordered = new StructureBlock[entries.Count];
        for (int rank = 0; rank < order.Count; rank++)
        {
            ordered[rank] = entries[order[rank]].Block with { Order = rank };
        }

        return new StructureResult
        {
            Blocks = ordered,
            SourceWidth = pageWidth,
            SourceHeight = pageHeight,
        };
    }

    /// <summary>
    /// Sequences the blocks per <see cref="StructureOptions.ReadingOrder"/>: XY-Cut++
    /// (<see cref="XyCutEnhancedOrderer"/>) for <see cref="LayoutReadingOrder.Auto"/> /
    /// <see cref="LayoutReadingOrder.XyCutEnhanced"/> — Python PP-StructureV3 never trusts the model's
    /// order column for final ordering — the model's own order for <see cref="LayoutReadingOrder.Model"/>
    /// (falling back to plain XY-cut when any block lacks one), and the plain geometric
    /// <see cref="XyCutOrderer"/> for <see cref="LayoutReadingOrder.XyCut"/>. Returns a permutation of
    /// <c>0..entries.Count-1</c> in reading order.
    /// <para>
    /// Right-to-left pages (<see cref="StructureOptions.RightToLeft"/>) are EasyOcrSharp-specific: XY-Cut++ is
    /// a port of Python's left-to-right-only orderer, so <see cref="LayoutReadingOrder.Auto"/> on an RTL page
    /// uses the direction-aware plain XY-cut instead (what 3.1.0 did), and every XY-cut path is told the
    /// direction. An explicit <see cref="LayoutReadingOrder.XyCutEnhanced"/> is honoured as requested.
    /// </para>
    /// </summary>
    private static IReadOnlyList<int> ComputeReadingOrder(
        List<BlockEntry> entries, StructureOptions options, int pageWidth, int pageHeight)
    {
        if (entries.Count == 0) return Array.Empty<int>();

        switch (options.ReadingOrder)
        {
            case LayoutReadingOrder.Model when entries.All(e => e.ModelOrder >= 0):
                return Enumerable.Range(0, entries.Count)
                    .OrderBy(i => entries[i].ModelOrder)
                    .ToArray();

            case LayoutReadingOrder.Model:
            case LayoutReadingOrder.XyCut:
            case LayoutReadingOrder.Auto when options.RightToLeft:
                return XyCutOrderer.Order(entries.Select(e => e.Block.Bounds).ToArray(), options.RightToLeft);

            default: // Auto (left-to-right), XyCutEnhanced
            {
                var orderables = new OrderableBlock[entries.Count];
                for (int i = 0; i < entries.Count; i++)
                {
                    var b = entries[i].Block.Bounds;
                    orderables[i] = new OrderableBlock(
                        i, (float)b.MinX, (float)b.MinY, (float)b.MaxX, (float)b.MaxY,
                        entries[i].Label, entries[i].ModelOrder);
                }
                return XyCutEnhancedOrderer.Order(orderables, pageWidth, pageHeight);
            }
        }
    }

    /// <summary>
    /// Assembles a text-like block from the whole-page OCR lines matched to it (plus any inline-formula
    /// spans spliced into it), using <see cref="TextLineAssembler"/> for the PP-StructureV3 line-joining
    /// rules. A block that matched no lines and carries no formula spans falls back to the old per-block
    /// crop OCR (pipeline_v2.py:476-513) so faint blocks the page pass missed still get a reading.
    /// </summary>
    private async Task<StructureBlock> AssembleTextBlockAsync(
        Image<Rgb24> page, LayoutRegion region, string label,
        List<OcrLine>? matched, List<BlockSpan>? formulaSpans,
        StructureOptions options, CancellationToken ct)
    {
        IReadOnlyList<OcrLine> lines = matched ?? (IReadOnlyList<OcrLine>)Array.Empty<OcrLine>();

        if (lines.Count == 0 && formulaSpans is null)
        {
            var rect = ClampToImage(region.Bounds, page.Width, page.Height);
            if (rect.Width >= MinRegionPx && rect.Height >= MinRegionPx)
            {
                using var crop = page.Clone(ctx => ctx.Crop(rect));
                var cropLines = await RecognizeCropTextAsync(crop, options, ct).ConfigureAwait(false);
                lines = TranslateLines(cropLines, rect.X, rect.Y);
            }
        }

        var spans = new List<BlockSpan>(lines.Count + (formulaSpans?.Count ?? 0));
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            var box = line.BoundingBox;
            spans.Add(new BlockSpan(
                (float)box.MinX, (float)box.MinY, (float)box.MaxX, (float)box.MaxY,
                line.Text, IsFormula: false));
        }
        if (formulaSpans is not null) spans.AddRange(formulaSpans);

        var bounds = region.Bounds;
        string? text = spans.Count == 0
            ? null
            : TextLineAssembler.Assemble(
                spans, label,
                (float)bounds.MinX, (float)bounds.MinY, (float)bounds.MaxX, (float)bounds.MaxY);

        return new StructureBlock(
            region.Type, region.Bounds, Order: 0,
            Text: string.IsNullOrEmpty(text) ? null : text,
            Lines: lines.Count == 0 ? null : lines,
            Score: region.Score);
    }

    /// <summary>
    /// Recognizes a table region: optionally classifies the crop's orientation with the shared
    /// doc-orientation classifier (rotating 90/180/270° crops upright before structure recognition,
    /// table_recognition/pipeline_v2.py:1317-1394), feeds the structure model the page-level OCR lines
    /// that fell inside the table box translated to crop-local coordinates (a rotated table gets a fresh
    /// LOCAL det+rec pass instead, since the page lines are sideways), and returns the block with
    /// <see cref="StructureBlock.CellBounds"/> populated in PAGE coordinates (mapped back through the
    /// rotation when one was applied).
    /// </summary>
    private async Task<StructureBlock> AnalyzeTableAsync(
        Image<Rgb24> page, LayoutRegion region, IReadOnlyList<OcrLine> matchedLines,
        StructureOptions options, CancellationToken ct)
    {
        using var stage = StructureDiagnostics.MeasureStage("table");
        var rect = ClampToImage(region.Bounds, page.Width, page.Height);
        if (rect.Width < MinRegionPx || rect.Height < MinRegionPx)
        {
            return new StructureBlock(StructureBlockType.Table, region.Bounds, Order: 0, Score: region.Score);
        }

        using var crop = page.Clone(ctx => ctx.Crop(rect));

        // ---- orientation classification (optional) ----------------------------------------------------
        int rotation = 0;
        Image<Rgb24>? rotated = null;
        if (options.UseTableOrientationClassification)
        {
            var preprocessor = await GetOrLoadPreprocessorAsync(needOrientation: true, needUnwarp: false, ct).ConfigureAwait(false);
            // Apply hands back the crop itself (OwnsImage false) when it is already upright, so no copy is
            // made or disposed on the common path; a non-zero rotation always comes back as an owned image.
            var preprocessed = preprocessor.Apply(crop, useOrientation: true, useUnwarp: false);
            if (preprocessed.RotationApplied != 0 && preprocessed.OwnsImage)
            {
                rotated = preprocessed.Image;
                rotation = preprocessed.RotationApplied;
                _logger?.LogInformation("Table crop rotated {Deg}° upright before structure recognition.", rotation);
            }
            else if (preprocessed.OwnsImage)
            {
                preprocessed.Image.Dispose();
            }
        }

        try
        {
            var recognizer = await GetOrLoadTableRecognizerAsync(options.TableModel, ct).ConfigureAwait(false);

            if (rotation == 0)
            {
                // Feed the page-level OCR lines inside the table box, in crop-local coordinates. Only when
                // the page pass matched nothing does the table fall back to its own crop OCR.
                var cropLines = TranslateLines(matchedLines, -rect.X, -rect.Y);
                if (cropLines.Count == 0)
                {
                    cropLines = await RecognizeCropTextAsync(crop, options, ct).ConfigureAwait(false);
                    matchedLines = TranslateLines(cropLines, rect.X, rect.Y);
                }

                var table = recognizer.Recognize(crop, cropLines);
                var cellBounds = TranslateBoxes(table.CellBounds, rect.X, rect.Y);
                return new StructureBlock(
                    StructureBlockType.Table, region.Bounds, Order: 0,
                    TableHtml: table.Html, Lines: matchedLines, Score: region.Score)
                {
                    CellBounds = cellBounds,
                };
            }
            else
            {
                // The page-level lines were recognized sideways — run a LOCAL det+rec pass on the
                // uprighted crop, recognize the structure there, and map everything back to page space.
                var rotatedLines = await RecognizeCropTextAsync(rotated!, options, ct).ConfigureAwait(false);
                var table = recognizer.Recognize(rotated!, rotatedLines);

                var cellBounds = new OcrBoundingBox[table.CellBounds.Count];
                for (int i = 0; i < table.CellBounds.Count; i++)
                {
                    var mapped = MapRotatedBoxToCrop(table.CellBounds[i], rotation, rect.Width, rect.Height);
                    cellBounds[i] = new OcrBoundingBox(
                        mapped.MinX + rect.X, mapped.MinY + rect.Y, mapped.MaxX + rect.X, mapped.MaxY + rect.Y);
                }

                var pageLines = new List<OcrLine>(rotatedLines.Count);
                foreach (var line in rotatedLines)
                {
                    pageLines.Add(MapRotatedLineToPage(line, rotation, rect));
                }

                return new StructureBlock(
                    StructureBlockType.Table, region.Bounds, Order: 0,
                    TableHtml: table.Html, Lines: pageLines, Score: region.Score)
                {
                    CellBounds = cellBounds,
                };
            }
        }
        finally
        {
            rotated?.Dispose();
        }
    }

    /// <summary>
    /// Recognizes a single vision-like layout region (figure / chart / seal) into a
    /// <see cref="StructureBlock"/>. Seals go through <see cref="ISealRecognizer"/> (with the straight-OCR
    /// fallback for seals the curved-text path cannot rectify); figures and charts carry no recoverable
    /// text and are represented by bounds/type alone. The block's <see cref="StructureBlock.Order"/> is
    /// provisionally 0 and overwritten by the reading-order pass in <see cref="AnalyzeAsync"/>.
    /// </summary>
    private async Task<StructureBlock> AnalyzeRegionAsync(
        Image<Rgb24> page, LayoutRegion region, StructureOptions options, CancellationToken ct)
    {
        var rect = ClampToImage(region.Bounds, page.Width, page.Height);
        if (rect.Width < MinRegionPx || rect.Height < MinRegionPx)
        {
            // Region collapsed to nothing after clamping — keep a placeholder block so the layout (and the
            // detector's score) is still represented in reading order.
            return new StructureBlock(region.Type, region.Bounds, Order: 0, Score: region.Score);
        }

        switch (region.Type)
        {
            case StructureBlockType.Seal when options.RecognizeSeals:
            {
                using var crop = page.Clone(ctx => ctx.Crop(rect));
                var recognizer = await GetOrLoadSealRecognizerAsync(options, ct).ConfigureAwait(false);
                var sealLines = recognizer.Recognize(crop, ct);

                // The curved-text path can come back empty on a seal it cannot rectify.
                // Falling back to the shared text recognizer is a reasonable compromise: it will at least recover the text in a straight line, even if the bounding polygon is not curved.
                if (sealLines.Count == 0)
                {
                    sealLines = await RecognizeCropTextAsync(crop, options, ct).ConfigureAwait(false);
                }

                var pageLines = TranslateLines(sealLines, rect.X, rect.Y);
                return new StructureBlock(
                    StructureBlockType.Seal, region.Bounds, Order: 0,
                    Text: JoinText(sealLines), Lines: pageLines, Score: region.Score);
            }

            default:
                // Picture-like regions (figure/chart) carry no recoverable text; bounds/type alone.
                return new StructureBlock(region.Type, region.Bounds, Order: 0, Score: region.Score);
        }
    }

    /// <summary>
    /// Smallest region side (px) worth cropping/recognizing; smaller regions yield an empty block.
    /// </summary>
    private const int MinRegionPx = 2;

    /// <summary>
    /// Whether independent pipeline stages (formula decode vs page OCR, separate table / seal regions) may
    /// run concurrently. DirectML requires sequential execution, so any DirectML session keeps the whole
    /// pipeline in its original sequential order; CPU and the other accelerators run the stages side by side.
    /// </summary>
    private bool AllowConcurrentStages
        => _resolvedProvider != OcrExecutionProvider.DirectMl
           && _ocrEngine.ActiveProvider != OcrExecutionProvider.DirectMl;

    /// <summary>Whether a region is handled by a per-region recognizer (table structure or seal text).</summary>
    private static bool IsRecognizerRegion(LayoutRegion region, StructureOptions options)
        => (region.Type == StructureBlockType.Table && options.RecognizeTables)
           || (region.Type == StructureBlockType.Seal && options.RecognizeSeals);

    /// <summary>
    /// Runs the table and seal recognizers for every such region and returns the blocks in slots indexed like
    /// <paramref name="regions"/> (null for every other region). Each region depends only on the shared
    /// read-only page, its own bounds and the lines matched into it, and the recognizers keep no per-call
    /// state, so independent regions run concurrently — bounded to half the processors, and sequential on
    /// DirectML. Results land in fixed slots, so the output is deterministic; the first failure or a
    /// cancellation propagates.
    /// </summary>
    private async Task<StructureBlock?[]> AnalyzeRecognizerBlocksAsync(
        Image<Rgb24> page, IReadOnlyList<LayoutRegion> regions, List<OcrLine>?[] matchedLines,
        StructureOptions options, CancellationToken ct)
    {
        var blocks = new StructureBlock?[regions.Count];
        var work = new List<int>();
        for (int i = 0; i < regions.Count; i++)
        {
            if (IsRecognizerRegion(regions[i], options)) work.Add(i);
        }
        if (work.Count == 0) return blocks;

        int degree = AllowConcurrentStages ? Math.Min(work.Count, Math.Max(1, Environment.ProcessorCount / 2)) : 1;
        if (degree <= 1)
        {
            foreach (int i in work)
            {
                ct.ThrowIfCancellationRequested();
                blocks[i] = await AnalyzeRecognizerBlockAsync(page, regions[i], matchedLines[i], options, ct).ConfigureAwait(false);
            }
            return blocks;
        }

        await Parallel.ForEachAsync(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct },
            async (i, token) => blocks[i] = await AnalyzeRecognizerBlockAsync(page, regions[i], matchedLines[i], options, token).ConfigureAwait(false))
            .ConfigureAwait(false);
        return blocks;
    }

    /// <summary>Dispatches one table or seal region to its recognizer path.</summary>
    private Task<StructureBlock> AnalyzeRecognizerBlockAsync(
        Image<Rgb24> page, LayoutRegion region, List<OcrLine>? matched, StructureOptions options, CancellationToken ct)
        => region.Type == StructureBlockType.Table
            ? AnalyzeTableAsync(page, region, matched ?? (IReadOnlyList<OcrLine>)Array.Empty<OcrLine>(), options, ct)
            : AnalyzeRegionAsync(page, region, options, ct);

    /// <summary>
    /// Recognizes the LaTeX of every formula-family region in <paramref name="formulaIndices"/> from the
    /// UNMASKED page, writing each result into its own slot of <paramref name="formulaLatex"/>. Regions that
    /// collapse below <see cref="MinRegionPx"/> after clamping keep a null slot.
    /// </summary>
    private async Task RecognizeFormulasAsync(
        Image<Rgb24> page, IReadOnlyList<LayoutRegion> regions, List<int> formulaIndices,
        string?[] formulaLatex, CancellationToken ct)
    {
        using var stage = StructureDiagnostics.MeasureStage("formula");
        var formulaRecognizer = await GetOrLoadFormulaRecognizerAsync(ct).ConfigureAwait(false);
        foreach (int i in formulaIndices)
        {
            ct.ThrowIfCancellationRequested();
            var rect = ClampToImage(regions[i].Bounds, page.Width, page.Height);
            if (rect.Width < MinRegionPx || rect.Height < MinRegionPx) continue;
            using var crop = page.Clone(ctx => ctx.Crop(rect));
            formulaLatex[i] = formulaRecognizer.RecognizeLatex(crop);
        }
    }

    /// <summary>
    /// The recognition options the structure pipeline's OCR passes run with:
    /// <see cref="StructureOptions.Recognition"/> when the caller supplied one, otherwise the engine
    /// defaults (text-line orientation on). Grouping is always forced to <see cref="TextGrouping.Word"/>
    /// (one <see cref="OcrLine"/> per detected box): the block matcher and the table recognizer both
    /// assign individual boxes, and merged same-line boxes would smear across block/cell borders.
    /// Document orientation/unwarp are always forced OFF here: the structure pipeline runs its own
    /// page-level pre-processing (<see cref="StructureOptions.UseDocOrientation"/> /
    /// <see cref="StructureOptions.UseUnwarp"/>) before layout detection, and per-block/table crops must
    /// never be independently re-oriented by the OCR engine's doc-orientation stage.
    /// </summary>
    private static RecognitionOptions BuildRecognitionOptions(StructureOptions options)
        => (options.Recognition ?? RecognitionOptions.Default) with
        {
            Grouping = TextGrouping.Word,
            UseDocOrientation = false,
            UseDocUnwarp = false,
            // Structure blocks re-map and re-translate lines without their words; don't compute them.
            ReturnWordBoxes = false,
        };

    /// <summary>
    /// Runs ONE det+cls+rec pass over the whole (formula-masked) page via the shared engine and returns
    /// the recognized lines in page pixel coordinates.
    /// </summary>
    private async Task<IReadOnlyList<OcrLine>> RecognizePageTextAsync(
        Image<Rgb24> page, StructureOptions options, CancellationToken ct)
        => await _ocrEngine.RecognizeAsync(page, options.Languages.ToCodes(), BuildRecognitionOptions(options), ct).ConfigureAwait(false);

    /// <summary>
    /// Runs the shared OCR recognizer (<see cref="StructureTextEngine.RecognizeAsync"/>) over a region crop and
    /// returns the recognized lines in the crop's pixel coordinates. Used for the per-block fallback when
    /// the whole-page pass matched nothing into a block, and for the local pass on rotated table crops.
    /// </summary>
    private async Task<IReadOnlyList<OcrLine>> RecognizeCropTextAsync(
        Image<Rgb24> crop, StructureOptions options, CancellationToken ct)
        => await _ocrEngine.RecognizeAsync(crop, options.Languages.ToCodes(), BuildRecognitionOptions(options), ct).ConfigureAwait(false);

    /// <summary>
    /// Re-recognizes one axis-aligned fragment of the page (a hurdle line's intersection with a block) by
    /// handing the engine the fragment's polygon — detection is skipped, the crop is rectified, classified
    /// and recognized. Lines come back in page coordinates.
    /// </summary>
    private async Task<IReadOnlyList<OcrLine>> RecognizeFragmentAsync(
        Image<Rgb24> page, OcrBoundingBox fragment, StructureOptions options, CancellationToken ct)
    {
        var polygon = new[]
        {
            new OcrPoint(fragment.MinX, fragment.MinY),
            new OcrPoint(fragment.MaxX, fragment.MinY),
            new OcrPoint(fragment.MaxX, fragment.MaxY),
            new OcrPoint(fragment.MinX, fragment.MaxY),
        };
        return await _ocrEngine.RecognizeRegionsAsync(
            page, options.Languages.ToCodes(), new[] { polygon }, BuildRecognitionOptions(options), ct).ConfigureAwait(false);
    }

    // ===============================================================================================
    // classification + geometry helpers
    // ===============================================================================================

    /// <summary>Whether a region belongs to the formula family (formula / inline_formula / formula_number).</summary>
    private static bool IsFormulaFamily(LayoutRegion region)
        => region.Type is StructureBlockType.Formula or StructureBlockType.FormulaNumber;

    /// <summary>
    /// Whether whole-page OCR lines may be matched into this region: text-like regions and tables yes;
    /// vision regions (figure/chart) and seals no; formula-family regions only when formula recognition is
    /// off (their pixels then stay un-masked and read as text, preserving the pre-V3 behaviour); seals
    /// only when seal recognition is off (so their text is still recovered somewhere).
    /// </summary>
    private static bool IsTextMatchable(LayoutRegion region, StructureOptions options) => region.Type switch
    {
        StructureBlockType.Figure or StructureBlockType.Chart => false,
        StructureBlockType.Seal => !options.RecognizeSeals,
        StructureBlockType.Formula or StructureBlockType.FormulaNumber => !options.RecognizeFormulas,
        _ => true,
    };

    /// <summary>
    /// The paddle label string the reading-order and text-assembly contracts key off: the model's own
    /// <see cref="LayoutRegion.RawLabel"/> when the sidecar supplied one, else the canonical paddle name
    /// for the mapped <see cref="StructureBlockType"/>.
    /// </summary>
    private static string PaddleLabel(LayoutRegion region) => region.RawLabel ?? region.Type switch
    {
        StructureBlockType.Text or StructureBlockType.Paragraph or StructureBlockType.List => "text",
        StructureBlockType.Title => "paragraph_title",
        StructureBlockType.DocTitle => "doc_title",
        StructureBlockType.Table => "table",
        StructureBlockType.TableCaption => "table_title",
        StructureBlockType.Figure => "image",
        StructureBlockType.FigureCaption => "figure_title",
        StructureBlockType.Formula => "formula",
        StructureBlockType.FormulaNumber => "formula_number",
        StructureBlockType.Seal => "seal",
        StructureBlockType.Chart => "chart",
        StructureBlockType.Header => "header",
        StructureBlockType.Footer => "footer",
        StructureBlockType.Reference => "reference",
        StructureBlockType.Footnote => "footnote",
        StructureBlockType.PageNumber => "number",
        StructureBlockType.Abstract => "abstract",
        StructureBlockType.Algorithm => "algorithm",
        StructureBlockType.Aside => "aside_text",
        _ => "text",
    };

    /// <summary>Builds the synthetic <c>$…$</c> OCR line a table-resident formula is injected as.</summary>
    private static OcrLine MakeFormulaLine(OcrBoundingBox bounds, string latex)
    {
        var polygon = new[]
        {
            new OcrPoint(bounds.MinX, bounds.MinY),
            new OcrPoint(bounds.MaxX, bounds.MinY),
            new OcrPoint(bounds.MaxX, bounds.MaxY),
            new OcrPoint(bounds.MinX, bounds.MaxY),
        };
        return new OcrLine
        {
            Text = "$" + latex + "$",
            Confidence = 1,
            BoundingPolygon = polygon,
            BoundingBox = bounds,
        };
    }

    /// <summary>Fills a rectangle of <paramref name="image"/> with white (formula white-out).</summary>
    private static void WhiteOut(Image<Rgb24> image, Rectangle rect)
    {
        var white = new Rgb24(255, 255, 255);
        image.ProcessPixelRows(accessor =>
        {
            int yEnd = rect.Y + rect.Height;
            for (int y = rect.Y; y < yEnd; y++)
            {
                accessor.GetRowSpan(y).Slice(rect.X, rect.Width).Fill(white);
            }
        });
    }

    /// <summary>The intersection of two boxes (a zero-size box at the origin overlap when disjoint).</summary>
    private static OcrBoundingBox Intersect(OcrBoundingBox a, OcrBoundingBox b)
    {
        double minX = Math.Max(a.MinX, b.MinX);
        double minY = Math.Max(a.MinY, b.MinY);
        double maxX = Math.Min(a.MaxX, b.MaxX);
        double maxY = Math.Min(a.MaxY, b.MaxY);
        return maxX <= minX || maxY <= minY
            ? OcrBoundingBox.Empty
            : new OcrBoundingBox(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Maps an axis-aligned box from an uprighted (rotated) table crop back into the ORIGINAL crop's
    /// coordinates. <paramref name="rotationCw"/> is the clockwise rotation that was applied to the crop
    /// (90/180/270); <paramref name="cropWidth"/>/<paramref name="cropHeight"/> are the original crop's
    /// dimensions.
    /// </summary>
    private static OcrBoundingBox MapRotatedBoxToCrop(OcrBoundingBox box, int rotationCw, int cropWidth, int cropHeight)
        => rotationCw switch
        {
            90 => new OcrBoundingBox(box.MinY, cropHeight - box.MaxX, box.MaxY, cropHeight - box.MinX),
            180 => new OcrBoundingBox(cropWidth - box.MaxX, cropHeight - box.MaxY, cropWidth - box.MinX, cropHeight - box.MinY),
            270 => new OcrBoundingBox(cropWidth - box.MaxY, box.MinX, cropWidth - box.MinY, box.MaxX),
            _ => box,
        };

    /// <summary>
    /// Maps an OCR line recognized on an uprighted (rotated) table crop back into PAGE coordinates:
    /// every polygon point is pushed through the inverse rotation into the original crop's space, then
    /// offset by the crop's page position.
    /// </summary>
    private static OcrLine MapRotatedLineToPage(OcrLine line, int rotationCw, Rectangle cropRect)
    {
        int w = cropRect.Width, h = cropRect.Height;
        var polygon = new OcrPoint[line.BoundingPolygon.Count];
        for (int i = 0; i < polygon.Length; i++)
        {
            var p = line.BoundingPolygon[i];
            var (x, y) = rotationCw switch
            {
                90 => (p.Y, h - p.X),
                180 => (w - p.X, h - p.Y),
                270 => (w - p.Y, p.X),
                _ => (p.X, p.Y),
            };
            polygon[i] = new OcrPoint(x + cropRect.X, y + cropRect.Y);
        }
        return line with
        {
            BoundingPolygon = polygon,
            BoundingBox = OcrBoundingBox.FromPoints(polygon),
        };
    }

    /// <summary>
    /// Clamps a (possibly out-of-bounds / inverted) bounding box to an integer pixel rectangle inside the page.
    /// </summary>
    private static Rectangle ClampToImage(OcrBoundingBox bounds, int width, int height)
    {
        int minX = (int)Math.Floor(Math.Min(bounds.MinX, bounds.MaxX));
        int minY = (int)Math.Floor(Math.Min(bounds.MinY, bounds.MaxY));
        int maxX = (int)Math.Ceiling(Math.Max(bounds.MinX, bounds.MaxX));
        int maxY = (int)Math.Ceiling(Math.Max(bounds.MinY, bounds.MaxY));

        minX = Math.Clamp(minX, 0, width);
        minY = Math.Clamp(minY, 0, height);
        maxX = Math.Clamp(maxX, 0, width);
        maxY = Math.Clamp(maxY, 0, height);
        return new Rectangle(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
    }

    /// <summary>
    /// Translates OCR lines by (<paramref name="dx"/>, <paramref name="dy"/>) — positive offsets go from
    /// crop to page coordinates, negative from page to crop — by offsetting every polygon point and the
    /// axis-aligned box. A zero offset returns the input unchanged.
    /// </summary>
    private static IReadOnlyList<OcrLine> TranslateLines(IReadOnlyList<OcrLine> lines, int dx, int dy)
    {
        if ((dx == 0 && dy == 0) || lines.Count == 0) return lines;
        var translated = new List<OcrLine>(lines.Count);
        foreach (var line in lines)
        {
            var poly = line.BoundingPolygon.Select(p => new OcrPoint(p.X + dx, p.Y + dy)).ToArray();
            var box = line.BoundingBox;
            translated.Add(line with
            {
                BoundingPolygon = poly,
                BoundingBox = new OcrBoundingBox(box.MinX + dx, box.MinY + dy, box.MaxX + dx, box.MaxY + dy),
            });
        }
        return translated;
    }

    /// <summary>Translates axis-aligned boxes by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    private static IReadOnlyList<OcrBoundingBox> TranslateBoxes(IReadOnlyList<OcrBoundingBox> boxes, int dx, int dy)
    {
        if (boxes.Count == 0) return boxes;
        var translated = new OcrBoundingBox[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            translated[i] = new OcrBoundingBox(b.MinX + dx, b.MinY + dy, b.MaxX + dx, b.MaxY + dy);
        }
        return translated;
    }

    /// <summary>
    /// Joins recognized lines into a single block-text string (newline-separated); null when empty.
    /// Used for seal blocks, whose curved lines don't flow through <see cref="TextLineAssembler"/>.
    /// </summary>
    private static string? JoinText(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count == 0) return null;
        var text = string.Join("\n", lines.Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        return text.Length == 0 ? null : text;
    }

    // ===============================================================================================
    // Wired session/recognizer loaders. These download the registry assets via ModelDownloadManager and
    // build the ONNX-session-backed components, caching one instance each.
    // ===============================================================================================

    /// <summary>
    /// Loads the document pre-processor with the doc-orientation and/or UVDoc unwarp sessions. The cache
    /// is keyed on which sessions the instance holds: a later call needing a session the cached instance
    /// lacks rebuilds it with the union of everything loaded so far (so the table-orientation path and
    /// the page pre-processing path share one classifier session).
    /// </summary>
    public async Task<IDocPreprocessor> GetOrLoadPreprocessorAsync(bool needOrientation, bool needUnwarp, CancellationToken ct)
    {
        var cached = _preprocessor;
        if (cached is not null
            && (!needOrientation || _preprocessorHasOrientation)
            && (!needUnwarp || _preprocessorHasUnwarp))
        {
            return cached;
        }

        await _preprocessorLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_preprocessor is not null
                && (!needOrientation || _preprocessorHasOrientation)
                && (!needUnwarp || _preprocessorHasUnwarp))
            {
                return _preprocessor;
            }

            bool wantOrientation = needOrientation || _preprocessorHasOrientation;
            bool wantUnwarp = needUnwarp || _preprocessorHasUnwarp;

            InferenceSession? orientation = wantOrientation
                ? await LoadSessionAsync(StructureModelRegistry.DocImageOrientation, ct).ConfigureAwait(false)
                : null;
            InferenceSession? unwarp = wantUnwarp
                ? await LoadSessionAsync(StructureModelRegistry.DocUnwarp, ct).ConfigureAwait(false)
                : null;

            _preprocessor?.Dispose();
            _preprocessor = new DocPreprocessor(orientation, unwarp);
            _preprocessorHasOrientation = wantOrientation;
            _preprocessorHasUnwarp = wantUnwarp;
            _logger?.LogInformation("Document pre-processor loaded (orientation={Ori}, unwarp={Unwarp}).",
                wantOrientation, wantUnwarp);
            return _preprocessor;
        }
        finally
        {
            _preprocessorLock.Release();
        }
    }

    /// <summary>
    /// Convenience overload keyed off <see cref="StructureOptions"/>'s pre-processing toggles.
    /// </summary>
    public Task<IDocPreprocessor> GetOrLoadPreprocessorAsync(StructureOptions options, CancellationToken ct)
        => GetOrLoadPreprocessorAsync(options.UseDocOrientation, options.UseUnwarp, ct);

    /// <summary>
    /// Loads (once) the layout detector for the requested <see cref="LayoutModel"/>. PicoDet-S/M build a
    /// <see cref="PicoDetLayoutDetector"/>; RT-DETR-L builds an <see cref="RtDetrLayoutDetector"/>. Both
    /// take a class-id → <see cref="StructureBlockType"/> map parsed from the model's label sidecar.
    /// </summary>
    public async Task<ILayoutDetector> GetOrLoadLayoutDetectorAsync(LayoutModel model, CancellationToken ct)
    {
        lock (_layoutDetectors)
        {
            if (_layoutDetectors.TryGetValue(model, out var cached)) return cached;
        }
        await _layoutLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_layoutDetectors)
            {
                if (_layoutDetectors.TryGetValue(model, out var cached)) return cached;
            }

            var (modelAsset, labelAsset) = model switch
            {
                LayoutModel.PicoDetS => (StructureModelRegistry.DocLayoutS, StructureModelRegistry.DocLayoutSLabels),
                LayoutModel.PicoDetM => (StructureModelRegistry.DocLayoutM, StructureModelRegistry.DocLayoutMLabels),
                LayoutModel.RtDetrL => (StructureModelRegistry.DocLayoutV3, StructureModelRegistry.DocLayoutV3Labels),
                _ => (StructureModelRegistry.DocLayoutS, StructureModelRegistry.DocLayoutSLabels),
            };

            var labelPath = await EnsureAssetAsync(labelAsset, ct).ConfigureAwait(false);
            var classMap = LayoutLabelMap.Load(labelPath);
            var labelNames = LayoutLabelMap.LoadNames(labelPath);
            if (classMap.Count == 0)
            {
                // Sidecar missing/unparseable: fall back to the model's canonical label vocabulary so
                // detections still map to real block types. The RT-DETR slot is served by PP-DocLayoutV3 (25-class).
                var fallbackNames = model == LayoutModel.RtDetrL
                    ? LayoutLabelMap.DocLayoutV325
                    : LayoutLabelMap.DocLayout23;
                classMap = LayoutLabelMap.FromNames(fallbackNames);
                labelNames = LayoutLabelMap.NamesFromList(fallbackNames);
            }
            var session = await LoadSessionAsync(modelAsset, ct).ConfigureAwait(false);

            ILayoutDetector detector = model == LayoutModel.RtDetrL
                ? new RtDetrLayoutDetector(session, classMap, labelNames)
                : new PicoDetLayoutDetector(session, classMap, labelNames);
            lock (_layoutDetectors) _layoutDetectors[model] = detector;
            _logger?.LogInformation("Layout detector loaded ({Model}).", model);
            return detector;
        }
        finally
        {
            _layoutLock.Release();
        }
    }

    /// <summary>
    /// Loads (once per <see cref="TableRecognitionModel"/>) the table-structure recognizer:
    /// <see cref="TableRecognitionModel.SlanetPlus"/> builds a single SLANet_plus recognizer (488×488);
    /// <see cref="TableRecognitionModel.SlaNeXt"/> builds a <see cref="SlaNeXtTableRouter"/> over the
    /// wired/wireless classifier, the SLANeXt_wired recognizer and the SLANet_plus wireless recognizer.
    /// Rebuilt if the requested model changes (mirrors the layout-detector cache).
    /// </summary>
    public async Task<ITableRecognizer> GetOrLoadTableRecognizerAsync(TableRecognitionModel model, CancellationToken ct)
    {
        lock (_tableRecognizers)
        {
            if (_tableRecognizers.TryGetValue(model, out var cached)) return cached;
        }
        await _tableLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_tableRecognizers)
            {
                if (_tableRecognizers.TryGetValue(model, out var cached)) return cached;
            }

            ITableRecognizer recognizer = model == TableRecognitionModel.SlaNeXt
                ? await BuildSlaNeXtRouterAsync(ct).ConfigureAwait(false)
                : await BuildSlanetPlusAsync(ct).ConfigureAwait(false);
            lock (_tableRecognizers) _tableRecognizers[model] = recognizer;
            return recognizer;
        }
        finally
        {
            _tableLock.Release();
        }
    }

    /// <summary>Builds the SLANet_plus recognizer (single end-to-end model, 488×488).</summary>
    private async Task<ITableRecognizer> BuildSlanetPlusAsync(CancellationToken ct)
    {
        var vocab = await LoadTableVocabAsync(ct).ConfigureAwait(false);
        var session = await LoadSessionAsync(StructureModelRegistry.SlanetPlus, ct).ConfigureAwait(false);
        _logger?.LogInformation("Table-structure recognizer loaded (SLANet_plus, {Tokens} tokens).", vocab.Count);
        return new SlanetTableRecognizer(session, vocab);
    }

    /// <summary>
    /// Builds the PP-StructureV3 v2 table router: the wired/wireless table classifier routes wired tables
    /// to SLANeXt_wired (512×512, content-normalized location head) and wireless tables to SLANet_plus
    /// (488×488, end-to-end) — the model pairing PP-StructureV3.yaml ships (its wireless structure model
    /// is SLANet_plus, not SLANeXt_wireless).
    /// </summary>
    private async Task<ITableRecognizer> BuildSlaNeXtRouterAsync(CancellationToken ct)
    {
        var vocab = await LoadTableVocabAsync(ct).ConfigureAwait(false);
        var clsSession = await LoadSessionAsync(StructureModelRegistry.TableClassifier, ct).ConfigureAwait(false);
        var wiredSession = await LoadSessionAsync(StructureModelRegistry.SlaNeXtWired, ct).ConfigureAwait(false);
        var wirelessSession = await LoadSessionAsync(StructureModelRegistry.SlanetPlus, ct).ConfigureAwait(false);

        const int slanextInputSize = 512;
        var classifier = new TableClassifier(clsSession);
        // SLANeXt's location head is content-normalized, unlike SLANet_plus's canvas-normalized one.
        var wired = new SlanetTableRecognizer(wiredSession, vocab, slanextInputSize, contentNormalizedBoxes: true);
        var wireless = new SlanetTableRecognizer(wirelessSession, vocab);
        _logger?.LogInformation("Table-structure recognizer loaded (SLANeXt v2: table_cls + SLANeXt_wired + SLANet_plus wireless).");
        return new SlaNeXtTableRouter(classifier, wired, wireless);
    }

    /// <summary>
    /// Loads the optional structure-token dictionary shared by SLANet/SLANeXt. The sidecar may not be hosted;
    /// <see cref="SlanetTableRecognizer"/> embeds the canonical 48-token fallback, so an empty list is fine.
    /// </summary>
    private async Task<IReadOnlyList<string>> LoadTableVocabAsync(CancellationToken ct)
    {
        try
        {
            var dictPath = await EnsureAssetAsync(StructureModelRegistry.TableStructureDict, ct).ConfigureAwait(false);
            return LoadTokenList(dictPath);
        }
        catch (Exception ex)
        {
            _logger?.LogInformation(ex, "Table-structure dictionary not hosted; using the embedded vocab.");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Loads (once) the LaTeX-OCR formula recognizer (image-resizer + encoder + decoder + tokenizer).
    /// </summary>
    public async Task<IFormulaRecognizer> GetOrLoadFormulaRecognizerAsync(CancellationToken ct)
    {
        if (_formulaRecognizer is not null) return _formulaRecognizer;
        await _formulaLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_formulaRecognizer is not null) return _formulaRecognizer;

            var encoder = await LoadSessionAsync(StructureModelRegistry.FormulaEncoder, ct).ConfigureAwait(false);
            var decoder = await LoadSessionAsync(StructureModelRegistry.FormulaDecoder, ct).ConfigureAwait(false);
            var resizer = await LoadSessionAsync(StructureModelRegistry.FormulaImageResizer, ct).ConfigureAwait(false);
            var tokenizerPath = await EnsureAssetAsync(StructureModelRegistry.FormulaTokenizer, ct).ConfigureAwait(false);
            var vocab = LoadIndexedVocab(tokenizerPath);

            _formulaRecognizer = new LatexOcrRecognizer(encoder, decoder, resizer, vocab);
            _logger?.LogInformation("Formula recognizer loaded (LaTeX-OCR, {Tokens} tokens).", vocab.Count);
            return _formulaRecognizer;
        }
        finally
        {
            _formulaLock.Release();
        }
    }

    /// <summary>
    /// Loads the seal recognizer (PP-OCRv4 seal detector + the shared text recognizer). Cached per
    /// language set: the shared text recognizer inside it is language-specific, so a call with different
    /// <see cref="StructureOptions.Languages"/> rebuilds the recognizer around the matching pack.
    /// </summary>
    public async Task<ISealRecognizer> GetOrLoadSealRecognizerAsync(StructureOptions options, CancellationToken ct)
    {
        var codes = options.Languages.ToCodes();
        var key = string.Join(",", codes);
        if (_sealRecognizer is not null && _sealRecognizerLanguageKey == key) return _sealRecognizer;
        await _sealLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sealRecognizer is not null && _sealRecognizerLanguageKey == key) return _sealRecognizer;
            _sealRecognizer?.Dispose();
            _sealRecognizer = null;

            var sealDet = await LoadSessionAsync(StructureModelRegistry.SealDetector, ct).ConfigureAwait(false);
            var textRecognizer = await _ocrEngine.GetSharedRecognizerAsync(codes, ct).ConfigureAwait(false);

            _sealRecognizer = new SealRecognizer(sealDet, textRecognizer);
            _sealRecognizerLanguageKey = key;
            _logger?.LogInformation("Seal recognizer loaded (PP-OCRv4 seal det + shared text recognizer, languages={Key}).", key);
            return _sealRecognizer;
        }
        finally
        {
            _sealLock.Release();
        }
    }

    // ---- low-level asset/session helpers (wired) ----

    private Task<string> EnsureAssetAsync(ModelAsset asset, CancellationToken ct)
        => ModelDownloadManager.EnsureModelAsync(asset, _options.ModelCachePath, _options.Download, _logger, ct);

    private async Task<InferenceSession> LoadSessionAsync(ModelAsset asset, CancellationToken ct)
    {
        var path = await EnsureAssetAsync(asset, ct).ConfigureAwait(false);
        return new InferenceSession(path, _sessionOptions);
    }

    /// <summary>
    /// Reads a newline-delimited token list (e.g. <c>table_structure_dict.txt</c>) into an ordered list.
    /// </summary>
    private static IReadOnlyList<string> LoadTokenList(string path)
        => File.ReadAllLines(path);

    /// <summary>
    /// Loads a token-id → token-string vocabulary for the LaTeX-OCR decoder. The primary format is a
    /// HuggingFace <c>tokenizer.json</c>, whose <c>model.vocab</c> object maps each token string to its
    /// integer id (<c>{ "token": id, ... }</c>); this is inverted into an id → token map. If the file is
    /// not a tokenizer.json (no <c>model.vocab</c> object), it falls back to reading a one-token-per-line
    /// list, using the line number as the id.
    /// </summary>
    private static IReadOnlyDictionary<int, string> LoadIndexedVocab(string path)
    {
        var text = File.ReadAllText(path);

        // Fast path: only attempt JSON parsing when the file actually looks like a JSON object.
        if (text.AsSpan().TrimStart().StartsWith("{"))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("model", out var model) &&
                    model.TryGetProperty("vocab", out var vocab) &&
                    vocab.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    var jsonMap = new Dictionary<int, string>();
                    foreach (var entry in vocab.EnumerateObject())
                    {
                        if (entry.Value.ValueKind == System.Text.Json.JsonValueKind.Number &&
                            entry.Value.TryGetInt32(out int id))
                        {
                            jsonMap[id] = entry.Name;
                        }
                    }

                    if (jsonMap.Count > 0) return jsonMap;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Not a valid tokenizer.json — fall through to the line-list fallback below.
            }
        }

        var lines = text.Split('\n');
        var map = new Dictionary<int, string>(lines.Length);
        for (int i = 0; i < lines.Length; i++) map[i] = lines[i].TrimEnd('\r');
        return map;
    }

    public async ValueTask DisposeAsync()
    {
        _preprocessor?.Dispose();
        foreach (var detector in _layoutDetectors.Values) detector.Dispose();
        _layoutDetectors.Clear();
        foreach (var recognizer in _tableRecognizers.Values) recognizer.Dispose();
        _tableRecognizers.Clear();
        _formulaRecognizer?.Dispose();
        _sealRecognizer?.Dispose();
        _sessionOptions.Dispose();
        if (_ownsOcrEngine)
        {
            await _ocrEngine.DisposeAsync().ConfigureAwait(false);
        }

        _preprocessorLock.Dispose();
        _layoutLock.Dispose();
        _tableLock.Dispose();
        _formulaLock.Dispose();
        _sealLock.Dispose();
    }
}
