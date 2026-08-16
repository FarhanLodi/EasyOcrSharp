namespace EasyOcrSharp.WebApi.Internal;

/// <summary>
/// Bounds how many OCR operations run at once and sheds load instead of queueing without limit.
/// </summary>
/// <remarks>
/// <para>
/// OCR is CPU-bound and already parallel inside a single call (see
/// <c>RecognitionOptions.MaxDegreeOfParallelism</c>). Letting the web server's own concurrency decide
/// how many of those run simultaneously produces the classic failure mode: a hundred in-flight requests
/// all making progress at 1% of the speed, every one of them eventually timing out, and a thread pool
/// and working set that grow until the process is killed.
/// </para>
/// <para>
/// So requests wait a bounded time for a slot, and a request that does not get one is refused promptly
/// with 503 + <c>Retry-After</c>. A caller that is told "busy" in 15 seconds can retry or fail over; a
/// caller silently queued for four minutes cannot.
/// </para>
/// </remarks>
internal sealed class OcrConcurrencyGate : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _queueTimeout;
    private int _waiting;

    /// <summary>Creates the gate from the configured concurrency limit and queue timeout.</summary>
    public OcrConcurrencyGate(WebApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Capacity = options.MaxConcurrentOcr;
        _slots = new SemaphoreSlim(Capacity, Capacity);
        _queueTimeout = TimeSpan.FromSeconds(options.QueueTimeoutSeconds);
    }

    /// <summary>Maximum number of concurrent OCR operations.</summary>
    public int Capacity { get; }

    /// <summary>Slots currently free. Diagnostic only — inherently racy.</summary>
    public int Available => _slots.CurrentCount;

    /// <summary>Requests currently waiting for a slot. Diagnostic only — inherently racy.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>How long a request waits for a slot before being shed.</summary>
    public TimeSpan QueueTimeout => _queueTimeout;

    /// <summary>
    /// Waits for a slot. The returned lease reports <see cref="Lease.Acquired"/> = <see langword="false"/>
    /// when the wait timed out; disposing an unacquired lease is a no-op, so the call site can always
    /// wrap it in <c>using</c>. Cancellation (a disconnected client) surfaces as
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _waiting);
        try
        {
            var acquired = await _slots.WaitAsync(_queueTimeout, cancellationToken).ConfigureAwait(false);
            return acquired ? new Lease(_slots) : default;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _slots.Dispose();

    /// <summary>
    /// A held slot, released on disposal. A <see langword="default"/> instance represents "not
    /// admitted" and releases nothing.
    /// </summary>
    internal readonly struct Lease : IDisposable
    {
        private readonly SemaphoreSlim? _slots;

        internal Lease(SemaphoreSlim slots) => _slots = slots;

        /// <summary>Whether a slot was actually obtained.</summary>
        public bool Acquired => _slots is not null;

        /// <summary>Releases the slot, if one was held.</summary>
        public void Dispose() => _slots?.Release();
    }
}
