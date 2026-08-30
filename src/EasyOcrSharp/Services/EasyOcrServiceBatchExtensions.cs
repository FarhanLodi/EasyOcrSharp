using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EasyOcrSharp.Diagnostics;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Services;

/// <summary>
/// One image's outcome in a batch. Either <see cref="Result"/> or <see cref="Error"/> is set, so a
/// single bad file never aborts the whole batch.
/// </summary>
public sealed record OcrBatchResult
{
    /// <summary>The image this result is for (the path passed in).</summary>
    public required string Source { get; init; }

    /// <summary>The OCR result, or null if recognition failed (see <see cref="Error"/>).</summary>
    public OcrResult? Result { get; init; }

    /// <summary>The failure, or null on success.</summary>
    public Exception? Error { get; init; }

    /// <summary>True when OCR succeeded.</summary>
    public bool Succeeded => Error is null && Result is not null;
}

/// <summary>
/// Batch helpers layered over the existing single-image API — folder/queue processing with bounded
/// concurrency. Provided as extensions so they work with any <see cref="IEasyOcrService"/>.
/// </summary>
/// <remarks>
/// A batch emits one <c>easyocr.operations</c> / <c>easyocr.duration</c> data point covering the whole run
/// — see <see cref="EasyOcrDiagnostics"/> — while each image inside it is still measured by the
/// single-image API. No concurrency slot is taken here: the per-image calls are already gated, and a slot
/// held across the whole batch would deadlock a service limited to one concurrent operation.
/// </remarks>
public static class EasyOcrServiceBatchExtensions
{

