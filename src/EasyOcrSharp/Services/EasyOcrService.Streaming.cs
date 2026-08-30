using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EasyOcrSharp.Diagnostics;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using Microsoft.Extensions.Logging;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Services;

/// <summary>
/// Streaming recognition — the <c>ExtractTextStreamAsync</c> family. Detection runs once (it is a single
/// whole-image model pass and cannot be split), then the detected regions are recognized with bounded
/// concurrency and each <see cref="OcrLine"/> is handed to the consumer the moment it is ready, in
/// reading order. Nothing here changes the buffered pipeline: the same detector, the same recognizer,
/// the same options — only the delivery schedule differs.
/// </summary>
public sealed partial class EasyOcrService
{
    /// <inheritdoc cref="IEasyOcrService.ExtractTextStreamAsync(Image{Rgb24}, IEnumerable{string}, RecognitionOptions?, CancellationToken)" />
    public IAsyncEnumerable<OcrLine> ExtractTextStreamAsync(
        Image<Rgb24> image,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(languages);
        // Not an iterator itself, so argument validation happens on the call rather than on the first
        // MoveNextAsync — matching the eager checks of the buffered overloads.
        return StreamCoreAsync(image, languages, options ?? RecognitionOptions.Default, cancellationToken);
    }

    /// <inheritdoc cref="IEasyOcrService.ExtractTextStreamAsync(string, IEnumerable{string}, RecognitionOptions?, CancellationToken)" />
    public async IAsyncEnumerable<OcrLine> ExtractTextStreamAsync(
        string imagePath,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(languages);
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("Image path must be provided.", nameof(imagePath));

        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"The image file '{fullPath}' could not be found.", fullPath);

        // The decoded image is owned by this enumeration and released when it is disposed — which
        // `await foreach` always does, including on `break` and on an exception.
        using var image = await LoadGuarded(fullPath, cancellationToken).ConfigureAwait(false);
        await foreach (var line in StreamCoreAsync(image, languages, options ?? RecognitionOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            yield return line;
        }
    }

