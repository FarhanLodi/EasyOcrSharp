using System.Diagnostics;
using EasyOcrSharp.Diagnostics;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Bounds how many OCR operations a service runs at once and how long one of them may take. Off by
/// default: with no limit and no timeout configured, <see cref="AcquireAsync"/> hands back the caller's
/// own token and touches nothing, so the pipeline behaves exactly as it did before the governor existed.
/// </summary>
/// <remarks>
/// Sheds rather than queues without limit when asked to (<c>QueueTimeout</c>), because an unbounded queue
/// in front of a saturated ONNX session turns a busy service into one that answers every request slowly
/// and none of them in time.
/// </remarks>
internal sealed class OperationGovernor : IDisposable
{
    private readonly SemaphoreSlim? _slots;
    private readonly TimeSpan _queueTimeout;
    private readonly TimeSpan _operationTimeout;
    private readonly int _capacity;
    private volatile bool _disposed;

    /// <summary>
    /// The longest delay .NET's timer infrastructure accepts: <c>uint.MaxValue - 1</c> milliseconds
    /// (~49.7 days). Anything beyond it throws <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    internal OperationGovernor(int maxConcurrentOperations, TimeSpan queueTimeout, TimeSpan operationTimeout)
    {
        _capacity = Math.Max(0, maxConcurrentOperations);
        _slots = _capacity > 0 ? new SemaphoreSlim(_capacity, _capacity) : null;
        // Both waits are clamped to what the timer infrastructure actually accepts. A negative wait is not
        // "reject immediately" (that is TimeSpan.Zero), and any wait longer than ~49.7 days is
        // indistinguishable from waiting forever; both become an infinite wait rather than an exception,
        // so a fat-fingered configuration degrades instead of faulting every call.
        //
        // The ceiling is measured, not assumed, and it is NOT int.MaxValue milliseconds. Both sinks a
        // timeout reaches from here — SemaphoreSlim.WaitAsync and CancellationTokenSource.CancelAfter —
        // bottom out on a timer whose limit is uint.MaxValue-1 ms, so a 40-day wait is legal.
        //
        // Getting this wrong is expensive in a way that hides: WaitAsync(TimeSpan) does NOT validate its
        // argument up front. When a permit is free it returns on the fast path without ever arming a timer,
        // so an over-long QueueTimeout survives every uncontended dev run and CI pass — then throws
        // ArgumentOutOfRangeException out of the first request that actually has to queue. The back-pressure
        // path would fail precisely when back-pressure exists, handing callers an argument exception instead
        // of the OcrBusyException they are meant to retry on.
        _queueTimeout = Clamp(queueTimeout);
        // OperationTimeout reaches CancelAfter, which has the same ceiling but validates eagerly — so an
        // over-long budget here would throw on every operation, contended or not.
        _operationTimeout = operationTimeout > TimeSpan.Zero ? Clamp(operationTimeout) : Timeout.InfiniteTimeSpan;

        static TimeSpan Clamp(TimeSpan wait)
            => wait < TimeSpan.Zero || wait > MaxTimerDelay ? Timeout.InfiniteTimeSpan : wait;
    }

    /// <summary>Whether anything is actually enforced; false for the default configuration.</summary>
    internal bool IsEnabled => _slots is not null || _operationTimeout != Timeout.InfiniteTimeSpan;

    /// <summary>
    /// The queue wait after clamping — <see cref="Timeout.InfiniteTimeSpan"/> when the configured value was
    /// negative or beyond <see cref="MaxTimerDelay"/>. Exposed for tests: whether a large wait was honoured
    /// or silently coerced is otherwise indistinguishable from the outside (both simply queue), which is
    /// exactly how a clamp set to the wrong ceiling escapes notice.
    /// </summary>
    internal TimeSpan EffectiveQueueTimeout => _queueTimeout;

    /// <summary>The operation budget after clamping; <see cref="Timeout.InfiniteTimeSpan"/> means no cap.</summary>
    internal TimeSpan EffectiveOperationTimeout => _operationTimeout;

