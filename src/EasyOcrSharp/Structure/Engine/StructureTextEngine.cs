using System.Collections.Concurrent;
using EasyOcrSharp.Structure.Engine.Classification;
using EasyOcrSharp.Structure.Engine.Detection;
using EasyOcrSharp.Structure.Engine.Geometry;
using EasyOcrSharp.Structure.Engine.Recognition;
using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure.Engine.Services;
using EasyOcrSharp.Structure.Preprocess;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;
using OcrWord = EasyOcrSharp.Models.OcrWord;

namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// Top-level coordinator: loads the DB detector once, lazy-loads the (optional) text-line orientation
/// classifier and the recognizer pack(s) per language, and runs the det → cls → rec → reading-order
/// pipeline producing <see cref="OcrLine"/>s. Thread-safe; all underlying ONNX sessions are reused
/// across calls. Mirrors the lazy-session-cache / WarmUp / DisposeAsync patterns of the reference engine.
/// <para>
/// The pipeline is PaddleOCR's <c>PaddleOCR.ocr()</c>: detect text quads → rectify each quad into an
/// upright crop (<see cref="PerspectiveWarp"/> / <c>get_rotate_crop_image</c>) → optionally flip 180°
/// boxes via the orientation classifier → recognize crops (batched) → drop low-confidence readings →
/// sort into reading order (<see cref="SortBoxes"/> / <c>sorted_boxes</c>) → assemble
/// <see cref="OcrLine"/>s. All ONNX sessions are loaded on demand via the wired loader plumbing
/// (<see cref="GetOrLoadDetectorAsync"/>, <see cref="GetOrLoadClassifierAsync"/>,
/// <see cref="GetOrLoadRecognizerAsync"/>).
/// </para>
/// </summary>
internal sealed class StructureTextEngine : IAsyncDisposable
{
    private readonly StructureEngineOptions _options;
    private readonly ILogger? _logger;

    // Session options for the resolved provider, shared by every session (detector, classifier,
    // recognizer). If an accelerated session fails to initialize at model-load time we build a matching
    // CPU set once and route all later sessions through it.
    private readonly SessionOptions _sessionOptions;
    private SessionOptions? _cpuFallbackSessionOptions;
    /// <summary>Rotated crops held in memory at once while confirming orientation verdicts.</summary>
    private const int OrientationConfirmChunk = 16;

    private volatile OcrExecutionProvider _activeProvider;
    private readonly object _fallbackLock = new();
    // One-time notice that a per-script pack has no server variant (RecognitionModel == Server).
    private int _serverVariantNoticeLogged;

    private ITextDetector? _detector;
    private readonly SemaphoreSlim _detectorLock = new(1, 1);

    private IAngleClassifier? _classifier;
    private readonly SemaphoreSlim _classifierLock = new(1, 1);

    // Document pre-processor (doc-orientation classifier + optional UVDoc unwarp) for the plain OCR
    // path — the same DocPreprocessor the structure engine uses, cached here keyed on which sessions it
    // holds (a later call needing a session the cached instance lacks rebuilds it with the union).
    // A failed load (e.g. model download unavailable offline) marks it unavailable once and the pipeline
    // proceeds without document pre-processing instead of failing every call.
    private IDocPreprocessor? _docPreprocessor;
    private bool _docPreprocessorHasOrientation;
    private bool _docPreprocessorHasUnwarp;
    private volatile bool _docPreprocessorUnavailable;
    private readonly SemaphoreSlim _docPreprocessorLock = new(1, 1);

    private readonly ConcurrentDictionary<string, Lazy<Task<ITextRecognizer>>> _recognizers =
        new(StringComparer.OrdinalIgnoreCase);

    public StructureTextEngine(StructureEngineOptions options, ILogger? logger)
    {
        _options = options;
        _logger = logger;
        ResolvedProvider = ExecutionProviderResolver.Resolve(options.ExecutionProvider, logger);
        // BuildSessionOptionsWithStatus reports whether the accelerator actually attached: when the
        // provider append fails the returned options are CPU-only and ActiveProvider says so, so the
        // engine never claims GPU while running on CPU.
        var build = ExecutionProviderResolver.BuildSessionOptionsWithStatus(ResolvedProvider, options, logger);
        _sessionOptions = build.Options;
        _activeProvider = build.ActiveProvider;
        GpuHint = BuildGpuHint(options.ExecutionProvider, ResolvedProvider, build.ProviderFailureHint, options.LogGpuHint, logger);
    }

    /// <summary>
    /// The accelerator the structure engine resolved to attempt at startup — <see cref="OcrExecutionProvider.Auto"/>
    /// is resolved to a concrete provider here. This reflects the initial attempt only; see
    /// <see cref="ActiveProvider"/> for what the sessions are actually running on.
    /// </summary>
    public OcrExecutionProvider ResolvedProvider { get; }

    /// <summary>
    /// The provider the ONNX sessions are actually running on right now. Starts as the provider whose
    /// append to the session options succeeded (CPU when the accelerator failed to attach) and degrades
    /// to CPU permanently if an accelerated session fails to initialize at model load.
    /// </summary>
    public OcrExecutionProvider ActiveProvider => _activeProvider;

    /// <summary>
    /// A one-time, actionable message explaining why OCR is running on CPU: either the requested/resolved
    /// accelerator failed to attach (with the exact fix, e.g. the CUDA-toolkit-major-mismatch hint) — set
    /// regardless of whether the provider was requested explicitly or via Auto — or
    /// <see cref="OcrExecutionProvider.Auto"/> fell back to CPU while a usable GPU is physically present
    /// (naming the provider package to install). Null when an accelerator is in use, CPU was chosen
    /// explicitly, or no GPU was detected.
    /// </summary>
    public string? GpuHint { get; }

    /// <summary>
    /// Runs the full pipeline (detect → optional classify → recognize) and returns the recognized lines
    /// in reading order. This is PaddleOCR's <c>ocr(det=True, rec=True)</c>: the DB detector locates text
    /// quadrilaterals, each quad is rectified into an upright crop, recognized, filtered by
    /// <see cref="RecognitionOptions.DropScore"/>, and sorted top-to-bottom / left-to-right.
    /// </summary>
    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(
        Image<Rgb24> image,
        IReadOnlyList<string> languages,
        RecognitionOptions options,
        CancellationToken cancellationToken)
        => (await RecognizeWithDetectedLanguagesAsync(image, languages, options, cancellationToken).ConfigureAwait(false)).Lines;

