using System.Diagnostics;
using EasyOcrSharp.Diagnostics;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using Microsoft.Extensions.Logging;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

using PaddleDetectionOptions = EasyOcrSharp.Structure.Engine.Models.DetectionOptions;
using PaddleGrouping = EasyOcrSharp.Structure.Engine.Models.TextGrouping;
using PaddleTextEngine = EasyOcrSharp.Structure.Engine.StructureTextEngine;

namespace EasyOcrSharp.Services;

/// <summary>
/// The opt-in PP-OCRv5 plain-OCR path (<see cref="RecognitionOptions.Engine"/> =
/// <see cref="RecognitionEngine.PpOcrV5"/>). Every entry point routes here from the same place the
/// default engine runs, so the governor slot, metrics recorder, span, dispose-drain scope, image guard
/// and cancellation are exactly the ones the default engine uses; only the models differ.
/// </summary>
/// <remarks>
/// <para>
/// The PP-OCRv5 sessions are the document analyzer's (see <c>EasyOcrService.DocumentAnalysis.cs</c>):
/// <see cref="GetPaddleTextEngine"/> reuses its text engine, so a service that both analyzes documents
/// and OCRs with PP-OCRv5 holds one DB detector, one text-line classifier and one session per recognizer
/// pack. Everything around the model run — pre-processing, document orientation/unwarp, the 4-way
/// orientation sweep, the region of interest, paragraph merging, reading order and result assembly — is
/// the default engine's own code, so both engines report boxes in the same frame and order lines the
/// same way. <see cref="PpOcrEngineAdapter"/> holds the pure option/language mapping.
/// </para>
/// <para>
/// Every method here must be called inside an operation scope (<see cref="BeginOperation"/>): that is
/// what keeps <see cref="DisposeAsync"/> from releasing the shared sessions under an in-flight run.
/// </para>
/// </remarks>
public sealed partial class EasyOcrService
{
    /// <summary>The value of the <c>easyocr.engine</c> span tag on PP-OCRv5 operations.</summary>
    private const string PaddleEngineTag = "ppocrv5";

    /// <summary>
    /// The shared PP-OCRv5 text engine, created (cheaply — models load on first use) together with the
    /// document analyzer that owns it.
    /// </summary>
    private PaddleTextEngine GetPaddleTextEngine() => GetOrCreateDocumentAnalyzer().TextEngine;

    /// <summary>
    /// Validates the engine selection and resolves the call's languages to PP-OCRv5 packs. Throws
    /// <see cref="ArgumentException"/> for an empty or unsupported language request.
    /// </summary>
    private static PpOcrLanguagePlan CreatePaddlePlan(IEnumerable<string> languages, RecognitionOptions options)
    {
        PpOcrEngineAdapter.EnsureKnownEngine(options.Engine);
        var requested = ResolveLanguages(languages, allowEmpty: options.AutoDetectLanguage);
        return PpOcrEngineAdapter.CreatePlan(requested, options);
    }

    /// <summary>
    /// The PP-OCRv5 counterpart of <c>RunPipelineCoreAsync</c>: document preprocessing → (optional 4-way
    /// orientation sweep) → pre-processing → region crop → PP-OCRv5 → result. Runs inside the caller's
    /// governor slot and operation scope.
    /// </summary>
    private async Task<OcrResult> RunPaddlePipelineCoreAsync(
        Image<Rgb24> image,
        IEnumerable<string> languages,
        RecognitionOptions options,
        OcrOperationRecorder recorder,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        // Validate before any model work: an unsupported language must not cost a page-orientation pass.
        var plan = CreatePaddlePlan(languages, options);

        // The same input frames as the default engine: a square-pixel copy for fax-style pages, then the
        // model-based document correction on the same host, so the boxes are reported in an identical frame
        // whichever engine reads the page.
        using var squared = CreateSquarePixelInput(image, options);
        var input = squared ?? image;

        Image<Rgb24>? docCorrected = null;
        try
        {
            int docRotation = 0;
            if (options.Preprocessing.DocumentOrientation || options.Preprocessing.DocumentUnwarp)
            {
                var (corrected, rotation) = await _docPreprocess.ApplyAsync(
                    input, options.Preprocessing.DocumentOrientation, options.Preprocessing.DocumentUnwarp, cancellationToken).ConfigureAwait(false);
                docCorrected = corrected;
                docRotation = rotation;
                if (rotation != 0)
                    _logger?.LogInformation("Document orientation corrected by {Degrees}° clockwise", rotation);
            }
            var working = docCorrected ?? input;
            var aspect = SquarePixelMappingFor(image, squared, docRotation);
            var workingOptions = WithRegionOnSquarePixels(options, aspect);

            (IReadOnlyList<OcrLine> Lines, string[] Languages, DeskewCanvas? Deskew, float DeskewAngle) outcome;
            if (options.Preprocessing.DetectOrientation)
            {
                var (lines, reported, deskewAngle) = await RecognizeBestOrientationWithPaddleAsync(working, plan, workingOptions, cancellationToken).ConfigureAwait(false);
                outcome = (lines, reported, null, deskewAngle);
            }
            else
            {
                var (lines, reported, deskew) = await PaddleCoreAsync(working, plan, workingOptions, cancellationToken).ConfigureAwait(false);
                outcome = (lines, reported, deskew, deskew?.RotationDegrees ?? 0f);
            }

            // As on the default engine: reading order is decided in the frame OCR ran in, then every
            // coordinate is mapped onto the caller's image.
            var result = BuildPaddleResult(outcome.Lines, outcome.Languages, sw, activity, working.Width, working.Height, options.ReadingDirection, recorder);
            return ToCallerFrame(result, outcome.Deskew, outcome.DeskewAngle, aspect);
        }
        finally
        {
            docCorrected?.Dispose();
        }
    }