    /// <inheritdoc cref="IEasyOcrService.ExtractTextStreamAsync(Stream, IEnumerable{string}, RecognitionOptions?, CancellationToken)" />
    public async IAsyncEnumerable<OcrLine> ExtractTextStreamAsync(
        Stream imageStream,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(imageStream);
        ArgumentNullException.ThrowIfNull(languages);

        using var image = await LoadGuarded(imageStream, cancellationToken).ConfigureAwait(false);
        await foreach (var line in StreamCoreAsync(image, languages, options ?? RecognitionOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            yield return line;
        }
    }

    /// <inheritdoc cref="IEasyOcrService.ExtractTextStreamAsync(byte[], IEnumerable{string}, RecognitionOptions?, CancellationToken)" />
    public async IAsyncEnumerable<OcrLine> ExtractTextStreamAsync(
        byte[] imageBytes,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(imageBytes);
        ArgumentNullException.ThrowIfNull(languages);
        if (imageBytes.Length == 0)
            throw new ArgumentException("Image bytes must not be empty.", nameof(imageBytes));

        cancellationToken.ThrowIfCancellationRequested();
        using var image = LoadGuarded(imageBytes.AsSpan());
        await foreach (var line in StreamCoreAsync(image, languages, options ?? RecognitionOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            yield return line;
        }
    }

    // ---- pipeline ----

    /// <summary>
    /// The streaming pipeline: document preprocessing → language resolution → one detection pass →
    /// concurrent, order-preserving recognition of the detected regions. Deliberately mirrors
    /// <c>RunPipelineAsync</c>/<c>CoreAsync</c> stage for stage so a streamed page and a buffered page
    /// see exactly the same image and the same regions.
    /// </summary>
    /// <remarks>
    /// No operation scope is held across the whole enumeration: a consumer that pauses mid-stream would
    /// otherwise stall <see cref="DisposeAsync"/> indefinitely. Instead each detection / recognition call
    /// takes its own scope, so a session can never be released under an in-flight model run, and a
    /// service disposed mid-stream fails the next chunk with a clean <see cref="ObjectDisposedException"/>.
    /// </remarks>
    private async IAsyncEnumerable<OcrLine> StreamCoreAsync(
        Image<Rgb24> image,
        IEnumerable<string> languages,
        RecognitionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The four-way orientation sweep scores whole-page results against each other, so not a single
        // line can be trusted until the last orientation has been recognized. Run the buffered pipeline
        // and replay it — correct output, just not incremental (documented on the public overloads).
        //
        // No governor slot is taken for this branch: it delegates to the buffered pipeline, which takes
        // one of its own. Acquiring here as well would take two slots for one call and deadlock a service
        // configured with a single slot against itself.
        if (options.Preprocessing.DetectOrientation)
        {
            var buffered = await ExtractTextFromImage(image, languages, options, cancellationToken).ConfigureAwait(false);
            foreach (var line in buffered.Lines)
            {
                yield return line;
            }
            yield break;
        }

        EnsureNotDisposed();
        // Same acquire → record → translate sequence as RunGatedAsync, spelled out because an iterator
        // cannot yield from inside a catch block. Declaration order is disposal order reversed: the
        // recorder stamps its outcome on the span, so the span must outlive it.
        using var activity = EasyOcrDiagnostics.ActivitySource.StartActivity("EasyOcr.ExtractStream", ActivityKind.Internal);
        using var recorder = EasyOcrDiagnostics.Begin(EasyOcrDiagnostics.OperationNames.ExtractStream, ProviderName).Annotate(activity);

        OperationLease? lease = null;
        bool completed = false;
        bool faulted = false;
        try
        {
            try
            {
                lease = await _governor.AcquireAsync(EasyOcrDiagnostics.OperationNames.ExtractStream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                faulted = true;
                var shed = RecordFailure(recorder, lease, ex);
                if (ReferenceEquals(shed, ex)) throw;
                throw shed;
            }

            // The pipeline runs on the LEASE token, not the caller's, so OperationTimeout can fire.
            await using var lines = StreamPipelineAsync(image, languages, options, recorder, activity, lease.Token)
                .GetAsyncEnumerator(lease.Token);
            while (true)
            {
                OcrLine current;
                try
                {
                    if (!await lines.MoveNextAsync().ConfigureAwait(false)) break;
                    current = lines.Current;
                }
                catch (Exception ex)
                {
                    faulted = true;
                    var surfaced = RecordFailure(recorder, lease, ex);
                    if (ReferenceEquals(surfaced, ex)) throw;
                    throw surfaced;
                }

                yield return current;
            }
            completed = true;
        }
        finally
        {
            // A consumer that walks away mid-stream (a `break`, or an exception of its own) made a choice
            // rather than hitting a fault, so it is recorded as a cancellation — otherwise the recorder's
            // deliberate "error" default would brand every partially consumed page as a failure.
            if (completed) recorder.Success();
            else if (!faulted) recorder.Canceled();
            lease?.Dispose();
        }
    }

    /// <summary>
    /// The streaming pipeline proper, run under the governor lease held by <see cref="StreamCoreAsync"/>.
    /// </summary>
    private async IAsyncEnumerable<OcrLine> StreamPipelineAsync(
        Image<Rgb24> image,
        IEnumerable<string> languages,
        RecognitionOptions options,
        OcrOperationRecorder recorder,
        Activity? activity,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var langs = Array.Empty<string>();
        int emitted = 0;

        Image<Rgb24>? docCorrected = null;
        Image<Rgb24>? preprocessed = null;
        try
        {
            // Model-based document preprocessing first (orientation upright, then unwarp), exactly as the
            // buffered pipeline does, so boxes are reported in the corrected image's coordinate space.
            if (options.Preprocessing.DocumentOrientation || options.Preprocessing.DocumentUnwarp)
            {
                // Inside the drain gate, like every other model call: without it _activeOperations stays 0
                // while the orientation/UVDoc sessions are running, so a concurrent DisposeAsync sees an idle
                // service and frees those sessions mid-Run.
                using var op = BeginOperation();
                var (corrected, rotation) = await _docPreprocess.ApplyAsync(
                    image, options.Preprocessing.DocumentOrientation, options.Preprocessing.DocumentUnwarp, cancellationToken).ConfigureAwait(false);
                docCorrected = corrected;
                if (rotation != 0)
                    _logger?.LogInformation("Document orientation corrected by {Degrees}° clockwise", rotation);
            }

            var working = docCorrected ?? image;
            if (options.Preprocessing.Denoise || options.Preprocessing.Deskew
                || options.Preprocessing.Sharpen || options.Preprocessing.Binarize)
            {
                preprocessed = ImagePreprocessor.Apply(working, options.Preprocessing);
                working = preprocessed;
            }

            langs = await ResolveStreamLanguagesAsync(working, languages, options, cancellationToken).ConfigureAwait(false);

            // One detection pass over the whole page. This also applies the region-of-interest crop and
            // the Line/Paragraph box merging, and reports polygons in `working` coordinates. The ungated
            // core, not the public method: this stream already holds a governor slot, and asking for a
            // second one would deadlock a single-slot service against itself.
            var regions = await DetectRegionsCoreAsync(working, options, cancellationToken).ConfigureAwait(false);
            if (regions.Count > 0)
            {
                // Recognize BatchSize regions per call so a caller that opted into batched inference still
                // gets it; 1 (the default) is the lowest-latency, most incremental setting.
                bool rightToLeft = ScriptDirection.IsRightToLeft(options.ReadingDirection, langs);
                var chunks = OrderRegionsForReading(regions, rightToLeft).Chunk(Math.Max(1, options.BatchSize)).ToArray();
                var perChunk = options with
                {
                    // Paragraph merging spans the whole page — never merge inside a chunk. (For Word/Line
                    // the recognition stage ignores Grouping: box merging already happened in detection.)
                    Grouping = TextGrouping.Word,
                    // The stream supplies the parallelism; the recognizer must not fan out underneath it.
                    MaxDegreeOfParallelism = 1,
                    // Already applied above / already folded into the polygons.
                    Preprocessing = PreprocessingOptions.None,
                    Region = null,
                };

                var source = working;
                var stream = OrderedStreamPump.RunOrderedAsync<DetectedRegion[], OcrLine>(
                    chunks,
                    (chunk, ct) => RecognizeChunkAsync(source, chunk, langs, perChunk, ct),
                    options.MaxDegreeOfParallelism,
                    cancellationToken);

                if (options.Grouping == TextGrouping.Paragraph)
                {
                    // Paragraphs are formed from lines anywhere on the page, so hold everything, merge
                    // once, and emit the paragraphs in reading order — identical to the buffered result.
                    var page = new List<OcrLine>();
                    await foreach (var line in stream.ConfigureAwait(false))
                    {
                        page.Add(line);
                    }
                    foreach (var paragraph in SortLinesByReadingOrder(ParagraphGrouper.Merge(page, options.GroupingOptions, rightToLeft), rightToLeft))
                    {
                        emitted++;
                        yield return paragraph;
                    }
                }
                else
                {
                    await foreach (var line in stream.ConfigureAwait(false))
                    {
                        emitted++;
                        yield return line;
                    }
                }
            }
        }
        finally
        {
            preprocessed?.Dispose();
            docCorrected?.Dispose();

            sw.Stop();
            // Recorded even when the consumer abandons the enumeration, so a partially consumed stream
            // still shows up in the metrics with what it actually produced. The measurements themselves
            // are emitted by the recorder — tagged with the operation, provider and outcome — when
            // StreamCoreAsync disposes it, which happens after this finally on every exit path.
            recorder.WithLanguages(langs).AddLines(emitted);
            if (activity is not null)
            {
                activity.SetTag("easyocr.languages", string.Join(",", langs));
                activity.SetTag("easyocr.lines", emitted);
                activity.SetTag("easyocr.gpu", _useGpu);
                activity.SetTag("easyocr.streaming", true);
            }
            _logger?.LogInformation("Streaming OCR completed: {Count} lines in {Ms:F0} ms", emitted, sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Recognizes one chunk of detected regions, holding an operation scope for its duration.</summary>
    private async Task<IReadOnlyList<OcrLine>> RecognizeChunkAsync(
        Image<Rgb24> image,
        IReadOnlyList<DetectedRegion> chunk,
        string[] languages,
        RecognitionOptions options,
        CancellationToken cancellationToken)
    {
        using var op = BeginOperation();
        var polygons = new OcrPoint[chunk.Count][];
        for (int i = 0; i < chunk.Count; i++)
        {
            var polygon = chunk[i].BoundingPolygon;
            polygons[i] = polygon as OcrPoint[] ?? polygon.ToArray();
        }
        return await _engine.RecognizeRegionsAsync(image, languages, polygons, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the recognizer languages for a streamed page — the caller's list, or the auto-detected
    /// script when <see cref="RecognitionOptions.AutoDetectLanguage"/> is on (mirroring <c>CoreAsync</c>).
    /// </summary>
    private async Task<string[]> ResolveStreamLanguagesAsync(
        Image<Rgb24> image,
        IEnumerable<string> languages,
        RecognitionOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.AutoDetectLanguage)
        {
            return ResolveLanguages(languages, allowEmpty: false);
        }

        // The ungated core: the caller already holds this stream's one governor slot.
        var detected = await DetectLanguagesCoreAsync(image, options.AutoDetectCandidates, cancellationToken).ConfigureAwait(false);
        if (detected.Count > 0)
        {
            _logger?.LogInformation("Auto-detected languages: {Langs}", string.Join(", ", detected));
            return detected.ToArray();
        }

        var requested = ResolveLanguages(languages, allowEmpty: true);
        return requested.Length > 0 ? requested : new[] { "en" };
    }

    /// <summary>
    /// Puts the detected regions into the reading order the buffered API reports its lines in, so a
    /// streamed enumeration reads like the page instead of like the detector's scan pattern. Reuses
    /// <see cref="SortLinesByReadingOrder"/> — the one and only ordering rule (columns first, then rows
    /// banded by median line height) — by handing it a placeholder line per region: the sorter looks at
    /// nothing but <see cref="OcrLine.BoundingBox"/>, and identity (not value) lookup maps each sorted
    /// placeholder back to its region even when two regions share identical geometry.
    /// </summary>
    internal static IReadOnlyList<DetectedRegion> OrderRegionsForReading(IReadOnlyList<DetectedRegion> regions, bool rightToLeft = false)
    {
        if (regions.Count <= 1) return regions;

        var placeholders = new OcrLine[regions.Count];
        var origin = new Dictionary<OcrLine, int>(regions.Count, LineIdentityComparer.Instance);
        for (int i = 0; i < regions.Count; i++)
        {
            placeholders[i] = new OcrLine
            {
                Text = string.Empty,
                BoundingPolygon = regions[i].BoundingPolygon,
                BoundingBox = regions[i].BoundingBox,
            };
            origin[placeholders[i]] = i;
        }

        var sorted = SortLinesByReadingOrder(placeholders, rightToLeft);
        var ordered = new List<DetectedRegion>(regions.Count);
        foreach (var placeholder in sorted)
        {
            ordered.Add(regions[origin[placeholder]]);
        }
        return ordered;
    }

    /// <summary>
    /// Reference-identity comparer for <see cref="OcrLine"/>. Records compare by value, which would make
    /// two placeholder lines with the same geometry collide in a dictionary; this keeps them distinct.
    /// </summary>
    private sealed class LineIdentityComparer : IEqualityComparer<OcrLine>
    {
        public static readonly LineIdentityComparer Instance = new();

        public bool Equals(OcrLine? x, OcrLine? y) => ReferenceEquals(x, y);

        public int GetHashCode(OcrLine obj) => RuntimeHelpers.GetHashCode(obj);
    }
}

/// <summary>
/// Order-preserving, bounded-concurrency bridge between a list of work items and an
/// <see cref="IAsyncEnumerable{T}"/> of their results.
/// </summary>
/// <remarks>
/// <para>
/// Work items are started in input order, at most <c>maxConcurrency</c> at a time, and their in-flight
/// <see cref="Task"/>s are queued through a bounded channel. The consumer awaits those
/// tasks in the very order they were queued, so results come out in input order while up to
/// <c>maxConcurrency</c> items are computed ahead — order without buffering the whole page, and a
/// look-ahead window bounded by the concurrency rather than by the item count.
/// </para>
/// <para>
/// The channel is completed exactly once, by the producer, on every path (drained, cancelled or faulted).
/// A fault raised by an item surfaces to the consumer unwrapped, on the item that raised it, after every
/// earlier item has been yielded. Abandoning the enumeration — <c>break</c>, an exception, or a
/// cancellation — cancels the producer, stops new items from starting, and observes whatever was already
/// in flight so nothing is left as an unobserved task exception.
/// </para>
/// </remarks>
internal static class OrderedStreamPump
{
    /// <summary>
    /// Processes <paramref name="work"/> with at most <paramref name="maxConcurrency"/> concurrent calls
    /// to <paramref name="process"/> and yields the results in input order, flattening each item's result
    /// list. <paramref name="process"/> is invoked on the thread pool, so a synchronous, CPU-bound body
    /// (as ONNX inference is) never blocks the producer or the consumer.
    /// </summary>
    internal static async IAsyncEnumerable<TResult> RunOrderedAsync<TSource, TResult>(
        IReadOnlyList<TSource> work,
        Func<TSource, CancellationToken, Task<IReadOnlyList<TResult>>> process,
        int maxConcurrency,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(process);
        if (work.Count == 0) yield break;

        int concurrency = Math.Max(1, maxConcurrency);
        var channel = Channel.CreateBounded<Task<IReadOnlyList<TResult>>>(new BoundedChannelOptions(concurrency)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // Cancelled when the consumer walks away, so the producer stops even if its own token never fires.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Never disposed: SemaphoreSlim only holds a resource once AvailableWaitHandle is touched (it is
        // not), and disposing it here would race with an in-flight item releasing it after an early exit.
        var slots = new SemaphoreSlim(concurrency, concurrency);
        var producer = ProduceAsync(work, process, channel.Writer, slots, stop.Token);

        Task<IReadOnlyList<TResult>>? awaiting = null;
        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var item))
                {
                    awaiting = item;
                    // WaitAsync so cancellation is observed immediately even when the item itself is deep
                    // inside a synchronous native call; the abandoned task is observed in the finally.
                    var results = await item.WaitAsync(cancellationToken).ConfigureAwait(false);
                    awaiting = null;
                    foreach (var result in results)
                    {
                        yield return result;
                    }
                }
            }
        }
        finally
        {
            stop.Cancel();
            // The producer swallows its own faults into the channel completion, so this never throws;
            // awaiting it guarantees no further item is queued behind our back before we drain.
            await producer.ConfigureAwait(false);

            // AWAIT the abandoned work rather than merely observing its faults. When the consumer breaks out
            // of the enumeration early, up to MaxDegreeOfParallelism recognition tasks are still inside
            // synchronous native inference, reading the page image. stop.Cancel() cannot interrupt them --
            // the detector and recognizer take no token -- so returning here would let StreamCoreAsync's own
            // finally dispose that image out from under them, reading pixel memory already returned to the
            // pool. This is the only point that can guarantee no task outlives the image.
            if (awaiting is not null) await DrainAsync(awaiting).ConfigureAwait(false);
            while (channel.Reader.TryRead(out var leftover)) await DrainAsync(leftover).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts items in order — each one gated on a concurrency slot — and queues them for the consumer.
    /// Completes the writer exactly once: cleanly when the list is exhausted or the run was cancelled,
    /// faulted when something unexpected went wrong while producing.
    /// </summary>
    private static async Task ProduceAsync<TSource, TResult>(
        IReadOnlyList<TSource> work,
        Func<TSource, CancellationToken, Task<IReadOnlyList<TResult>>> process,
        ChannelWriter<Task<IReadOnlyList<TResult>>> writer,
        SemaphoreSlim slots,
        CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<TResult>>? started = null;
        try
        {
            foreach (var item in work)
            {
                cancellationToken.ThrowIfCancellationRequested();
                started = RunOneAsync(item, process, slots, cancellationToken);
                await writer.WriteAsync(started, cancellationToken).ConfigureAwait(false);
                started = null;
            }
            writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the consumer's business to report (it holds the original token); completing
            // cleanly here keeps a ChannelClosedException from masking it. The task started but never queued
            // is reading the page image, so wait it out here -- RunOrderedAsync awaits this producer before
            // its caller disposes that image.
            if (started is not null) await DrainAsync(started).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            if (started is not null) await DrainAsync(started).ConfigureAwait(false);
            writer.TryComplete(ex);
        }
    }

    /// <summary>Waits for a concurrency slot, runs one item on the thread pool, and frees the slot.</summary>
    private static async Task<IReadOnlyList<TResult>> RunOneAsync<TSource, TResult>(
        TSource item,
        Func<TSource, CancellationToken, Task<IReadOnlyList<TResult>>> process,
        SemaphoreSlim slots,
        CancellationToken cancellationToken)
    {
        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => process(item, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>
    /// Waits for a task whose result nobody will consume, swallowing whatever it finishes with. Used to make
    /// abandoned in-flight work quiesce before the caller tears down the image it is reading; it also marks
    /// the fault observed, so nothing surfaces later as an unobserved task exception.
    /// </summary>
    private static async Task DrainAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The consumer abandoned this item, so its outcome -- fault or cancellation -- is irrelevant.
            // What matters is that the task has stopped touching the source image.
        }
    }
}
