using EasyOcrSharp.Structure.Engine;
using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure.Engine.Services;
using EasyOcrSharp.Structure.Formula;
using EasyOcrSharp.Structure.Layout;
using EasyOcrSharp.Structure.Preprocess;
using EasyOcrSharp.Structure.Seal;
using EasyOcrSharp.Structure.Table;
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
/// dispose patterns of <see cref="StructureTextEngine"/>. Text inside text-like regions is recognized by
/// running the existing <see cref="StructureTextEngine"/> over each region crop.
/// <para>
/// Pipeline (filled by <see cref="AnalyzeAsync"/>): doc-preprocess → layout detect → for each region,
/// OCR text regions via the shared recognizer path, tables via <see cref="ITableRecognizer"/>, formulas
/// via <see cref="IFormulaRecognizer"/>, seals via <see cref="ISealRecognizer"/> → assign reading order
/// with <see cref="ReadingOrder.XyCutOrderer"/> → assemble a <see cref="StructureResult"/>.
/// </para>
/// </summary>
internal sealed class StructureEngine : IAsyncDisposable
{
    private readonly StructureEngineOptions _options;
    private readonly ILogger? _logger;

    // The text-OCR path is the existing engine, reused for text/caption regions and (indirectly) for seals.
    private readonly StructureTextEngine _ocrEngine;

    // Session options resolved once for the structure models (single big graph per crop → not per-box parallel).
    private readonly SessionOptions _sessionOptions;
    private readonly OcrExecutionProvider _resolvedProvider;

    // Lazy single-instance caches, each guarded by its own lock (mirrors StructureTextEngine's detector/classifier).
    private IDocPreprocessor? _preprocessor;
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

    private ISealRecognizer? _sealRecognizer;
    private readonly SemaphoreSlim _sealLock = new(1, 1);

    public StructureEngine(StructureEngineOptions options, ILogger? logger)
    {
        _options = options;
        _logger = logger;
        _ocrEngine = new StructureTextEngine(options, logger);
        _resolvedProvider = ExecutionProviderResolver.Resolve(options.ExecutionProvider, logger);
        _sessionOptions = ExecutionProviderResolver.BuildSessionOptions(_resolvedProvider, options, logger, perBoxParallel: false);
    }

    /// <summary>
    /// The OCR engine used for the text-recognition path (exposed so the parent service can share/warm it).
    /// </summary>
    public StructureTextEngine OcrEngine => _ocrEngine;

