using System.Runtime.CompilerServices;
using System.Threading.Channels;
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

        var output = Channel.CreateUnbounded<OcrBatchResult>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var pump = Task.Run(async () =>
        {
            using var gate = new SemaphoreSlim(concurrency);
            var tasks = new List<Task>();
            try
            {
                foreach (var path in imagePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    tasks.Add(ProcessOneAsync(service, path, langs, options, output.Writer, gate, cancellationToken));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                output.Writer.TryComplete();
            }
        }, cancellationToken);

        await foreach (var item in output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }

        await pump.ConfigureAwait(false); // surface cancellation / fatal enumeration errors
    }

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