    /// <summary>
    /// OCRs many image files with bounded concurrency, yielding each result as it completes (order is
    /// not preserved — use <see cref="OcrBatchResult.Source"/> to correlate). Per-image failures are
    /// captured in <see cref="OcrBatchResult.Error"/> rather than thrown.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imagePaths">Image file paths to process.</param>
    /// <param name="languages">Languages to recognize (same as the single-image API).</param>
    /// <param name="options">Recognition options applied to every image.</param>
    /// <param name="maxConcurrency">Max images processed at once. ≤0 selects a safe default (half the CPU count).</param>
    /// <param name="cancellationToken">Cancels the whole batch.</param>
    public static async IAsyncEnumerable<OcrBatchResult> ExtractTextFromImagesAsync(
        this IEasyOcrService service,
        IEnumerable<string> imagePaths,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        int maxConcurrency = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(imagePaths);
        ArgumentNullException.ThrowIfNull(languages);

        // Each OCR call already parallelizes its regions, so default to a modest fan-out to avoid
        // oversubscribing the CPU. Callers can raise it (e.g. when GPU-bound or IO-bound).
        int concurrency = maxConcurrency > 0 ? maxConcurrency : Math.Max(1, Environment.ProcessorCount / 2);
        var langs = languages as string[] ?? languages.ToArray();

        // Declared before the recorder so it outlives it: the recorder stamps the span from its own
        // Dispose, and tags set on a stopped activity are dropped by every exporter.
        using var activity = EasyOcrDiagnostics.ActivitySource.StartActivity("EasyOcr.Batch", ActivityKind.Internal);
        using var recorder = EasyOcrDiagnostics.Begin(EasyOcrDiagnostics.OperationNames.Batch, ProviderOf(service))
            .WithLanguages(langs)
            .Annotate(activity);

        // `yield return` cannot sit inside a try/catch, so the outcome is tracked in this local and settled
        // in the finally below.
        bool settled = false;

        var output = Channel.CreateUnbounded<OcrBatchResult>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        // Linked so that a consumer abandoning the enumeration (break, or an exception in its own loop body)
        // stops the pump. Without it the pump keeps enumerating imagePaths and OCRing every remaining file
        // into an unbounded channel nobody drains -- CPU burned and memory grown after the caller believed
        // the batch was over.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pumpToken = stop.Token;

        var pump = Task.Run(async () =>
        {
            // Deliberately NOT `using`: disposing the semaphore here would race the in-flight ProcessOneAsync
            // tasks, whose finally calls gate.Release(). On cancellation the WhenAll below is skipped, so
            // those releases would hit a disposed semaphore and throw ObjectDisposedException from inside a
            // finally, on tasks nobody awaits. SemaphoreSlim needs no disposal unless AvailableWaitHandle is
            // touched, and it is not.
            var gate = new SemaphoreSlim(concurrency);
            var tasks = new List<Task>();
            try
            {
                foreach (var path in imagePaths)
                {
                    pumpToken.ThrowIfCancellationRequested();
                    await gate.WaitAsync(pumpToken).ConfigureAwait(false);
                    tasks.Add(ProcessOneAsync(service, path, langs, options, output.Writer, gate, pumpToken));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                // Let whatever is still running finish before the writer is closed, on every exit path.
                foreach (var task in tasks)
                {
                    try { await task.ConfigureAwait(false); } catch { /* reported per-item or cancelled */ }
                }
                output.Writer.TryComplete();
            }
        }, pumpToken);

        try
        {
            await foreach (var item in output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Only images that actually produced a result count as pages. A path that could not be read
                // was never OCRed, and counting it would make a batch of missing files report a full run's
                // worth of work at zero lines.
                if (item.Succeeded)
                {
                    recorder.AddPages(1).AddLines(item.Result!.Lines.Count);
                }
                yield return item;
            }

            // A per-image failure is reported through OcrBatchResult.Error rather than thrown, so a batch
            // that drained is a success even when individual files failed — that is this method's contract.
            recorder.Success();
            settled = true;
        }
        finally
        {
            // Reached on break and on an exception too, unlike a bare `await pump` after the loop.
            stop.Cancel();
            try
            {
                await pump.ConfigureAwait(false); // surface cancellation / fatal enumeration errors
            }
            catch (OperationCanceledException)
            {
                // Expected when the consumer stopped early; the caller's own token governs what it sees.
            }
            catch (Exception ex)
            {
                // A fatal pump fault — imagePaths itself throwing part-way, say — closes the channel, so the
                // drain above completes and calls Success before this surfaces. It has to overwrite that
                // verdict, or a batch that ended in an exception would be counted as a clean run.
                recorder.Failure(ex);
                settled = true;
                throw;
            }

            // Unsettled means the enumeration never finished and nothing threw here: the caller cancelled,
            // or walked away after the results it wanted. Neither is a fault, and recording it as one would
            // let a single early break poison the error rate on a dashboard.
            if (!settled)
            {
                recorder.Canceled();
            }
        }
    }

    /// <summary>
    /// The <see cref="EasyOcrDiagnostics.TagNames.Provider"/> value for a composite operation.
    /// <see cref="EasyOcrService"/> keeps the exact resolved provider private and this is an extension on
    /// the interface, so the tag is coarse here: "Cpu" matches the per-image points exactly, a GPU service
    /// reports "Gpu" without claiming to know whether it is CUDA, DirectML or CoreML, and a caller's own
    /// <see cref="IEasyOcrService"/> reports "unknown" rather than a guess that would make a dashboard's
    /// CPU/GPU split quietly wrong.
    /// </summary>
    private static string ProviderOf(IEasyOcrService service)
        => service is EasyOcrService concrete ? concrete.ProviderName : "unknown";

    private static async Task ProcessOneAsync(
        IEasyOcrService service, string path, string[] langs, RecognitionOptions? options,
        ChannelWriter<OcrBatchResult> writer, SemaphoreSlim gate, CancellationToken ct)
    {
        try
        {
            var result = await service.ExtractTextFromImage(path, langs, options, ct).ConfigureAwait(false);
            await writer.WriteAsync(new OcrBatchResult { Source = path, Result = result }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // batch-level cancellation: let the pump observe it
        }
        catch (Exception ex)
        {
            await writer.WriteAsync(new OcrBatchResult { Source = path, Error = ex }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
