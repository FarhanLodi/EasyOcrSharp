using System.Diagnostics;
using System.Runtime.CompilerServices;
using EasyOcrSharp.Diagnostics;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using Microsoft.Extensions.Logging;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Services;

/// <summary>
/// Handwriting recognition via a locally supplied <b>TrOCR</b> encoder-decoder — the one thing Python
/// EasyOCR cannot do at all, because its CRNN recognizers are trained on printed glyphs. Text regions
/// are still located by the same CRAFT detector (handwriting is text; finding it is not the hard part),
/// and each region is then read by the transformer instead of the CRNN.
/// <para>
/// The feature is off unless <see cref="EasyOcrServiceOptions.Handwriting"/> was set: no model is
/// downloaded, no session is created, and every existing call path is untouched. The recognizer is
/// built on the first <c>RecognizeHandwritingAsync</c> call and reused afterwards.
/// </para>
/// </summary>
public sealed partial class EasyOcrService
{
    private volatile TrOcrRecognizer? _handwriting;
    private readonly object _handwritingLock = new();

    // Handwriting calls in flight. UnloadHandwritingModels swaps the field out and then waits on this before
    // disposing, so it can never free an encoder/decoder session a concurrent Recognize is running on --
    // which would be an AccessViolationException inside onnxruntime, not a catchable .NET exception.
    private int _handwritingInFlight;

    /// <summary>
    /// Whether this service was configured for handwriting — i.e. whether
    /// <see cref="EasyOcrServiceOptions.Handwriting"/> was set. When false, every
    /// <c>RecognizeHandwritingAsync</c> overload throws <see cref="InvalidOperationException"/> and
    /// nothing about the service's behaviour differs from a build without the feature.
    /// </summary>
    public bool SupportsHandwriting => HandwritingRegistry.Resolve(_engineOptions) is not null;