    /// <summary>
    /// Same as <see cref="RecognizeAsync"/> but also reports the language code(s) inferred when language
    /// auto-detection is active (see <see cref="RecognitionOptions.AutoDetectLanguage"/> or the <c>"auto"</c>
    /// language code) and the corrective page rotation applied by the document-orientation stage.
    /// When auto-detection did not run, <c>DetectedLanguages</c> is empty.
    /// <para>
    /// Document pre-processing (Python's <c>use_doc_preprocessor</c>): when
    /// <see cref="RecognitionOptions.UseDocOrientation"/> and/or <see cref="RecognitionOptions.UseDocUnwarp"/>
    /// is set, the page is uprighted (0/90/180/270° classifier) and/or dewarped (UVDoc) <b>before</b>
    /// detection. Recognition and reading order run in the uprighted frame; the returned quads are then
    /// inverse-rotated back into the <b>original</b> image's orientation (unwarp displacements are not
    /// invertible, so those coordinates stay in the unwarped frame — Python behaves the same).
    /// <c>AppliedRotation</c> is the clockwise rotation that was applied to upright the page (0 when none).
    /// </para>
    /// </summary>
    public async Task<(IReadOnlyList<OcrLine> Lines, IReadOnlyList<string> DetectedLanguages, int AppliedRotation)> RecognizeWithDetectedLanguagesAsync(
        Image<Rgb24> image,
        IReadOnlyList<string> languages,
        RecognitionOptions options,
        CancellationToken cancellationToken)
    {
        Image<Rgb24> page = image;
        Image<Rgb24>? owned = null;
        int appliedRotation = 0;
        try
        {
            if (options.UseDocOrientation || options.UseDocUnwarp)
            {
                using var preprocessingStage = StructureDiagnostics.MeasureStage("document_preprocess");
                var preprocessor = await GetOrLoadDocPreprocessorAsync(
                    options.UseDocOrientation, options.UseDocUnwarp, cancellationToken).ConfigureAwait(false);
                if (preprocessor is not null)
                {
                    // Apply returns the caller's image itself when no stage changed pixels (OwnsImage false);
                    // only an image it created is ours to dispose.
                    var preprocessed = preprocessor.Apply(image, options.UseDocOrientation, options.UseDocUnwarp);
                    page = preprocessed.Image;
                    if (preprocessed.OwnsImage) owned = preprocessed.Image;
                    appliedRotation = preprocessed.RotationApplied;
                    if (appliedRotation != 0)
                    {
                        _logger?.LogInformation("Document orientation: page rotated {Deg}° clockwise to upright.", appliedRotation);
                    }
                }
            }

            var detector = await GetOrLoadDetectorAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<TextQuad> quads;
            using (StructureDiagnostics.MeasureStage("detection"))
                quads = detector.Detect(page, options.Detection);
            _logger?.LogInformation("DB detector located {Count} text regions", quads.Count);
            if (quads.Count == 0) return (Array.Empty<OcrLine>(), Array.Empty<string>(), appliedRotation);

            // Optional de-duplication of overlapping detections. Off by default (NmsIouThreshold 0 makes
            // Reduce a no-op) — Python performs no post-detection NMS, and axis-aligned IoU can delete
            // legitimate adjacent rotated boxes. Opt back in via DetectionOptions.NmsIouThreshold.
            var polygons = BoxNms.Reduce(quads.Select(q => q.ToOcrPoints()).ToArray(), options.Detection.NmsIouThreshold);
            var detected = new List<string>();
            var lines = await RecognizePolygonsAsync(
                page, languages, polygons, options, detected, cancellationToken,
                pageWasReoriented: appliedRotation != 0).ConfigureAwait(false);

            // Map quads back into the ORIGINAL image's orientation (Python reports them in the rotated
            // frame; we deliberately don't — callers overlay boxes on the image they supplied). The list
            // order is untouched: it is the reading order of the uprighted page.
            if (appliedRotation != 0)
            {
                lines = OrientationMapper.MapLinesToOriginalFrame(lines, appliedRotation, page.Width, page.Height);
            }
            return (lines, detected, appliedRotation);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>
    /// Recognizes a caller-supplied set of region polygons (skipping detection) — PaddleOCR's
    /// <c>ocr(det=False)</c>. Applies the optional text-line orientation classifier and the requested
    /// grouping. Boxes are returned in reading order, in the image's pixel coordinates.
    /// </summary>
    public Task<IReadOnlyList<OcrLine>> RecognizeRegionsAsync(
        Image<Rgb24> image,
        IReadOnlyList<string> languages,
        IReadOnlyList<OcrPoint[]> polygons,
        RecognitionOptions options,
        CancellationToken cancellationToken)
        => RecognizePolygonsAsync(image, languages, polygons, options, detectedLanguagesSink: null, cancellationToken);

    /// <summary>
    /// Runs only the detector (no recognition) and returns the located regions in reading order.
    /// </summary>
    public async Task<IReadOnlyList<DetectedRegion>> DetectRegionsAsync(
        Image<Rgb24> image,
        DetectionOptions detection,
        TextGrouping grouping,
        CancellationToken cancellationToken)
    {
        var detector = await GetOrLoadDetectorAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TextQuad> quads;
        using (StructureDiagnostics.MeasureStage("detection"))
            quads = detector.Detect(image, detection);
        if (quads.Count == 0) return Array.Empty<DetectedRegion>();

        var polygons = BoxNms.Reduce(quads.Select(q => q.ToOcrPoints()).ToArray(), detection.NmsIouThreshold);
        var ordered = SortBoxes(polygons);

        var regions = new List<DetectedRegion>(ordered.Count);
        foreach (var poly in ordered)
        {
            regions.Add(new DetectedRegion
            {
                BoundingPolygon = poly,
                BoundingBox = OcrBoundingBox.FromPoints(poly),
            });
        }
        return regions;
    }

    /// <summary>
    /// Shared det-less core: rectify every polygon into an upright crop, optionally flip 180° crops,
    /// recognize the crops in batches, drop low-confidence readings, sort the survivors into reading order
    /// and (when requested) merge them into paragraphs.
    /// <para>
    /// When language auto-detection is active (see <see cref="IsAutoDetect"/>) the crops are recognized with
    /// every candidate pack and the best per-crop reading is kept; the winning language code(s) are appended
    /// to <paramref name="detectedLanguagesSink"/> when one is supplied.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<OcrLine>> RecognizePolygonsAsync(
        Image<Rgb24> image,
        IReadOnlyList<string> languages,
        IReadOnlyList<OcrPoint[]> polygons,
        RecognitionOptions options,
        List<string>? detectedLanguagesSink,
        CancellationToken cancellationToken,
        bool pageWasReoriented = false)
    {
        if (polygons.Count == 0) return Array.Empty<OcrLine>();

        bool auto = IsAutoDetect(languages, options);

        // For the normal (non-auto) path, resolve the single requested pack up front so a wholly
        // unsupported language set short-circuits before any cropping work.
        RecognizerPack? fixedPack = null;
        if (!auto)
        {
            fixedPack = ResolvePacks(languages).FirstOrDefault();
            if (fixedPack is null) return Array.Empty<OcrLine>();
        }

        // The per-call orientation flag wins when set explicitly; otherwise the service-level option applies
        // when it was set. The classifier session is loaded on demand and reused thereafter.
        IAngleClassifier? classifier = null;
        if (ResolveUseTextLineOrientation(options, _options.UseTextLineOrientation))
        {
            classifier = await GetOrLoadClassifierAsync(cancellationToken).ConfigureAwait(false);
        }

        int parallelism = BoundedParallel.ResolveDegree(options.MaxDegreeOfParallelism);
        bool withWords = options.ReturnWordBoxes;

        // Rectify each quad into an upright crop, in parallel (each polygon is independent and the page is
        // only read). A null slot means the polygon was degenerate (sub-2px after rectification); it is
        // skipped while the surviving crops keep their polygons' order, so recognition results line back up
        // with their source polygons.
        var slots = new Image<Rgb24>?[polygons.Count];
        var slotGeometry = new CropGeometry[polygons.Count];
        try
        {
            BoundedParallel.For(polygons.Count, parallelism, cancellationToken, i =>
            {
                // rotateVertical: tall crops (h/w >= 1.5, i.e. vertical text lines) are rotated 90° CCW
                // inside Rectify so the recognizer sees them horizontally — PaddleOCR's
                // get_rotate_crop_image np.rot90 rule.
                var crop = PerspectiveWarp.Rectify(image, polygons[i], rotateVertical: true, out slotGeometry[i]);
                if (crop is null) return;

                // Optional white padding around the crop (GitHub issue #6): applied before the 180°
                // classifier so classification and recognition see identical pixels.
                if (options.CropPadding > 0)
                {
                    try
                    {
                        slots[i] = PadCrop(crop, options.CropPadding);
                    }
                    finally
                    {
                        crop.Dispose();
                    }
                    return;
                }

                slots[i] = crop;
            });

            var crops = new List<Image<Rgb24>>(polygons.Count);
            var cropPolygons = new List<OcrPoint[]>(polygons.Count);
            var cropGeometry = new List<CropGeometry>(polygons.Count);
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] is not { } crop) continue;
                crops.Add(crop);
                cropPolygons.Add(polygons[i]);
                cropGeometry.Add(slotGeometry[i]);
            }