    /// <summary>
    /// The PP-OCRv5 counterpart of <c>RecognizeBestOrientationAsync</c>: OCR at 0/90/180/270°, keep the
    /// orientation with the strongest result (Σ confidence × length) and map its boxes back into the
    /// original image's frame.
    /// </summary>
    private async Task<(IReadOnlyList<OcrLine> Lines, string[] Languages, float DeskewAngle)> RecognizeBestOrientationWithPaddleAsync(
        Image<Rgb24> image, PpOcrLanguagePlan plan, RecognitionOptions options, CancellationToken ct)
    {
        var noOrient = options with { Preprocessing = options.Preprocessing with { DetectOrientation = false } };

        (IReadOnlyList<OcrLine> Lines, string[] Languages, DeskewCanvas? Deskew)? best = null;
        double bestScore = double.NegativeInfinity;
        int bestDegrees = 0;

        foreach (var degrees in new[] { 0, 90, 180, 270 })
        {
            ct.ThrowIfCancellationRequested();
            Image<Rgb24>? rotated = degrees == 0 ? null : ImagePreprocessor.RotateRightAngle(image, degrees);
            try
            {
                var outcome = await PaddleCoreAsync(rotated ?? image, plan, noOrient, ct).ConfigureAwait(false);
                double score = outcome.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).Sum(l => l.Confidence * l.Text.Length);
                _logger?.LogInformation("Orientation {Deg}° scored {Score:F1} (PP-OCRv5)", degrees, score);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = outcome;
                    bestDegrees = degrees;
                }
            }
            finally
            {
                rotated?.Dispose();
            }
        }

        if (best is not { } chosen)
        {
            return (Array.Empty<OcrLine>(), PpOcrEngineAdapter.ReportedLanguages(plan, Array.Empty<string>()), 0f);
        }

        // Off the winning pass's deskewed canvas first, then out of its quarter turn.
        var upright = chosen.Deskew is { } canvas ? canvas.LinesToSource(chosen.Lines) : chosen.Lines;
        return (MapLinesToOriginalOrientation(upright, bestDegrees, image.Width, image.Height), chosen.Languages, chosen.Deskew?.RotationDegrees ?? 0f);
    }

    /// <summary>
    /// Pre-process → region crop → PP-OCRv5 detect + recognize → paragraph merge. The PP-OCRv5 counterpart
    /// of <c>CoreAsync</c> + the region-of-interest helper, reusing the default engine's pre-processor and
    /// coordinate translation.
    /// </summary>
    private async Task<(IReadOnlyList<OcrLine> Lines, string[] Languages, DeskewCanvas? Deskew)> PaddleCoreAsync(
        Image<Rgb24> image, PpOcrLanguagePlan plan, RecognitionOptions options, CancellationToken ct)
    {
        bool needsPreprocess = options.Preprocessing.Denoise || options.Preprocessing.Deskew
            || options.Preprocessing.Sharpen || options.Preprocessing.Binarize;
        float deskewRotation = 0f;
        Image<Rgb24> working = needsPreprocess ? ImagePreprocessor.Apply(image, options.Preprocessing, out deskewRotation) : image;
        try
        {
            // As in CoreAsync: lines come back on the deskewed canvas, so the caller's region is re-expressed
            // there and the canvas is returned for mapping the result onto the input image.
            DeskewCanvas? deskew = deskewRotation != 0f
                ? new DeskewCanvas(deskewRotation, working.Width, working.Height, image.Width, image.Height)
                : null;
            if (deskew is { } canvas && options.Region is { } deskewRegion)
            {
                options = options with { Region = canvas.RegionToCanvas(deskewRegion) };
            }

            var engine = GetPaddleTextEngine();
            var engineOptions = PpOcrEngineAdapter.ToEngineOptions(options, plan, perRegion: false);

            IReadOnlyList<OcrLine> lines;
            IReadOnlyList<string> detected;
            if (options.Region is not { } region)
            {
                (lines, detected, _) = await engine.RecognizeWithDetectedLanguagesAsync(
                    working, plan.EngineLanguages, engineOptions, ct).ConfigureAwait(false);
            }
            else
            {
                var (rx, ry, rw, rh) = region.Resolve(working.Width, working.Height);
                if (rw < 2 || rh < 2)
                {
                    return (Array.Empty<OcrLine>(), PpOcrEngineAdapter.ReportedLanguages(plan, Array.Empty<string>()), deskew);
                }

                using var roi = working.Clone(ctx => ctx.Crop(new Rectangle(rx, ry, rw, rh)));
                (lines, detected, _) = await engine.RecognizeWithDetectedLanguagesAsync(
                    roi, plan.EngineLanguages, engineOptions, ct).ConfigureAwait(false);
                lines = TranslateLines(lines, rx, ry);
            }

            var reported = PpOcrEngineAdapter.ReportedLanguages(
                plan, detected, plan.AutoDetect ? string.Concat(lines.Select(l => l.Text)) : null);
            if (plan.AutoDetect)
                _logger?.LogInformation("Auto-detected languages (PP-OCRv5): {Langs}", string.Join(", ", reported));
            return (MergeParagraphsIfRequested(lines, options, reported), reported, deskew);
        }
        finally
        {
            if (needsPreprocess) working.Dispose();
        }
    }

    /// <summary>
    /// The PP-OCRv5 body of <c>RecognizeRegionsAsync</c>: one reading per caller-supplied region (detection,
    /// pre-processing and <see cref="RecognitionOptions.Region"/> are skipped, as on the default engine).
    /// </summary>
    private async Task<OcrResult> RecognizeRegionsWithPaddleAsync(
        Image<Rgb24> image,
        IReadOnlyList<OcrPoint[]> polygons,
        string[] languages,
        RecognitionOptions options,
        Stopwatch sw,
        Activity? activity,
        OcrOperationRecorder recorder,
        CancellationToken cancellationToken)
    {
        PpOcrEngineAdapter.EnsureKnownEngine(options.Engine);
        var plan = PpOcrEngineAdapter.CreatePlan(languages, options);

        IReadOnlyList<OcrLine> lines = Array.Empty<OcrLine>();
        if (polygons.Count > 0)
        {
            lines = await GetPaddleTextEngine().RecognizeRegionsAsync(
                image, plan.EngineLanguages, polygons, PpOcrEngineAdapter.ToEngineOptions(options, plan, perRegion: true), cancellationToken).ConfigureAwait(false);
        }

        var reported = PpOcrEngineAdapter.ReportedLanguages(plan, Array.Empty<string>());
        lines = MergeParagraphsIfRequested(lines, options, reported);
        return BuildPaddleResult(lines, reported, sw, activity, image.Width, image.Height, options.ReadingDirection, recorder);
    }

    /// <summary>
    /// The PP-OCRv5 body of <c>DetectRegionsAsync</c>: the DB detector's text-line regions in reading
    /// order, honoring <see cref="RecognitionOptions.Region"/>. CRAFT's <see cref="RecognitionOptions.Detection"/>
    /// thresholds and the grouping options do not apply — DB already emits line-level regions.
    /// </summary>
    private async Task<IReadOnlyList<DetectedRegion>> DetectRegionsWithPaddleAsync(
        Image<Rgb24> image, RecognitionOptions options, CancellationToken cancellationToken)
    {
        PpOcrEngineAdapter.EnsureKnownEngine(options.Engine);
        var engine = GetPaddleTextEngine();

        if (options.Region is not { } region)
        {
            return PpOcrEngineAdapter.ToPublicRegions(await engine.DetectRegionsAsync(
                image, PaddleDetectionOptions.Default, PaddleGrouping.Line, cancellationToken).ConfigureAwait(false));
        }

        var (rx, ry, rw, rh) = region.Resolve(image.Width, image.Height);
        if (rw < 2 || rh < 2) return Array.Empty<DetectedRegion>();

        using var roi = image.Clone(ctx => ctx.Crop(new Rectangle(rx, ry, rw, rh)));
        var regions = PpOcrEngineAdapter.ToPublicRegions(await engine.DetectRegionsAsync(
            roi, PaddleDetectionOptions.Default, PaddleGrouping.Line, cancellationToken).ConfigureAwait(false));
        return TranslateRegions(regions, rx, ry);
    }

    /// <summary>
    /// Validates a streamed page's PP-OCRv5 language request once, up front (so an unsupported language
    /// fails before detection rather than on the first chunk), and returns the codes each chunk is
    /// recognized with. Language detection, when requested, happens per line inside each chunk.
    /// </summary>
    private static string[] ResolvePaddleStreamLanguages(IEnumerable<string> languages, RecognitionOptions options)
    {
        var plan = CreatePaddlePlan(languages, options);
        return plan.Requested.Length > 0 ? plan.Requested : plan.EngineLanguages;
    }

    /// <summary>
    /// Recognizes one streamed chunk of regions with PP-OCRv5. The caller (<c>RecognizeChunkAsync</c>)
    /// holds the operation scope.
    /// </summary>
    private async Task<IReadOnlyList<OcrLine>> RecognizePaddleChunkAsync(
        Image<Rgb24> image,
        OcrPoint[][] polygons,
        string[] languages,
        RecognitionOptions options,
        CancellationToken cancellationToken)
    {
        var plan = PpOcrEngineAdapter.CreatePlan(languages, options);
        return await GetPaddleTextEngine().RecognizeRegionsAsync(
            image, plan.EngineLanguages, polygons, PpOcrEngineAdapter.ToEngineOptions(options, plan, perRegion: true), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies <see cref="TextGrouping.Paragraph"/> with the default engine's paragraph grouper (so
    /// <see cref="RecognitionOptions.GroupingOptions"/> thresholds and the reading direction apply exactly
    /// as they do there). Other groupings pass through.
    /// </summary>
    private static IReadOnlyList<OcrLine> MergeParagraphsIfRequested(
        IReadOnlyList<OcrLine> lines, RecognitionOptions options, IReadOnlyList<string> languages)
    {
        if (options.Grouping != TextGrouping.Paragraph || lines.Count == 0) return lines;

        var paragraphs = ParagraphGrouper.Merge(lines, options.GroupingOptions, ScriptDirection.IsRightToLeft(options.ReadingDirection, languages));
        // Merging concatenates whole lines; put their word boxes back as the default engine does.
        return options.WordLevelDetail == WordLevelDetail.None
            ? paragraphs
            : OnnxEasyOcrEngine.ReattachWords(paragraphs, lines as List<OcrLine> ?? lines.ToList());
    }

    /// <summary>
    /// Assembles the result with the default engine's <see cref="BuildResult"/> — identical reading order,
    /// full text, metrics and span tags — then reports the execution provider the PP-OCRv5 sessions are
    /// actually running on, which can differ from the default engine's when only one of them degraded to
    /// CPU.
    /// </summary>
    private OcrResult BuildPaddleResult(
        IReadOnlyList<OcrLine> lines,
        string[] languages,
        Stopwatch sw,
        Activity? activity,
        int sourceWidth,
        int sourceHeight,
        TextReadingDirection direction,
        OcrOperationRecorder recorder)
    {
        var result = BuildResult(lines, languages, sw, activity, sourceWidth, sourceHeight, direction, recorder);

        var provider = PpOcrEngineAdapter.ToPublicProvider(GetPaddleTextEngine().ActiveProvider);
        bool usedGpu = provider != OcrExecutionProvider.Cpu;
        if (activity is not null)
        {
            activity.SetTag("easyocr.gpu", usedGpu);
            activity.SetTag("easyocr.engine", PaddleEngineTag);
        }

        return result with { UsedGpu = usedGpu, ExecutionProvider = provider };
    }
}
