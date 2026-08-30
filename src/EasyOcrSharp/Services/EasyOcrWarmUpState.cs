namespace EasyOcrSharp.Services;

/// <summary>
/// Lifecycle phase of the background model warm-up performed by <see cref="EasyOcrWarmUpService"/>.
/// </summary>
public enum EasyOcrWarmUpStatus
{
    /// <summary>
    /// The hosted service has not reached its warm-up call yet (the host is still starting, or warm-up
    /// was never registered). A readiness probe should treat this as <b>not ready</b>: models may still
    /// be downloading or absent.
    /// </summary>
    NotStarted = 0,

    /// <summary>Models are being downloaded and/or ONNX sessions created right now.</summary>
    InProgress = 1,

    /// <summary>Warm-up finished successfully; the first real request will not pay cold-start cost.</summary>
    Completed = 2,

    /// <summary>
    /// Warm-up threw (unreachable model host, corrupt cache, unusable execution provider). The process is
    /// still usable — models are loaded lazily on first use — but a probe should surface this.
    /// </summary>
    Failed = 3,
}

/// <summary>
/// A consistent point-in-time view of <see cref="EasyOcrWarmUpState"/>. Reading the individual
/// properties one by one can tear across a state transition (for example observing
/// <see cref="EasyOcrWarmUpStatus.Failed"/> but a still-null <see cref="Error"/>), so anything that
/// reports on more than one field — the health check included — reads a snapshot instead.
/// </summary>
/// <param name="Status">Phase the warm-up was in when the snapshot was taken.</param>
/// <param name="Languages">Languages the warm-up was asked to preload.</param>
/// <param name="Elapsed">Wall-clock time the warm-up took (zero until it finishes).</param>
/// <param name="Error">The failure, when <paramref name="Status"/> is <see cref="EasyOcrWarmUpStatus.Failed"/>.</param>
public readonly record struct EasyOcrWarmUpSnapshot(
    EasyOcrWarmUpStatus Status,
    IReadOnlyList<string> Languages,
    TimeSpan Elapsed,
    Exception? Error);

/// <summary>
/// Thread-safe, publicly readable record of how the background model warm-up went. Registered as a
/// singleton by <see cref="ServiceCollectionExtensions.AddEasyOcrWarmUp"/>, written by
/// <see cref="EasyOcrWarmUpService"/>, and read by <see cref="EasyOcrHealthCheck"/> — or by any app that
/// wants to surface warm-up progress on its own status page.
/// </summary>
/// <remarks>
/// Warm-up runs on a background thread while requests are already arriving on others, so every read and
/// write goes through one lock. The fields are written only a handful of times per process, so the lock
/// is never contended in practice.
/// </remarks>
public sealed class EasyOcrWarmUpState
{
    private readonly object _gate = new();
    private EasyOcrWarmUpStatus _status = EasyOcrWarmUpStatus.NotStarted;
    private IReadOnlyList<string> _languages = Array.Empty<string>();
    private TimeSpan _elapsed;
    private Exception? _error;

    /// <summary>Current phase of the warm-up.</summary>
    public EasyOcrWarmUpStatus Status
    {
        get { lock (_gate) { return _status; } }
    }

    /// <summary>Languages the warm-up was asked to preload. Empty until warm-up starts.</summary>
    public IReadOnlyList<string> Languages
    {
        get { lock (_gate) { return _languages; } }
    }

    /// <summary>Wall-clock time the warm-up took. <see cref="TimeSpan.Zero"/> until it finishes.</summary>
    public TimeSpan Elapsed
    {
        get { lock (_gate) { return _elapsed; } }
    }

    /// <summary>The exception that ended the warm-up, or null when it has not failed.</summary>
    public Exception? Error
    {
        get { lock (_gate) { return _error; } }
    }

    /// <summary>
    /// True while warm-up has not reached a terminal phase — that is, it is
    /// <see cref="EasyOcrWarmUpStatus.NotStarted"/> or <see cref="EasyOcrWarmUpStatus.InProgress"/>.
    /// Readiness probes report "not ready" while this is true.
    /// </summary>
    public bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _status is EasyOcrWarmUpStatus.NotStarted or EasyOcrWarmUpStatus.InProgress;
            }
        }
    }

    /// <summary>Takes a consistent snapshot of every field at once. See <see cref="EasyOcrWarmUpSnapshot"/>.</summary>
    public EasyOcrWarmUpSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new EasyOcrWarmUpSnapshot(_status, _languages, _elapsed, _error);
        }
    }

    /// <summary>Records that warm-up has begun for the given languages.</summary>
    internal void MarkStarted(IReadOnlyList<string> languages)
    {
        lock (_gate)
        {
            _status = EasyOcrWarmUpStatus.InProgress;
            _languages = languages;
            _elapsed = TimeSpan.Zero;
            _error = null;
        }
    }

    /// <summary>Records a successful warm-up and how long it took.</summary>
    internal void MarkCompleted(TimeSpan elapsed)
    {
        lock (_gate)
        {
            _status = EasyOcrWarmUpStatus.Completed;
            _elapsed = elapsed;
            _error = null;
        }
    }

    /// <summary>Records a failed warm-up, keeping the exception so a probe can report the real cause.</summary>
    internal void MarkFailed(TimeSpan elapsed, Exception error)
    {
        lock (_gate)
        {
            _status = EasyOcrWarmUpStatus.Failed;
            _elapsed = elapsed;
            _error = error;
        }
    }
}