            if (crops.Count == 0) return Array.Empty<OcrLine>();

            // Optional text-line orientation, classified in batches. The classifier's verdict is only a
            // candidate: it is confirmed later by recognizing both orientations and keeping the more
            // confident reading (see VerifyOrientationByRecognition), because a confidently-wrong flip
            // destroys an upright line outright.
            var flipCandidates = new List<int>();
            var flippedInPlace = new bool[crops.Count];
            if (classifier is not null)
            {
                var verdicts = classifier.Classify(crops, parallelism);
                for (int i = 0; i < crops.Count; i++)
                {
                    var (rotated, score) = verdicts[i];
                    if (!(rotated && score >= options.TextLineOrientationThreshold)) continue;

                    if (options.VerifyOrientationByRecognition)
                    {
                        flipCandidates.Add(i);
                    }
                    else
                    {
                        crops[i].Mutate(ctx => ctx.Rotate(RotateMode.Rotate180));
                        flippedInPlace[i] = true;
                    }
                }
            }

            // A wrong page-level orientation verdict corrupts every line at once (PP-LCNet_x1_0_doc_ori
            // does misfire on upright pages), and the text-line classifier does not reliably flag the
            // result. So when the page was reoriented, every crop is verified against its 180° twin and
            // the more confident reading wins — the page decision is confirmed line by line.
            if (pageWasReoriented && options.VerifyOrientationByRecognition)
            {
                flipCandidates.Clear();
                for (int i = 0; i < crops.Count; i++) flipCandidates.Add(i);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Per-crop best reading: either from the single requested pack, or — when auto-detecting — the
            // highest-confidence reading across all candidate packs. The classifier's 180° verdicts are then
            // confirmed: the flagged crops are recognized a second time upside-down and the more confident
            // reading wins. On CPU both passes share the thread-safe session and run concurrently.
            int concurrency = RecognitionBatchConcurrency(options);
            IReadOnlyList<RecognizedText> readings;
            if (flipCandidates.Count > 0 && concurrency > 1)
            {
                var uprightTask = Task.Run(() => RecognizeCropsAsync(
                    crops, options, auto, fixedPack, detectedLanguagesSink, concurrency, withWords, cancellationToken), cancellationToken);
                var flippedTask = Task.Run(() => RecognizeFlippedAsync(
                    crops, flipCandidates, options, auto, fixedPack, concurrency, withWords, cancellationToken), cancellationToken);
                // WhenAll only completes once both passes have finished, so no pass can outlive the crops.
                await Task.WhenAll(uprightTask, flippedTask).ConfigureAwait(false);
                readings = CombineOrientation(uprightTask.Result, flippedTask.Result, flipCandidates);
            }
            else
            {
                readings = await RecognizeCropsAsync(
                    crops, options, auto, fixedPack, detectedLanguagesSink, concurrency, withWords, cancellationToken).ConfigureAwait(false);
                if (flipCandidates.Count > 0)
                {
                    var flipped = await RecognizeFlippedAsync(
                        crops, flipCandidates, options, auto, fixedPack, concurrency, withWords, cancellationToken).ConfigureAwait(false);
                    readings = CombineOrientation(readings, flipped, flipCandidates);
                }
            }

            // Drop low-confidence / empty readings (PaddleOCR's drop_score), then keep each surviving
            // line paired with its source polygon.
            var lines = new List<OcrLine>(crops.Count);
            for (int i = 0; i < crops.Count && i < readings.Count; i++)
            {
                var reading = readings[i];
                if (string.IsNullOrWhiteSpace(reading.Text) || reading.Confidence < options.DropScore) continue;

                var poly = cropPolygons[i];
                lines.Add(new OcrLine
                {
                    Text = reading.Text,
                    Confidence = reading.Confidence,
                    BoundingPolygon = poly,
                    BoundingBox = OcrBoundingBox.FromPoints(poly),
                    Words = withWords
                        ? WordBoxBuilder.Build(reading, cropGeometry[i], crops[i].Width, crops[i].Height,
                            options.CropPadding, flipped: flippedInPlace[i] || reading.Flipped)
                        : Array.Empty<OcrWord>(),
                });
            }

            bool rightToLeft = EasyOcrSharp.Internal.ScriptDirection.IsRightToLeft(languages);
            var ordered = SortLines(lines, rightToLeft);
            return options.Grouping switch
            {
                TextGrouping.Paragraph => ParagraphGrouper.Merge(ordered, rightToLeft: rightToLeft),
                TextGrouping.Line => LineGrouper.Merge(ordered),
                _ => ordered,
            };
        }
        finally
        {
            foreach (var crop in slots) crop?.Dispose();
        }
    }

    /// <summary>
    /// Resolves whether the text-line orientation classifier runs for a call: a value set explicitly on
    /// <paramref name="options"/> wins; otherwise the service-level <paramref name="serviceDefault"/> applies
    /// when it was set; otherwise the per-call default (on, the Python pipeline default) applies.
    /// </summary>
    /// <param name="options">The call's recognition options.</param>
    /// <param name="serviceDefault">The service-level setting, or null when it was never set.</param>
    internal static bool ResolveUseTextLineOrientation(RecognitionOptions options, bool? serviceDefault)
        => options.UseTextLineOrientationSpecified
            ? options.UseTextLineOrientation
            : serviceDefault ?? options.UseTextLineOrientation;

    /// <summary>
    /// Upper bound on concurrently running recognition batches on the CPU provider when ONNX Runtime uses its
    /// default intra-op thread pool (measured: more concurrent runs only contend for the same pool).
    /// </summary>
    private const int MaxCpuConcurrentBatches = 2;

    /// <summary>
    /// Upper bound on concurrently running recognition batches when the intra-op thread count is pinned.
    /// </summary>
    private const int MaxPinnedConcurrentBatches = 4;

    /// <summary>
    /// How many recognition batches may run at once on a shared session. Accelerated providers stay
    /// sequential (DirectML requires it and CUDA gains little); on CPU it is bounded by
    /// <see cref="RecognitionOptions.MaxDegreeOfParallelism"/> and by how many intra-op pools fit on the machine.
    /// </summary>
    private int RecognitionBatchConcurrency(RecognitionOptions options)
    {
        if (_activeProvider != OcrExecutionProvider.Cpu) return 1;

        int cap = _options.IntraOpNumThreads is int intra and > 0
            ? Math.Clamp(Environment.ProcessorCount / intra, 1, MaxPinnedConcurrentBatches)
            : MaxCpuConcurrentBatches;
        return Math.Clamp(BoundedParallel.ResolveDegree(options.MaxDegreeOfParallelism), 1, cap);
    }

    /// <summary>
    /// Picks the better of two readings of the same crop — as recognized upright and rotated 180°.
    /// </summary>
    /// <remarks>
    /// Recognition confidence is the arbiter: the correct orientation reads cleanly while the inverted
    /// one decodes as gibberish, so the higher-confidence reading is kept. A blank reading never wins
    /// (an empty result carries no evidence), but it always loses to a non-blank one.
    /// </remarks>
    internal static (string Text, float Confidence) ChooseOrientation(
        (string Text, float Confidence) upright,
        (string Text, float Confidence) flipped)
        => PreferFlipped(upright.Text, upright.Confidence, flipped.Text, flipped.Confidence) ? flipped : upright;

    /// <summary>
    /// The <see cref="ChooseOrientation"/> rule: true when the flipped reading should replace the upright one.
    /// </summary>
    private static bool PreferFlipped(string uprightText, float uprightConfidence, string flippedText, float flippedConfidence)
    {
        if (string.IsNullOrWhiteSpace(flippedText)) return false;
        if (string.IsNullOrWhiteSpace(uprightText)) return true;
        return flippedConfidence > uprightConfidence;
    }

    /// <summary>
    /// Recognizes crops with the fixed pack (applying its post-processing) or, when auto-detecting, with
    /// every candidate pack.
    /// </summary>
    private async Task<IReadOnlyList<RecognizedText>> RecognizeCropsAsync(
        IReadOnlyList<Image<Rgb24>> crops,
        RecognitionOptions options,
        bool auto,
        RecognizerPack? fixedPack,
        List<string>? detectedLanguagesSink,
        int concurrency,
        bool withWords,
        CancellationToken cancellationToken)
    {
        if (auto)
        {
            return await RecognizeAutoAsync(crops, options, detectedLanguagesSink, concurrency, withWords, cancellationToken).ConfigureAwait(false);
        }

        var recognizer = await GetOrLoadRecognizerAsync(fixedPack!, cancellationToken).ConfigureAwait(false);
        return ApplyPackPostProcessing(fixedPack!, recognizer.RecognizeDetailed(crops, options, concurrency, withWords, cancellationToken));
    }

    /// <summary>
    /// Re-recognizes the crops the orientation classifier flagged as upside-down, rotated 180°. The result is
    /// aligned with <paramref name="flipCandidates"/>; <see cref="CombineOrientation"/> keeps whichever
    /// orientation the recognizer is more confident about.
    /// </summary>
    /// <remarks>
    /// The classifier (PP-LCNet_x1_0_textline_ori) misfires on some upright lines — including
    /// confidently — and acting on a wrong verdict replaces a good reading with transliterated gibberish.
    /// Recognition confidence separates the two cases cleanly (a correct orientation typically scores
    /// ~0.95+ against ~0.35 for the inverted one), so it is used as the arbiter. Only flagged crops pay
    /// for the second pass. PaddleX 3.x has no such confirmation; this is a deliberate improvement.
    /// </remarks>
    private async Task<RecognizedText?[]> RecognizeFlippedAsync(
        List<Image<Rgb24>> crops,
        List<int> flipCandidates,
        RecognitionOptions options,
        bool auto,
        RecognizerPack? fixedPack,
        int concurrency,
        bool withWords,
        CancellationToken cancellationToken)
    {
        var flipped = new RecognizedText?[flipCandidates.Count];

        // Rotated copies are made a chunk at a time. A reoriented page seeds every line as a candidate,
        // so cloning them all at once would hold a second copy of the whole page's crops in memory —
        // costly on the near-native-resolution scans the current detection defaults produce.
        for (int start = 0; start < flipCandidates.Count; start += OrientationConfirmChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int count = Math.Min(OrientationConfirmChunk, flipCandidates.Count - start);
            var rotated = new List<Image<Rgb24>>(count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var copy = crops[flipCandidates[start + i]].Clone();
                    copy.Mutate(ctx => ctx.Rotate(RotateMode.Rotate180));
                    rotated.Add(copy);
                }

                // Auto-detection runs its own per-pack sweep; a fixed pack recognizes the batch directly.
                var rotatedReadings = await RecognizeCropsAsync(
                    rotated, options, auto, fixedPack, null, concurrency, withWords, cancellationToken).ConfigureAwait(false);

                for (int i = 0; i < count && i < rotatedReadings.Count; i++)
                {
                    flipped[start + i] = rotatedReadings[i] with { Flipped = true };
                }
            }
            finally
            {
                foreach (var image in rotated) image.Dispose();
            }
        }

        return flipped;
    }

    /// <summary>
    /// Applies the orientation confirmation: for every flagged crop, the flipped reading replaces the upright
    /// one when <see cref="ChooseOrientation"/> prefers it.
    /// </summary>
    private static RecognizedText[] CombineOrientation(
        IReadOnlyList<RecognizedText> upright, RecognizedText?[] flipped, List<int> flipCandidates)
    {
        var confirmed = upright.ToArray();
        for (int k = 0; k < flipCandidates.Count && k < flipped.Length; k++)
        {
            int index = flipCandidates[k];
            if (index >= confirmed.Length || flipped[k] is not { } candidate) continue;

            if (PreferFlipped(confirmed[index].Text, confirmed[index].Confidence, candidate.Text, candidate.Confidence))
                confirmed[index] = candidate;
        }
        return confirmed;
    }

    /// <summary>
    /// Returns a copy of <paramref name="crop"/> centered on a white canvas with a
    /// <paramref name="padding"/>-px border on every side (<see cref="RecognitionOptions.CropPadding"/>).
    /// The caller still owns (and disposes) the original crop.
    /// </summary>
    private static Image<Rgb24> PadCrop(Image<Rgb24> crop, int padding)
    {
        var padded = new Image<Rgb24>(crop.Width + 2 * padding, crop.Height + 2 * padding, new Rgb24(255, 255, 255));
        padded.Mutate(ctx => ctx.DrawImage(crop, new Point(padding, padding), 1f));
        return padded;
    }

    /// <summary>
    /// Applies pack-specific output fixes: the Arabic pack's readings come out of CTC decoding in logical
    /// (typing) order, which renders reversed — PaddleOCR runs bidi <c>get_display</c> on them; this is the
    /// equivalent. Other packs pass through unchanged.
    /// </summary>
    private static IReadOnlyList<RecognizedText> ApplyPackPostProcessing(
        RecognizerPack pack, IReadOnlyList<RecognizedText> readings)
    {
        if (!pack.Name.Contains("arabic", StringComparison.OrdinalIgnoreCase)) return readings;

        var reordered = new RecognizedText[readings.Count];
        for (int i = 0; i < readings.Count; i++)
        {
            reordered[i] = readings[i] with
            {
                Text = RtlTextReorder.ApplyDisplayOrder(readings[i].Text),
                RightToLeft = true,
            };
        }
        return reordered;
    }

    // ---- language auto-detection ----

    /// <summary>
    /// The mean default-pack confidence at/above which the Latin/CJK fast path accepts without trying other packs.
    /// </summary>
    private const double AutoFastPathConfidence = 0.85;

    /// <summary>
    /// How much less confident a reading backed by script evidence may be and still beat a Latin reading in
    /// the multi-pack vote (<see cref="IsBetterReading"/>).
    /// </summary>
    internal const float ScriptEvidenceMargin = 0.10f;

    /// <summary>
    /// Whether <paramref name="candidate"/> should replace <paramref name="current"/> as a crop's reading in
    /// the multi-pack vote. Normally the more confident reading wins. But confidences from different packs
    /// are not calibrated against each other, and the default pack — which has no Cyrillic, Greek, Arabic,
    /// Indic, Thai or Hangul letters — reads such a word as confident Latin look-alikes: a Russian
    /// <c>ВЫХОД</c> sign came back as <c>BbIXOA</c> at 0.969 against the Cyrillic pack's correct 0.966.
    /// Every script pack's dictionary also holds the Latin letters, so when a pack picks a letter of its own
    /// script that has no Latin twin (<see cref="ScriptDetection.DistinctiveScript"/>), that choice is
    /// evidence. Such a reading beats a Latin one unless it is less confident by more than
    /// <see cref="ScriptEvidenceMargin"/>. Readings made only of look-alike letters (<c>ЕХІТ</c> for
    /// <c>EXIT</c>) carry no such evidence and are compared on confidence alone.
    /// </summary>
    internal static bool IsBetterReading(RecognizedText candidate, RecognizedText current)
    {
        var candidateScript = ScriptDetection.DistinctiveScript(candidate.Text);
        var currentScript = ScriptDetection.DistinctiveScript(current.Text);
        if (candidateScript != currentScript)
        {
            if (candidateScript != DetectedScript.Unknown && currentScript == DetectedScript.Unknown
                && ScriptDetection.DominantScript(current.Text) == DetectedScript.Latin)
            {
                return candidate.Confidence >= current.Confidence - ScriptEvidenceMargin;
            }

            if (currentScript != DetectedScript.Unknown && candidateScript == DetectedScript.Unknown
                && ScriptDetection.DominantScript(candidate.Text) == DetectedScript.Latin)
            {
                return candidate.Confidence > current.Confidence + ScriptEvidenceMargin;
            }
        }

        return candidate.Confidence > current.Confidence;
    }

    /// <summary>
    /// The curated cross-script candidate shortlist used when auto-detection is requested without an explicit
    /// <see cref="RecognitionOptions.AutoDetectCandidates"/>: the default PP-OCRv5 pack ("ch") plus the major
    /// per-script packs. Kept small so a worst-case run downloads ~11 (not all) recognizers on demand.
    /// </summary>
    private static readonly string[] DefaultAutoDetectCandidates =
    {
        "ch", "latin", "cyrillic", "arabic", "devanagari", "korean", "japan", "thai", "greek", "telugu", "tamil",
    };

    /// <summary>
    /// True when the caller asked for language auto-detection — either by setting
    /// <see cref="RecognitionOptions.AutoDetectLanguage"/> or by passing the literal language code
    /// <c>"auto"</c> in <paramref name="languages"/> (case-insensitive).
    /// </summary>
    private static bool IsAutoDetect(IReadOnlyList<string> languages, RecognitionOptions options)
        => options.AutoDetectLanguage
           || languages.Any(l => string.Equals(l, "auto", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Recognizes every crop with each candidate recognizer pack and keeps, per crop, the highest-confidence
    /// reading across packs. The pack that wins the most crops (weighted by confidence) is the detected
    /// language; its representative code (plus any runners-up that won crops) is appended to
    /// <paramref name="detectedLanguagesSink"/>. Each pack is loaded — and auto-downloaded if missing — via
    /// <see cref="GetOrLoadRecognizerAsync"/>.
    /// <para>
    /// Fast path: the default PP-OCRv5 pack is tried first; if its mean confidence over all crops is at least
    /// <see cref="AutoFastPathConfidence"/> and the recognized text is dominantly Latin/CJK, that result is
    /// accepted and the remaining candidates are skipped — so a clean English page never downloads ten extra
    /// models.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<RecognizedText>> RecognizeAutoAsync(
        IReadOnlyList<Image<Rgb24>> crops,
        RecognitionOptions options,
        List<string>? detectedLanguagesSink,
        int concurrency,
        bool withWords,
        CancellationToken cancellationToken)
    {
        // Resolve candidate codes → distinct packs, preserving order (default pack first so the fast path
        // can short-circuit on it).
        var candidateCodes = options.AutoDetectCandidates is { Count: > 0 }
            ? options.AutoDetectCandidates
            : DefaultAutoDetectCandidates;
        var candidatePacks = ResolveOrderedPacks(candidateCodes);
        if (candidatePacks.Count == 0)
        {
            // No candidate resolved: fall back to the default recognizer so we still return readings.
            var fallback = await GetOrLoadRecognizerAsync(StructureModelRegistry.MobileRecognizer, cancellationToken).ConfigureAwait(false);
            return fallback.RecognizeDetailed(crops, options, concurrency, withWords, cancellationToken);
        }

        int n = crops.Count;
        var best = new RecognizedText[n];
        var bestPack = new RecognizerPack?[n];
        // Per-pack tally of crops won, weighted by the winning confidence — the detection vote.
        var packScore = new Dictionary<string, double>(StringComparer.Ordinal);

        for (int p = 0; p < candidatePacks.Count; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pack = candidatePacks[p];
            var recognizer = await GetOrLoadRecognizerAsync(pack, cancellationToken).ConfigureAwait(false);
            var readings = ApplyPackPostProcessing(pack, recognizer.RecognizeDetailed(crops, options, concurrency, withWords, cancellationToken));

            double confSum = 0;
            for (int i = 0; i < n && i < readings.Count; i++)
            {
                var reading = readings[i];
                confSum += reading.Confidence;
                // Keep this pack's reading for crop i if it beats the best so far (or is the first reading).
                if (bestPack[i] is null || IsBetterReading(reading, best[i]))
                {
                    best[i] = reading;
                    bestPack[i] = pack;
                }
            }

            // Fast path: after the very first (default) pack, accept immediately when it is already confident
            // and the page is dominantly Latin/CJK — avoids downloading the remaining candidate models.
            // IsDefaultRecognizerPack (not a mobile-name check) so the server-variant substitution
            // (RecognitionModel == Server) keeps the fast path alive.
            if (p == 0 && IsDefaultRecognizerPack(pack) && n > 0)
            {
                double meanConf = confSum / n;
                string combined = string.Concat(readings.Take(n).Select(r => r.Text));
                var script = ScriptDetection.DominantScript(combined);
                bool latinOrCjk = script is DetectedScript.Latin or DetectedScript.Han
                    or DetectedScript.Kana or DetectedScript.Hangul;
                if (meanConf >= AutoFastPathConfidence && latinOrCjk)
                {
                    // Report the script the default pack actually read (latin/ch/japan/korean), which is more
                    // informative than the pack's first registered code.
                    if (detectedLanguagesSink is not null)
                    {
                        var code = ScriptDetection.ToLanguageCode(script) ?? pack.Languages.FirstOrDefault() ?? pack.Name;
                        if (!detectedLanguagesSink.Contains(code, StringComparer.OrdinalIgnoreCase))
                            detectedLanguagesSink.Add(code);
                    }
                    _logger?.LogInformation(
                        "Auto-detect fast path: default pack mean confidence {Conf:F2} on {Script} text; skipping {Remaining} other candidates.",
                        meanConf, script, candidatePacks.Count - 1);
                    return best;
                }
            }
        }

        // Tally the vote: each crop's winning pack gets its winning confidence added to its running score.
        for (int i = 0; i < n; i++)
        {
            var pack = bestPack[i];
            if (pack is null) continue;
            packScore[pack.Name] = packScore.GetValueOrDefault(pack.Name) + best[i].Confidence;
        }

        // Report the winning language(s): packs that actually won crops, ordered by weighted score (most
        // first). Map each back to its representative language code.
        var ranked = candidatePacks
            .Where(pk => packScore.ContainsKey(pk.Name))
            .OrderByDescending(pk => packScore[pk.Name])
            .Select(pk => (pk, packScore[pk.Name]))
            .ToArray();
        AppendDetectedLanguages(detectedLanguagesSink, ranked);

        if (ranked.Length > 0)
        {
            _logger?.LogInformation("Auto-detect chose '{Lang}' (won the weighted crop vote across {Packs} packs).",
                ranked[0].pk.Languages.FirstOrDefault() ?? ranked[0].pk.Name, candidatePacks.Count);
        }

        return best;
    }

    /// <summary>
    /// Appends the representative language code of each scored pack (highest score first) to
    /// <paramref name="sink"/>, de-duplicating. No-op when <paramref name="sink"/> is null.
    /// </summary>
    private static void AppendDetectedLanguages(List<string>? sink, IReadOnlyList<(RecognizerPack Pack, double Score)> ranked)
    {
        if (sink is null) return;
        foreach (var (pack, _) in ranked)
        {
            var code = pack.Languages.FirstOrDefault() ?? pack.Name;
            if (!sink.Contains(code, StringComparer.OrdinalIgnoreCase)) sink.Add(code);
        }
    }

    /// <summary>
    /// Resolves language codes to their recognizer packs, preserving the input order and dropping duplicate
    /// packs (two codes can map to the same pack) and unsupported codes. Unlike <see cref="ResolvePacks"/>
    /// this keeps the caller's ordering, which the auto-detect fast path relies on (default pack first).
    /// </summary>
    private IReadOnlyList<RecognizerPack> ResolveOrderedPacks(IReadOnlyList<string> languages)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packs = new List<RecognizerPack>(languages.Count);
        foreach (var lang in languages)
        {
            if (string.Equals(lang, "auto", StringComparison.OrdinalIgnoreCase)) continue;
            var pack = StructureModelRegistry.FindByLanguage(lang);
            if (pack is null)
            {
                _logger?.LogWarning("Auto-detect candidate '{Lang}' is not supported by any recognizer pack.", lang);
                continue;
            }
            if (seen.Add(pack.Name)) packs.Add(pack);
        }
        return packs;
    }

    /// <summary>
    /// Eagerly loads the DB detector, the recognizer pack(s) for the given languages, and (when enabled)
    /// the text-line classifier so the first real OCR call doesn't pay cold-start latency.
    /// </summary>
    public async Task WarmUp(IReadOnlyList<string> languages, CancellationToken cancellationToken)
    {
        var warmDetector = await GetOrLoadDetectorAsync(cancellationToken).ConfigureAwait(false);
        IAngleClassifier? warmClassifier = null;
        // RecognitionOptions.UseTextLineOrientation defaults to true (the Python pipeline default), so the
        // classifier participates in a default recognition call unless the service-level option turned it
        // off — pre-load it whenever a default call would bring it in.
        if (ResolveUseTextLineOrientation(RecognitionOptions.Default, _options.UseTextLineOrientation))
        {
            warmClassifier = await GetOrLoadClassifierAsync(cancellationToken).ConfigureAwait(false);
        }
        // Doc orientation defaults on too (RecognitionOptions.UseDocOrientation, the Python pipeline
        // default) — pre-load its classifier as well. A failed load degrades gracefully (never throws).
        if (RecognitionOptions.Default.UseDocOrientation)
        {
            await GetOrLoadDocPreprocessorAsync(needOrientation: true, needUnwarp: false, cancellationToken).ConfigureAwait(false);
        }
        Detection.ModelWarmUp.Run(warmDetector, warmClassifier, Array.Empty<ITextRecognizer>(), _logger);
        foreach (var pack in ResolvePacks(languages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recognizer = await GetOrLoadRecognizerAsync(pack, cancellationToken).ConfigureAwait(false);
            Detection.ModelWarmUp.Run(null, null, new[] { recognizer }, _logger);
        }
        // Loading a session does not run it: one dummy inference per model moves kernel selection and
        // arena growth out of the first real request.
    }

    /// <summary>
    /// Returns the shared <see cref="ITextRecognizer"/> for the first resolvable language (falling back to
    /// the default pack), loading and caching it via the same on-demand path as the OCR pipeline. Used by
    /// the structure subsystem (e.g. the seal recognizer) so it reuses the one cached recognizer session
    /// rather than loading its own. The returned recognizer is owned by this engine and disposed by it.
    /// </summary>
    public Task<ITextRecognizer> GetSharedRecognizerAsync(IReadOnlyList<string> languages, CancellationToken cancellationToken)
    {
        var pack = ResolvePacks(languages).FirstOrDefault() ?? StructureModelRegistry.MobileRecognizer;
        return GetOrLoadRecognizerAsync(pack, cancellationToken);
    }

    /// <summary>
    /// Resolves the distinct recognizer packs for the requested languages (unsupported codes are skipped).
    /// </summary>
    private IReadOnlyList<RecognizerPack> ResolvePacks(IReadOnlyList<string> languages)
    {
        var packs = new Dictionary<string, RecognizerPack>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in languages)
        {
            var pack = StructureModelRegistry.FindByLanguage(lang);
            if (pack is null)
            {
                _logger?.LogWarning("Language '{Lang}' is not supported by any recognizer pack.", lang);
                continue;
            }
            packs[pack.Name] = pack;
        }
        return packs.Values.ToArray();
    }

    // ---- reading order (PaddleOCR's sorted_boxes; algorithm extracted to SortedBoxes) ----

    /// <summary>
    /// Orders detected polygons into reading order — PaddleOCR's <c>sorted_boxes</c>. Boxes are stably
    /// sorted by their ordered quad's <b>first point</b> (the top-left corner, matching Python's
    /// <c>box[0]</c> key — not the axis-aligned bbox, which differs for rotated quads), then a single
    /// adjacent-swap (bubble) pass fixes pairs that belong to the same text line (their first-point y
    /// differs by less than <see cref="SortedBoxes.SameLineTolerance"/>) but landed out of left-to-right order.
    /// </summary>
    private static IReadOnlyList<OcrPoint[]> SortBoxes(IReadOnlyList<OcrPoint[]> polygons)
    {
        if (polygons.Count <= 1) return polygons;
        var boxes = polygons.ToList();
        SortedBoxes.Sort(boxes, p => p[0]);
        return boxes;
    }

    /// <summary>
    /// Orders recognized lines into reading order using the same <c>sorted_boxes</c> rule as
    /// <see cref="SortBoxes"/>, keying off each line's polygon's first (top-left) point.
    /// </summary>
    private static List<OcrLine> SortLines(List<OcrLine> lines, bool rightToLeft = false)
    {
        if (lines.Count <= 1) return lines;
        if (!rightToLeft)
        {
            // Python-parity sorted_boxes (top-left corner keyed); RTL pages keep the mirrored ordering below.
            SortedBoxes.Sort(lines, l => l.BoundingPolygon[0]);
            return lines;
        }

        SortByReadingOrder(lines, l => l.BoundingBox, rightToLeft);
        return lines;
    }

    /// <summary>
    /// In-place <c>sorted_boxes</c>: a stable sort by top-y then left-x, followed by a one-pass bubble
    /// that swaps an out-of-order neighbour pair that actually sits on the same text line.
    /// </summary>
    private static void SortByReadingOrder<T>(List<T> items, Func<T, OcrBoundingBox> boxOf, bool rightToLeft = false)
    {
        // Stable primary sort: top-y, then left-x. List.Sort is not stable, so use OrderBy/ThenBy (stable)
        // and copy back — matching numpy's lexicographic sort used by PaddleOCR. On a right-to-left page the
        // horizontal key inverts: right-most first, keyed on MaxX so a short fragment to the right of a
        // longer one still leads.
        var byRow = items.OrderBy(i => boxOf(i).MinY);
        var sorted = (rightToLeft
            ? byRow.ThenByDescending(i => boxOf(i).MaxX)
            : byRow.ThenBy(i => boxOf(i).MinX))
            .ToList();
        for (int i = 0; i < items.Count; i++) items[i] = sorted[i];

        // Bubble pass: when two adjacent boxes are within the same-line tolerance vertically but the
        // earlier one is further right, swap them so the line reads left-to-right. The inner loop walks
        // back while the pair stays on the same line; the `else break` is load-bearing — the moment a
        // neighbour is on a *different* line we must stop, otherwise a later same-line pair could pull a
        // box across a line boundary and corrupt the vertical order.
        for (int i = 0; i < items.Count - 1; i++)
        {
            for (int j = i; j >= 0; j--)
            {
                var a = boxOf(items[j + 1]);
                var b = boxOf(items[j]);
                bool outOfOrder = rightToLeft ? a.MaxX > b.MaxX : a.MinX < b.MinX;
                if (Math.Abs(a.MinY - b.MinY) < SortedBoxes.SameLineTolerance && outOfOrder)
                {
                    (items[j], items[j + 1]) = (items[j + 1], items[j]);
                }
                else
                {
                    break;
                }
            }
        }
    }

    // ---- session loading (wired; reused by the pipeline methods) ----

    private async Task<ITextDetector> GetOrLoadDetectorAsync(CancellationToken cancellationToken)
    {
        if (_detector is not null) return _detector;

        await _detectorLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_detector is not null) return _detector;
            var path = await ResolveDetectorModelPathAsync(cancellationToken).ConfigureAwait(false);
            _detector = CreateSessionBacked(so => (ITextDetector)new DbTextDetector(new InferenceSession(path, so)));
            StructureDiagnostics.ModelLoads.Add(1, new KeyValuePair<string, object?>("model", "det"));
            _logger?.LogInformation("DB detector loaded from {Path}", path);
            return _detector;
        }
        finally
        {
            _detectorLock.Release();
        }
    }

    private async Task<IAngleClassifier> GetOrLoadClassifierAsync(CancellationToken cancellationToken)
    {
        if (_classifier is not null) return _classifier;

        await _classifierLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_classifier is not null) return _classifier;
            var path = await ModelDownloadManager.EnsureModelAsync(
                StructureModelRegistry.Classifier, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
            _classifier = CreateSessionBacked(so => (IAngleClassifier)new TextLineClassifier(new InferenceSession(path, so)));
            StructureDiagnostics.ModelLoads.Add(1, new KeyValuePair<string, object?>("model", "cls"));
            _logger?.LogInformation("Text-line orientation classifier loaded from {Path}", path);
            return _classifier;
        }
        finally
        {
            _classifierLock.Release();
        }
    }

    /// <summary>
    /// True when the whole-document orientation classifier can be (or already is) loaded — used by
    /// <see cref="Services.StructureService"/> to decide between the model-based orientation pass and the
    /// brute-force 4-rotation fallback. A failed load marks the pre-processor unavailable and returns false.
    /// </summary>
    public async Task<bool> TryEnsureDocOrientationAsync(CancellationToken cancellationToken)
        => await GetOrLoadDocPreprocessorAsync(needOrientation: true, needUnwarp: false, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// Loads (once) the document pre-processor with the doc-orientation and/or UVDoc unwarp sessions,
    /// mirroring <see cref="Structure.StructureEngine.GetOrLoadPreprocessorAsync(bool, bool, CancellationToken)"/>:
    /// the cache is keyed on which sessions the instance holds, and a later call needing a session the
    /// cached instance lacks rebuilds it with the union of everything loaded so far. Sessions go through
    /// the engine's normal session-building path (<see cref="CreateSessionBacked{T}"/>), so provider
    /// fallback behaves like every other model. Returns <c>null</c> — permanently, with a one-time
    /// warning — when a model cannot be obtained (e.g. offline with an empty cache), so the default-on
    /// orientation flag can never make plain OCR calls fail.
    /// </summary>
    private async Task<IDocPreprocessor?> GetOrLoadDocPreprocessorAsync(
        bool needOrientation, bool needUnwarp, CancellationToken cancellationToken)
    {
        var cached = _docPreprocessor;
        if (cached is not null
            && (!needOrientation || _docPreprocessorHasOrientation)
            && (!needUnwarp || _docPreprocessorHasUnwarp))
        {
            return cached;
        }
        if (_docPreprocessorUnavailable) return null;

        await _docPreprocessorLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _docPreprocessor;
            if (cached is not null
                && (!needOrientation || _docPreprocessorHasOrientation)
                && (!needUnwarp || _docPreprocessorHasUnwarp))
            {
                return cached;
            }
            if (_docPreprocessorUnavailable) return null;

            bool wantOrientation = needOrientation || _docPreprocessorHasOrientation;
            bool wantUnwarp = needUnwarp || _docPreprocessorHasUnwarp;

            InferenceSession? orientation = null;
            InferenceSession? unwarp = null;
            try
            {
                if (wantOrientation)
                {
                    var path = await ModelDownloadManager.EnsureModelAsync(
                        StructureModelRegistry.DocOrientationClassifier, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
                    orientation = CreateSessionBacked(so => new InferenceSession(path, so));
                    StructureDiagnostics.ModelLoads.Add(1, new KeyValuePair<string, object?>("model", "doc_ori"));
                }
                if (wantUnwarp)
                {
                    var path = await ModelDownloadManager.EnsureModelAsync(
                        StructureModelRegistry.DocUnwarp, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
                    unwarp = CreateSessionBacked(so => new InferenceSession(path, so));
                    StructureDiagnostics.ModelLoads.Add(1, new KeyValuePair<string, object?>("model", "doc_unwarp"));
                }
            }
            catch (OperationCanceledException)
            {
                orientation?.Dispose();
                unwarp?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                orientation?.Dispose();
                unwarp?.Dispose();
                _docPreprocessorUnavailable = true;
                _logger?.LogWarning(ex,
                    "Document pre-processor model(s) unavailable; continuing without doc orientation/unwarp. " +
                    "OCR results for rotated pages may suffer until the model can be downloaded.");
                return null;
            }

            _docPreprocessor?.Dispose();
            _docPreprocessor = new DocPreprocessor(orientation, unwarp);
            _docPreprocessorHasOrientation = wantOrientation;
            _docPreprocessorHasUnwarp = wantUnwarp;
            _logger?.LogInformation("Document pre-processor loaded (orientation={Ori}, unwarp={Unwarp}).",
                wantOrientation, wantUnwarp);
            return _docPreprocessor;
        }
        finally
        {
            _docPreprocessorLock.Release();
        }
    }

    /// <summary>
    /// Resolves the file path of the detection ONNX model: an explicit local
    /// <see cref="StructureEngineOptions.DetectionModelPath"/> wins (no download, no checksum — the file is
    /// trusted as-is), otherwise the registry's mobile or server detector per
    /// <see cref="StructureEngineOptions.DetectionModel"/> is downloaded/cached as usual.
    /// </summary>
    private async Task<string> ResolveDetectorModelPathAsync(CancellationToken cancellationToken)
    {
        if (_options.DetectionModelPath is { Length: > 0 } localPath)
        {
            if (!File.Exists(localPath))
                throw new FileNotFoundException($"The detection model file '{localPath}' (StructureServiceOptions.DetectionModelPath) could not be found.", localPath);
            _logger?.LogInformation("Using local detection model {Path} (download and checksum verification skipped).", localPath);
            return localPath;
        }

        var asset = _options.DetectionModel == OcrModelVariant.Server
            ? StructureModelRegistry.ServerDetector
            : StructureModelRegistry.MobileDetector;
        return await ModelDownloadManager.EnsureModelAsync(
            asset, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True for the default Chinese/English/Japanese recognizer pack (either variant) — the pack the
    /// auto-detect fast path and the local-model override apply to.
    /// </summary>
    private static bool IsDefaultRecognizerPack(RecognizerPack pack)
        => pack.Name == StructureModelRegistry.MobileRecognizer.Name
           || pack.Name == StructureModelRegistry.ServerRecognizer.Name;

    /// <summary>
    /// Applies <see cref="StructureEngineOptions.RecognitionModel"/>: the default mobile pack (and the
    /// Traditional-Chinese codes, which reuse the same mobile network + dictionary) is substituted with
    /// the server pack; per-script packs have no published server variant and stay mobile, with a one-time
    /// informational log line. Called from the single load choke point so WarmUp, GetSharedRecognizerAsync,
    /// auto-detection and the normal pipeline all honor the substitution consistently.
    /// </summary>
    private RecognizerPack ApplyRecognitionVariant(RecognizerPack pack)
    {
        if (_options.RecognitionModel != OcrModelVariant.Server) return pack;

        if (pack.Name == StructureModelRegistry.MobileRecognizer.Name
            || pack.Name == StructureModelRegistry.ChineseTraditional.Name)
        {
            return StructureModelRegistry.ServerRecognizer;
        }

        if (pack.Name != StructureModelRegistry.ServerRecognizer.Name
            && Interlocked.Exchange(ref _serverVariantNoticeLogged, 1) == 0)
        {
            _logger?.LogInformation(
                "The server recognizer only exists for the default ch/en/ja pack; per-script pack '{Pack}' stays on its mobile network.",
                pack.Name);
        }
        return pack;
    }

    private async Task<ITextRecognizer> GetOrLoadRecognizerAsync(RecognizerPack pack, CancellationToken cancellationToken)
    {
        // Honor the server-variant substitution before touching the cache so the substituted pack is
        // both what gets loaded and what keys the cache entry.
        pack = ApplyRecognitionVariant(pack);

        // The load Task is shared across all callers, so it must NOT capture any single caller's
        // CancellationToken — otherwise the first caller cancelling would poison the cached Task and
        // every later caller (with a live token) would observe that cancellation. Load with None and
        // let each caller observe only its own token via WaitAsync.
        var lazy = _recognizers.GetOrAdd(pack.Name, _ => new Lazy<Task<ITextRecognizer>>(
            () => LoadRecognizerAsync(pack, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));

        var loadTask = lazy.Value;
        try
        {
            return await loadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // If the shared load itself failed (download/IO/corrupt model), evict the poisoned entry so a
            // later call retries instead of being served the cached faulted Task forever. A per-caller
            // cancellation leaves a still-running/successful task in place, so only the genuine fault path
            // evicts. TryRemove(pair) is a CAS: it removes only when the value is still this exact Lazy.
            if (loadTask.IsFaulted)
            {
                _recognizers.TryRemove(new KeyValuePair<string, Lazy<Task<ITextRecognizer>>>(pack.Name, lazy));
            }
            throw;
        }
    }

    private async Task<ITextRecognizer> LoadRecognizerAsync(RecognizerPack pack, CancellationToken cancellationToken)
    {
        string modelPath;
        string dictPath;

        // Local override (StructureServiceOptions.RecognitionModelPath) replaces the DEFAULT pack's
        // network — no download, no checksum, the file is trusted as-is. The paired dictionary defaults
        // to the pack's published one when RecognitionDictionaryPath is not set.
        if (IsDefaultRecognizerPack(pack) && _options.RecognitionModelPath is { Length: > 0 } localModel)
        {
            if (!File.Exists(localModel))
                throw new FileNotFoundException($"The recognition model file '{localModel}' (StructureServiceOptions.RecognitionModelPath) could not be found.", localModel);
            modelPath = localModel;
            _logger?.LogInformation("Using local recognition model {Path} (download and checksum verification skipped).", localModel);

            if (_options.RecognitionDictionaryPath is { Length: > 0 } localDict)
            {
                if (!File.Exists(localDict))
                    throw new FileNotFoundException($"The recognition dictionary file '{localDict}' (StructureServiceOptions.RecognitionDictionaryPath) could not be found.", localDict);
                dictPath = localDict;
            }
            else
            {
                dictPath = await ModelDownloadManager.EnsureModelAsync(
                    pack.Dictionary, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            modelPath = await ModelDownloadManager.EnsureModelAsync(
                pack.Model, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
            dictPath = await ModelDownloadManager.EnsureModelAsync(
                pack.Dictionary, _options.ModelCachePath, _options.Download, _logger, cancellationToken).ConfigureAwait(false);
        }

        // Pass the raw dictionary lines; SvtrRecognizer builds the vocab to match the model's class count.
        var dictLines = CharacterDictionary.LoadLines(dictPath);
        StructureDiagnostics.ModelLoads.Add(1, new KeyValuePair<string, object?>("model", pack.Name));
        _logger?.LogInformation("Recognizer '{Name}' loaded from {Path} ({Count} dict lines)", pack.Name, modelPath, dictLines.Count);
        return CreateSessionBacked(so => (ITextRecognizer)new SvtrRecognizer(new InferenceSession(modelPath, so), dictLines));
    }

    /// <summary>
    /// Creates an ONNX-session-backed object via <paramref name="factory"/> using the active session
    /// options. When an accelerated provider was resolved but its session fails to initialize, this
    /// permanently downgrades the engine to CPU and retries once, so the very first model load can't
    /// hard-fail on a bad accelerator.
    /// </summary>
    private T CreateSessionBacked<T>(Func<SessionOptions, T> factory)
    {
        if (_activeProvider == OcrExecutionProvider.Cpu)
        {
            return factory(_cpuFallbackSessionOptions ?? _sessionOptions);
        }

        try
        {
            return factory(_sessionOptions);
        }
        catch (Exception ex)
        {
            return factory(DowngradeToCpu(ex));
        }
    }

    private SessionOptions DowngradeToCpu(Exception cause)
    {
        lock (_fallbackLock)
        {
            if (_cpuFallbackSessionOptions is null)
            {
                _logger?.LogWarning(cause,
                    "{Provider} session initialization failed at model load; falling back to CPU for all sessions.",
                    _activeProvider);
                _cpuFallbackSessionOptions = ExecutionProviderResolver.BuildSessionOptions(OcrExecutionProvider.Cpu, _options, _logger);
                _activeProvider = OcrExecutionProvider.Cpu;
            }
            return _cpuFallbackSessionOptions;
        }
    }

    /// <summary>
    /// Builds the "why is OCR on CPU" hint. A provider-append failure (from
    /// <see cref="ExecutionProviderResolver.BuildSessionOptionsWithStatus"/>) always wins and is surfaced
    /// regardless of whether the accelerator was requested explicitly or via Auto — that failure was
    /// previously invisible without a logger. Otherwise, when auto-detection landed on CPU but the host
    /// actually has a GPU, the package-specific upgrade hint is built. The string is always returned (and
    /// exposed via <see cref="GpuHint"/>); it is only logged as a startup warning when
    /// <paramref name="logHint"/> is true (off by default).
    /// </summary>
    private static string? BuildGpuHint(OcrExecutionProvider requested, OcrExecutionProvider resolved, string? providerFailureHint, bool logHint, ILogger? logger)
    {
        if (providerFailureHint is not null)
        {
            if (logHint) logger?.LogWarning("{GpuHint}", providerFailureHint);
            return providerFailureHint;
        }

        if (requested != OcrExecutionProvider.Auto || resolved != OcrExecutionProvider.Cpu) return null;

        // Checked before the GPU probe, and independently of it: the displaced-runtime state is diagnosed
        // from the deployed files rather than the hardware, so it must still be reported where the probe
        // cannot run (it is Windows-only) or where it finds nothing.
        var displaced = ExecutionProviderResolver.CudaProviderDisplacedHint();
        if (displaced is not null)
        {
            if (logHint) logger?.LogWarning("{GpuHint}", displaced);
            return displaced;
        }

        var vendor = GpuProbe.Detect();
        if (vendor == GpuProbe.GpuVendor.None) return null;

        var message = vendor == GpuProbe.GpuVendor.Nvidia
            ? "EasyOcrSharp: an NVIDIA GPU was detected but OCR is running on CPU. Install the " +
              "'EasyOcrSharp.Gpu' NuGet package for CUDA acceleration. It is then used automatically — " +
              "no code change needed."
            : $"EasyOcrSharp: a GPU ({vendor}) was detected but OCR is running on CPU. GPU acceleration is " +
              "currently available for NVIDIA GPUs via the 'EasyOcrSharp.Gpu' package (CUDA 12+).";

        if (logHint) logger?.LogWarning("{GpuHint}", message);
        return message;
    }

    public async ValueTask DisposeAsync()
    {
        _detector?.Dispose();
        _detector = null;
        _classifier?.Dispose();
        _classifier = null;
        _docPreprocessor?.Dispose();
        _docPreprocessor = null;

        foreach (var entry in _recognizers.Values)
        {
            if (entry.IsValueCreated)
            {
                try { (await entry.Value.ConfigureAwait(false)).Dispose(); }
                catch { /* dispose best-effort */ }
            }
        }
        _recognizers.Clear();
        _sessionOptions.Dispose();
        _cpuFallbackSessionOptions?.Dispose();
    }
}