    /// <summary>
    /// Waits for a slot and returns the lease the operation must run under. Throws
    /// <see cref="OcrBusyException"/> when the queue wait elapses first, and
    /// <see cref="OperationCanceledException"/> if the caller has already cancelled.
    /// </summary>
    internal async ValueTask<OperationLease> AcquireAsync(string operation, CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Services.EasyOcrService));
        // Fail fast on an already-cancelled token instead of loading models for a call nobody is waiting
        // for; the pipeline would have thrown the same exception several hundred milliseconds later.
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsEnabled) return OperationLease.PassThrough(cancellationToken);

        if (_slots is not null)
        {
            // The wait is measured even when it is instant. A shed request is FAST, not slow, so latency
            // alone cannot tell a healthy service from a saturated one — the queue wait is the signal that
            // says whether MaxConcurrentOperations is set too low.
            var queuedTag = new KeyValuePair<string, object?>(EasyOcrDiagnostics.TagNames.Operation, operation);
            var waitStarted = Stopwatch.GetTimestamp();
            EasyOcrDiagnostics.QueuedOperations.Add(1, queuedTag);

            bool admitted;
            try
            {
                admitted = await _slots.WaitAsync(_queueTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                RecordWait(operation, waitStarted, EasyOcrDiagnostics.Outcomes.Canceled);
                throw;
            }
            finally
            {
                EasyOcrDiagnostics.QueuedOperations.Add(-1, queuedTag);
            }

            RecordWait(operation, waitStarted, admitted ? EasyOcrDiagnostics.Outcomes.Success : EasyOcrDiagnostics.Outcomes.Shed);

            if (!admitted)
            {
                throw new OcrBusyException(
                    $"All {_capacity} OCR slot(s) (EasyOcrServiceOptions.MaxConcurrentOperations) are in use and the " +
                    $"queue wait of {_queueTimeout.TotalSeconds:0.###}s (EasyOcrServiceOptions.QueueTimeout) elapsed " +
                    $"before '{operation}' could start. Retry shortly, raise the limits, or add capacity.");
            }
        }

        try
        {
            return new OperationLease(_slots, _operationTimeout, cancellationToken, operation);
        }
        catch
        {
            // The slot is taken at this point; leaking it would permanently shrink the service's capacity.
            _slots?.Release();
            throw;
        }
    }

    private static void RecordWait(string operation, long startedAt, string outcome)
        => EasyOcrDiagnostics.QueueWait.Record(
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            new KeyValuePair<string, object?>(EasyOcrDiagnostics.TagNames.Operation, operation),
            new KeyValuePair<string, object?>(EasyOcrDiagnostics.TagNames.Outcome, outcome));

    /// <summary>
    /// Stops new operations being admitted. The semaphore itself is deliberately NOT disposed: it holds no
    /// unmanaged resource unless <c>AvailableWaitHandle</c> is touched (it never is), and disposing it
    /// would race with a call already queued at the gate, replacing its clean ObjectDisposedException for
    /// the service with one naming a SemaphoreSlim.
    /// </summary>
    public void Dispose() => _disposed = true;
}

/// <summary>
/// One admitted operation: the slot it holds (released on <see cref="Dispose"/>) and the token it must
/// run under.
/// </summary>
internal sealed class OperationLease : IDisposable
{
    private readonly SemaphoreSlim? _slots;
    private readonly CancellationTokenSource? _timeout;
    private readonly CancellationToken _callerToken;
    private readonly TimeSpan _operationTimeout;
    private readonly string _operation;
    private int _released;

    internal OperationLease(SemaphoreSlim? slots, TimeSpan operationTimeout, CancellationToken callerToken, string operation)
    {
        _slots = slots;
        _callerToken = callerToken;
        _operation = operation;
        _operationTimeout = operationTimeout;

        if (operationTimeout != Timeout.InfiniteTimeSpan)
        {
            _timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            _timeout.CancelAfter(operationTimeout);
            Token = _timeout.Token;
        }
        else
        {
            // No timeout configured: hand back the caller's own token, so the default configuration
            // allocates no linked source and registers no callback on the caller's token.
            Token = callerToken;
        }
    }

    /// <summary>A lease that enforces nothing — the default configuration's fast path.</summary>
    internal static OperationLease PassThrough(CancellationToken cancellationToken)
        => new(null, Timeout.InfiniteTimeSpan, cancellationToken, string.Empty);

    /// <summary>The token the operation must run under; the caller's own when no timeout is configured.</summary>
    internal CancellationToken Token { get; }

    /// <summary>
    /// Decides what a cancellation coming out of the pipeline actually was. Both a caller cancellation and
    /// an operation timeout arrive as the same exception type through the same linked token, and only the
    /// lease knows which source fired — so a timeout becomes <see cref="OcrTimeoutException"/> while a
    /// genuine caller cancellation is returned untouched and stays an <see cref="OperationCanceledException"/>.
    /// </summary>
    internal Exception Translate(OperationCanceledException exception)
    {
        // Caller first: if they asked to stop, that is the answer even if the timeout also expired.
        if (_callerToken.IsCancellationRequested) return exception;
        if (_timeout is { IsCancellationRequested: true })
        {
            return new OcrTimeoutException(
                $"The OCR operation '{_operation}' exceeded the {_operationTimeout.TotalSeconds:0.###}s budget " +
                "(EasyOcrServiceOptions.OperationTimeout) and was cancelled.", exception);
        }
        return exception;
    }

    /// <summary>Releases the slot. Idempotent — a double release would inflate the service's capacity.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        _timeout?.Dispose();
        _slots?.Release();
    }
}