    /// <summary>
    /// Detects text regions with CRAFT and reads each one with TrOCR — the handwriting counterpart of
    /// <see cref="ExtractTextFromImage(Image{Rgb24}, IEnumerable{string}, RecognitionOptions?, CancellationToken)"/>.
    /// Honors <see cref="RecognitionOptions.Region"/>, <see cref="RecognitionOptions.Grouping"/>,
    /// <see cref="RecognitionOptions.Detection"/>, <see cref="RecognitionOptions.GroupingOptions"/> and
    /// <see cref="RecognitionOptions.MinConfidence"/>; the CRNN-only settings (decoder, allow/block list,
    /// contrast retry, rotation, batch size) do not apply to a transformer decoder and are ignored.
    /// </summary>
    /// <param name="image">The page or photo to read. Never modified.</param>
    /// <param name="options">Detection, region and grouping options. Null uses <see cref="RecognitionOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels detection and generation.</param>
    /// <returns>Lines in reading order, with boxes in <paramref name="image"/>'s coordinates.</returns>
    /// <exception cref="InvalidOperationException"><see cref="EasyOcrServiceOptions.Handwriting"/> was not configured.</exception>
    public Task<OcrResult> RecognizeHandwritingAsync(
        Image<Rgb24> image,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => RunGatedAsync(
            EasyOcrDiagnostics.OperationNames.Handwriting,
            "EasyOcr.Handwriting",
            (recorder, activity, token) => DetectAndRecognizeHandwritingAsync(image, options, recorder, activity, token),
            cancellationToken);

    private async Task<OcrResult> DetectAndRecognizeHandwritingAsync(
        Image<Rgb24> image,
        RecognitionOptions? options,
        OcrOperationRecorder recorder,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        using var op = BeginOperation();
        ArgumentNullException.ThrowIfNull(image);
        options ??= RecognitionOptions.Default;
        var handwriting = RequireHandwritingOptions();

        var sw = Stopwatch.StartNew();
        var languages = new[] { handwriting.Language };

        Image<Rgb24>? roi = null;
        try
        {
            var working = image;
            int dx = 0, dy = 0;
            if (options.Region is { } roiRegion)
            {
                var (rx, ry, rw, rh) = roiRegion.Resolve(image.Width, image.Height);
                if (rw < 2 || rh < 2)
                {
                    return BuildResult(Array.Empty<OcrLine>(), languages, sw, activity, image.Width, image.Height, options.ReadingDirection, recorder);
                }
                roi = image.Clone(ctx => ctx.Crop(new Rectangle(rx, ry, rw, rh)));
                working = roi;
                dx = rx;
                dy = ry;
            }

            var regions = await _engine.DetectRegionsAsync(
                working, options.Detection, options.Grouping, options.GroupingOptions, cancellationToken).ConfigureAwait(false);

            var polygons = new List<OcrPoint[]>(regions.Count);
            foreach (var detected in regions) polygons.Add(detected.BoundingPolygon.ToArray());

            var lines = await RecognizeHandwrittenPolygonsAsync(working, polygons, options, cancellationToken).ConfigureAwait(false);
            return BuildResult(TranslateLines(lines, dx, dy), languages, sw, activity, image.Width, image.Height, options.ReadingDirection, recorder);
        }
        finally
        {
            roi?.Dispose();
        }
    }

    /// <summary>
    /// Detects and reads handwriting in an image file — the same pipeline as
    /// <see cref="RecognizeHandwritingAsync(Image{Rgb24}, RecognitionOptions?, CancellationToken)"/>,
    /// with the decompression-bomb guard applied while loading.
    /// </summary>
    /// <param name="imagePath">Path to the image to read.</param>
    /// <param name="options">Detection, region and grouping options. Null uses <see cref="RecognitionOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels loading, detection and generation.</param>
    /// <exception cref="InvalidOperationException"><see cref="EasyOcrServiceOptions.Handwriting"/> was not configured.</exception>
    /// <exception cref="FileNotFoundException">The image file does not exist.</exception>
    public async Task<OcrResult> RecognizeHandwritingAsync(
        string imagePath,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("Image path must be provided.", nameof(imagePath));

        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"The image file '{fullPath}' could not be found.", fullPath);

        using var image = await LoadGuarded(fullPath, cancellationToken).ConfigureAwait(false);
        return await RecognizeHandwritingAsync(image, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads handwriting inside caller-supplied regions, <b>skipping detection</b> — the handwriting
    /// counterpart of
    /// <see cref="RecognizeRegionsAsync(Image{Rgb24}, IEnumerable{IReadOnlyList{OcrPoint}}, IEnumerable{string}, RecognitionOptions?, CancellationToken)"/>.
    /// Each region is a polygon (3+ points) in the image's pixel coordinates, e.g. from
    /// <see cref="DetectRegionsAsync(Image{Rgb24}, RecognitionOptions?, CancellationToken)"/>, a form
    /// template, or your own layout analysis — which is the usual way to read a handful of handwritten
    /// fields on an otherwise printed page.
    /// </summary>
    /// <param name="image">The image the regions refer to. Never modified.</param>
    /// <param name="regions">Region polygons, in <paramref name="image"/>'s coordinates.</param>
    /// <param name="options">Grouping and confidence options. Detection settings are unused here.</param>
    /// <param name="cancellationToken">Cancels generation between regions.</param>
    /// <exception cref="InvalidOperationException"><see cref="EasyOcrServiceOptions.Handwriting"/> was not configured.</exception>
    public Task<OcrResult> RecognizeHandwritingAsync(
        Image<Rgb24> image,
        IEnumerable<IReadOnlyList<OcrPoint>> regions,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
        => RunGatedAsync(
            EasyOcrDiagnostics.OperationNames.Handwriting,
            "EasyOcr.Handwriting",
            async (recorder, activity, token) =>
            {
                using var op = BeginOperation();
                ArgumentNullException.ThrowIfNull(image);
                ArgumentNullException.ThrowIfNull(regions);
                options ??= RecognitionOptions.Default;
                var handwriting = RequireHandwritingOptions();

                var sw = Stopwatch.StartNew();
                var languages = new[] { handwriting.Language };

                var polygons = regions
                    .Where(r => r is { Count: >= 3 })
                    .Select(r => r.ToArray())
                    .ToArray();

                var lines = await RecognizeHandwrittenPolygonsAsync(image, polygons, options, token).ConfigureAwait(false);
                return BuildResult(lines, languages, sw, activity, image.Width, image.Height, options.ReadingDirection, recorder);
            },
            cancellationToken);

    /// <summary>Reads handwriting inside regions located by a prior <see cref="DetectRegionsAsync(Image{Rgb24}, RecognitionOptions?, CancellationToken)"/> pass.</summary>
    /// <param name="image">The image the regions refer to. Never modified.</param>
    /// <param name="regions">Regions from a previous detection pass.</param>
    /// <param name="options">Grouping and confidence options. Detection settings are unused here.</param>
    /// <param name="cancellationToken">Cancels generation between regions.</param>
    /// <exception cref="InvalidOperationException"><see cref="EasyOcrServiceOptions.Handwriting"/> was not configured.</exception>
    public Task<OcrResult> RecognizeHandwritingAsync(
        Image<Rgb24> image,
        IEnumerable<DetectedRegion> regions,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regions);
        return RecognizeHandwritingAsync(image, regions.Select(r => r.BoundingPolygon), options, cancellationToken);
    }

    /// <summary>
    /// Releases the TrOCR sessions if any were loaded, freeing the several hundred megabytes a
    /// transformer pair occupies. The next <c>RecognizeHandwritingAsync</c> call transparently reloads
    /// them. Safe to call concurrently with handwriting calls: it waits for those in flight to finish.
    /// </summary>
    /// <remarks>
    /// Handwriting models are loaded on demand. <see cref="DisposeAsync"/> now releases them too, so this is
    /// only needed to reclaim the memory of a service you intend to keep using.
    /// </remarks>
    public void UnloadHandwritingModels()
    {
        TrOcrRecognizer? recognizer;
        lock (_handwritingLock)
        {
            recognizer = _handwriting;
            _handwriting = null;
        }
        if (recognizer is null) return;

        // The field swap above stops NEW calls resolving this instance; calls already inside Recognize are
        // still running native inference on it. Wait for them before disposing.
        var spin = new SpinWait();
        while (Volatile.Read(ref _handwritingInFlight) > 0)
        {
            spin.SpinOnce();
        }

        recognizer.Dispose();
    }

    /// <summary>
    /// Runs TrOCR over each polygon, drops blank and below-threshold readings, and applies paragraph
    /// merging when it was asked for — the same post-processing the CRNN pipeline applies, so the two
    /// produce comparably shaped results.
    /// </summary>
    private async Task<IReadOnlyList<OcrLine>> RecognizeHandwrittenPolygonsAsync(
        Image<Rgb24> image, IReadOnlyList<OcrPoint[]> polygons, RecognitionOptions options, CancellationToken cancellationToken)
    {
        if (polygons.Count == 0) return Array.Empty<OcrLine>();

        var recognizer = await GetOrCreateHandwritingRecognizerAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<OcrLine> recognized;
        Interlocked.Increment(ref _handwritingInFlight);
        try
        {
            recognized = recognizer.Recognize(image, polygons, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _handwritingInFlight);
        }

        var kept = new List<OcrLine>(recognized.Count);
        foreach (var line in recognized)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            if (line.Confidence < options.MinConfidence) continue;
            kept.Add(line);
        }

        _logger?.LogInformation("Handwriting: read {Kept} of {Total} regions with TrOCR.", kept.Count, polygons.Count);

        return options.Grouping == TextGrouping.Paragraph
            ? ParagraphGrouper.Merge(
                kept,
                options.GroupingOptions,
                ScriptDirection.IsRightToLeft(options.ReadingDirection, [RequireHandwritingOptions().Language]))
            : kept;
    }

    /// <summary>
    /// Creates the TrOCR recognizer on first use. Any model path the caller left null is resolved
    /// through the shared model cache first — downloading it if necessary, with the same checksum
    /// verification, retry and offline behaviour as the printed-text packs — so a missing export or a
    /// failed download surfaces on the first handwriting call rather than at service construction.
    /// </summary>
    private async Task<TrOcrRecognizer> GetOrCreateHandwritingRecognizerAsync(CancellationToken cancellationToken)
    {
        if (_handwriting is { } existing) return existing;

        var options = RequireHandwritingOptions();
        var resolved = await ResolveHandwritingModelsAsync(options, cancellationToken).ConfigureAwait(false);

        lock (_handwritingLock)
        {
            // Another caller may have won the race while this one was downloading; the loser's resolved
            // paths are simply discarded (the files are cached either way).
            return _handwriting ??= new TrOcrRecognizer(resolved, _engineOptions, _logger);
        }
    }

    /// <summary>
    /// Fills in whichever of the three model paths were left null, fetching the hosted TrOCR assets into
    /// the model cache. A fully local configuration is returned untouched and never touches the network.
    /// </summary>
    private async Task<HandwritingOptions> ResolveHandwritingModelsAsync(
        HandwritingOptions options,
        CancellationToken cancellationToken)
    {
        if (options.IsFullyLocal) return options;

        async Task<string> EnsureAsync(string? configured, ModelAsset asset)
            => !string.IsNullOrWhiteSpace(configured)
                ? configured
                : await ModelDownloadManager.EnsureModelAsync(
                    asset, _engineOptions.ModelCachePath, _engineOptions.Download, _logger, cancellationToken)
                    .ConfigureAwait(false);

        return options with
        {
            EncoderModelPath = await EnsureAsync(options.EncoderModelPath, ModelRegistry.TrOcrEncoder(options.Quantize)).ConfigureAwait(false),
            DecoderModelPath = await EnsureAsync(options.DecoderModelPath, ModelRegistry.TrOcrDecoder(options.Quantize)).ConfigureAwait(false),
            TokenizerPath = await EnsureAsync(options.TokenizerPath, ModelRegistry.TrOcrVocab).ConfigureAwait(false),
        };
    }

    private HandwritingOptions RequireHandwritingOptions()
        => HandwritingRegistry.Resolve(_engineOptions)
            ?? throw new InvalidOperationException(
                "Handwriting recognition is not configured. Set EasyOcrServiceOptions.Handwriting to a " +
                "HandwritingOptions instance pointing at an exported TrOCR encoder, decoder and tokenizer " +
                "(see tools/export_trocr_onnx.py) before calling RecognizeHandwritingAsync.");
}

/// <summary>
/// Side table carrying <see cref="EasyOcrServiceOptions.Handwriting"/> from the public options object to
/// the service, keyed by the <see cref="EngineOptions"/> instance the service holds.
/// </summary>
/// <remarks>
/// <para>
/// The service captures its configuration as an <see cref="EngineOptions"/> record in a constructor this
/// feature is not allowed to touch, so the handwriting settings ride alongside that instance instead of
/// inside it. Lookup is by reference identity (a <see cref="ConditionalWeakTable{TKey,TValue}"/> ignores
/// the record's structural equality), the entry lives exactly as long as the options object it belongs
/// to, and a service that never enables handwriting never gets an entry — so
/// <see cref="EasyOcrService.SupportsHandwriting"/> is false and nothing changes for it.
/// </para>
/// <para>
/// TODO: fold this into a plain <c>EngineOptions.Handwriting</c> property when the engine options record
/// and the service constructor can be edited together.
/// </para>
/// </remarks>
internal static class HandwritingRegistry
{
    private static readonly ConditionalWeakTable<EngineOptions, HandwritingOptions> Table = new();

    /// <summary>Associates handwriting settings with an engine-options instance. Null clears nothing and stores nothing.</summary>
    public static void Attach(EngineOptions engineOptions, HandwritingOptions? handwriting)
    {
        if (handwriting is not null) Table.AddOrUpdate(engineOptions, handwriting);
    }

    /// <summary>The handwriting settings attached to an engine-options instance, or null when the feature is off.</summary>
    public static HandwritingOptions? Resolve(EngineOptions engineOptions)
        => Table.TryGetValue(engineOptions, out var handwriting) ? handwriting : null;
}