    /// <summary>
    /// Runs the full document-structure pipeline over <paramref name="image"/> and returns the analyzed
    /// blocks in reading order.
    /// </summary>
    /// <param name="image">The page image to analyze (caller retains ownership).</param>
    /// <param name="options">Which stages / sub-recognizers to run and the recognition languages.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Pipeline: (1) doc-preprocess (orientation / unwarp per <paramref name="options"/>); (2) layout
    /// detection on the processed page; (3) per-region recognition — text/title/list/… regions are OCR'd by
    /// running the shared <see cref="StructureTextEngine"/> over the region crop (lines translated back to page
    /// coordinates), tables via <see cref="ITableRecognizer"/> (fed the region's own OCR lines for cell
    /// text), formulas via <see cref="IFormulaRecognizer"/>, seals via <see cref="ISealRecognizer"/>;
    /// (4) reading order via <see cref="ReadingOrder.XyCutOrderer"/>, written back into each block's
    /// <see cref="StructureBlock.Order"/>; (5) assemble a <see cref="StructureResult"/> carrying the
    /// processed-page dimensions. Toggles that are off skip their stage and fall back to plain text where
    /// applicable. The caller retains ownership of <paramref name="image"/>; any processed copy is disposed
    /// here.
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
        try
        {
            if (options.UseDocOrientation || options.UseUnwarp)
            {
                var preprocessor = await GetOrLoadPreprocessorAsync(options, ct).ConfigureAwait(false);
                var (processed, rotation) = preprocessor.Apply(image, options.UseDocOrientation, options.UseUnwarp);
                if (!ReferenceEquals(processed, image))
                {
                    page = processed;
                    owned = processed;
                }
                _logger?.LogInformation("Document pre-processing applied (rotation={Rotation} deg).", rotation);
            }

            // ---- (2) layout detection ----------------------------------------------------------------
            var layoutDetector = await GetOrLoadLayoutDetectorAsync(options.LayoutModel, ct).ConfigureAwait(false);
            var detected = layoutDetector.Detect(page, options.LayoutScoreThreshold);

            // The detectors emit a fixed top-k of candidates with no NMS, so the same area of the page is
            // routinely proposed several times under different labels. Clean that up (and apply the optional
            // NMS / unclip / merge passes) before anything is recognized.
            var regions = Layout.LayoutPostProcessor.Apply(detected, options, page.Width, page.Height);
            _logger?.LogInformation(
                "Layout detector found {Count} region(s) ({Raw} before post-processing).",
                regions.Count, detected.Count);

            if (regions.Count == 0)
            {
                return new StructureResult
                {
                    Blocks = Array.Empty<StructureBlock>(),
                    SourceWidth = page.Width,
                    SourceHeight = page.Height,
                };
            }

            // ---- (3) per-region recognition ----------------------------------------------------------
            var blocks = new List<StructureBlock>(regions.Count);
            foreach (var region in regions)
            {
                ct.ThrowIfCancellationRequested();
                blocks.Add(await AnalyzeRegionAsync(page, region, options, ct).ConfigureAwait(false));
            }

            // ---- (4) reading order -------------------------------------------------------------------
            // Prefer the order the model predicted for itself when it supplied one for every region —
            // LayoutPostProcessor has already sorted the regions (and therefore the blocks) by it, so the
            // ranking is the identity. Otherwise fall back to XY-cut over the region bounds. Either way the
            // rank is written into each block's Order and the blocks are emitted in that order, so downstream
            // exporters can rely on either.
            bool useModelOrder = options.ReadingOrder != LayoutReadingOrder.XyCut
                && regions.Count == blocks.Count
                && regions.All(r => r.OrderIndex is not null);
            var order = useModelOrder
                ? Enumerable.Range(0, blocks.Count).ToArray()
                : ReadingOrder.XyCutOrderer.Order(blocks.Select(b => b.Bounds).ToArray(), options.RightToLeft);
            var ordered = new StructureBlock[blocks.Count];
            for (int rank = 0; rank < order.Count; rank++)
            {
                var sourceIndex = order[rank];
                ordered[rank] = blocks[sourceIndex] with { Order = rank };
            }

            // ---- (5) assemble result -----------------------------------------------------------------
            return new StructureResult
            {
                Blocks = ordered,
                SourceWidth = page.Width,
                SourceHeight = page.Height,
            };
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>
    /// Recognizes a single layout region into a <see cref="StructureBlock"/>, dispatching on its
    /// <see cref="StructureBlockType"/>: tables -> <see cref="ITableRecognizer"/>, formulas ->
    /// <see cref="IFormulaRecognizer"/>, seals -> <see cref="ISealRecognizer"/>, everything text-like -> the
    /// shared OCR path. The region's bounds are clamped to the page before cropping; a degenerate region
    /// yields an empty block of its type. The block's <see cref="StructureBlock.Order"/> is provisionally 0
    /// and overwritten by the reading-order pass in <see cref="AnalyzeAsync"/>.
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
            case StructureBlockType.Table when options.RecognizeTables:
            {
                using var crop = page.Clone(ctx => ctx.Crop(rect));
                // Recognize the table's own text first (in crop coordinates) so the table recognizer can
                // distribute it into the predicted cells.
                var cropLines = await RecognizeCropTextAsync(crop, options, ct).ConfigureAwait(false);
                var recognizer = await GetOrLoadTableRecognizerAsync(options.TableModel, ct).ConfigureAwait(false);
                var table = recognizer.Recognize(crop, cropLines);
                var pageLines = TranslateLines(cropLines, rect.X, rect.Y);
                return new StructureBlock(
                    StructureBlockType.Table, region.Bounds, Order: 0,
                    TableHtml: table.Html, Lines: pageLines, Score: region.Score);
            }

            case StructureBlockType.Formula when options.RecognizeFormulas:
            {
                using var crop = page.Clone(ctx => ctx.Crop(rect));
                var recognizer = await GetOrLoadFormulaRecognizerAsync(ct).ConfigureAwait(false);
                var latex = recognizer.RecognizeLatex(crop);
                return new StructureBlock(
                    StructureBlockType.Formula, region.Bounds, Order: 0,
                    Latex: latex, Score: region.Score);
            }

            case StructureBlockType.Seal when options.RecognizeSeals:
            {
                using var crop = page.Clone(ctx => ctx.Crop(rect));
                var recognizer = await GetOrLoadSealRecognizerAsync(options, ct).ConfigureAwait(false);
                var sealLines = recognizer.Recognize(crop);
                var pageLines = TranslateLines(sealLines, rect.X, rect.Y);
                return new StructureBlock(
                    StructureBlockType.Seal, region.Bounds, Order: 0,
                    Text: JoinText(sealLines), Lines: pageLines, Score: region.Score);
            }

            case StructureBlockType.Figure:
            case StructureBlockType.Chart:
                // Picture-like regions carry no recoverable text; represented by bounds/type alone.
                return new StructureBlock(region.Type, region.Bounds, Order: 0, Score: region.Score);

            default:
            {
                // Text-like region (text, title, list, caption, header/footer, reference, …): OCR the crop.
                using var crop = page.Clone(ctx => ctx.Crop(rect));
                var cropLines = await RecognizeCropTextAsync(crop, options, ct).ConfigureAwait(false);
                var pageLines = TranslateLines(cropLines, rect.X, rect.Y);
                return new StructureBlock(
                    region.Type, region.Bounds, Order: 0,
                    Text: JoinText(cropLines), Lines: pageLines, Score: region.Score);
            }
        }
    }

    /// <summary>
    /// Smallest region side (px) worth cropping/recognizing; smaller regions yield an empty block.
    /// </summary>
    private const int MinRegionPx = 2;

    /// <summary>
    /// Runs the shared OCR recognizer (<see cref="StructureTextEngine.RecognizeAsync"/>) over a region crop and
    /// returns the recognized lines in the crop's pixel coordinates. The grouping is forced to
    /// <see cref="TextGrouping.Line"/> so structure blocks keep their constituent lines (the exporter joins
    /// them with newlines).
    /// </summary>
    private async Task<IReadOnlyList<OcrLine>> RecognizeCropTextAsync(
        Image<Rgb24> crop, StructureOptions options, CancellationToken ct)
    {
        var recOptions = new RecognitionOptions { Grouping = TextGrouping.Line };
        return await _ocrEngine.RecognizeAsync(crop, options.Languages.ToCodes(), recOptions, ct).ConfigureAwait(false);
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
    /// Translates OCR lines from a crop's pixel coordinates back into the page's coordinates by offsetting
    /// every polygon point and the axis-aligned box by the crop's top-left corner. A zero offset returns the
    /// input unchanged.
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

    /// <summary>
    /// Joins recognized lines into a single block-text string (newline-separated); null when empty.
    /// </summary>
    private static string? JoinText(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count == 0) return null;
        var text = string.Join("\n", lines.Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        return text.Length == 0 ? null : text;
    }

    // ===============================================================================================
    // Wired session/recognizer loaders. These download the registry assets via ModelDownloadManager and
    // build the ONNX-session-backed components, caching one instance each. The per-region body-fill agents
    // call these from AnalyzeAsync; the loaders themselves are complete.
    // ===============================================================================================

    /// <summary>
    /// Loads (once) the document pre-processor, wiring the doc-orientation and UVDoc unwarp sessions.
    /// </summary>
    public async Task<IDocPreprocessor> GetOrLoadPreprocessorAsync(StructureOptions options, CancellationToken ct)
    {
        if (_preprocessor is not null) return _preprocessor;
        await _preprocessorLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_preprocessor is not null) return _preprocessor;

            InferenceSession? orientation = options.UseDocOrientation
                ? await LoadSessionAsync(StructureModelRegistry.DocImageOrientation, ct).ConfigureAwait(false)
                : null;
            InferenceSession? unwarp = options.UseUnwarp
                ? await LoadSessionAsync(StructureModelRegistry.DocUnwarp, ct).ConfigureAwait(false)
                : null;

            _preprocessor = new DocPreprocessor(orientation, unwarp);
            _logger?.LogInformation("Document pre-processor loaded (orientation={Ori}, unwarp={Unwarp}).",
                options.UseDocOrientation, options.UseUnwarp);
            return _preprocessor;
        }
        finally
        {
            _preprocessorLock.Release();
        }
    }

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
    /// wired/wireless classifier and the two SLANeXt structure recognizers (512×512). Rebuilt if the requested
    /// model changes (mirrors the layout-detector cache).
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
    /// Builds the SLANeXt v2 router: the wired/wireless table classifier plus the two SLANeXt structure
    /// recognizers (512×512). SLANeXt shares SLANet's heads, so each leg is an ordinary
    /// <see cref="SlanetTableRecognizer"/> at the 512 input size.
    /// </summary>
    private async Task<ITableRecognizer> BuildSlaNeXtRouterAsync(CancellationToken ct)
    {
        var vocab = await LoadTableVocabAsync(ct).ConfigureAwait(false);
        var clsSession = await LoadSessionAsync(StructureModelRegistry.TableClassifier, ct).ConfigureAwait(false);
        var wiredSession = await LoadSessionAsync(StructureModelRegistry.SlaNeXtWired, ct).ConfigureAwait(false);
        var wirelessSession = await LoadSessionAsync(StructureModelRegistry.SlaNeXtWireless, ct).ConfigureAwait(false);

        const int slanextInputSize = 512;
        var classifier = new TableClassifier(clsSession);
        var wired = new SlanetTableRecognizer(wiredSession, vocab, slanextInputSize);
        var wireless = new SlanetTableRecognizer(wirelessSession, vocab, slanextInputSize);
        _logger?.LogInformation("Table-structure recognizer loaded (SLANeXt v2: table_cls + wired + wireless).");
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
    /// Loads (once) the seal recognizer (PP-OCRv4 seal detector + the shared text recognizer).
    /// </summary>
    public async Task<ISealRecognizer> GetOrLoadSealRecognizerAsync(StructureOptions options, CancellationToken ct)
    {
        if (_sealRecognizer is not null) return _sealRecognizer;
        await _sealLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sealRecognizer is not null) return _sealRecognizer;

            var sealDet = await LoadSessionAsync(StructureModelRegistry.SealDetector, ct).ConfigureAwait(false);
            var textRecognizer = await _ocrEngine.GetSharedRecognizerAsync(options.Languages.ToCodes(), ct).ConfigureAwait(false);

            _sealRecognizer = new SealRecognizer(sealDet, textRecognizer);
            _logger?.LogInformation("Seal recognizer loaded (PP-OCRv4 seal det + shared text recognizer).");
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
        await _ocrEngine.DisposeAsync().ConfigureAwait(false);

        _preprocessorLock.Dispose();
        _layoutLock.Dispose();
        _tableLock.Dispose();
        _formulaLock.Dispose();
        _sealLock.Dispose();
    }
}
