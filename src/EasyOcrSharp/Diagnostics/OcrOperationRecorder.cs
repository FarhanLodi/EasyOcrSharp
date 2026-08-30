using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EasyOcrSharp.Diagnostics;

/// <summary>
/// Records one logical OCR operation — its duration, its outcome, and the work it produced — emitting
/// <c>easyocr.operations</c>, <c>easyocr.duration</c>, <c>easyocr.lines</c> and <c>easyocr.pages</c>
/// under one tag set, and stamping that same tag set on the operation's span so a dashboard and a trace
/// slice identically.
/// </summary>
/// <remarks>
/// <para>
/// The outcome starts at <see cref="EasyOcrDiagnostics.Outcomes.Error"/> deliberately. The instruments
/// used to be incremented only on the success path, so a service failing every single request looked
/// idle rather than broken — the one thing an operator most needs to see. An operation that throws past
/// its own bookkeeping still emits a data point; the worst case is a mislabelled error, never a missing
/// one.
/// </para>
/// <para>
/// A class, not a struct: <c>using var</c> locals are read-only, so a mutable struct would hand every
/// <see cref="AddLines"/> call a defensive copy and silently drop it.
/// </para>
/// </remarks>
internal sealed class OcrOperationRecorder : IDisposable
{
    private readonly string _operation;
    private readonly string _provider;
    private readonly long _startedAt = Stopwatch.GetTimestamp();

    private Activity? _activity;
    private string _outcome = EasyOcrDiagnostics.Outcomes.Error;
    private string? _errorType;
    private string? _languages;
    private long _lines;
    private long _pages;
    private bool _disposed;

    internal OcrOperationRecorder(string operation, string provider)
    {
        _operation = operation;
        _provider = provider;
        EasyOcrDiagnostics.ActiveOperations.Add(1, new KeyValuePair<string, object?>(EasyOcrDiagnostics.TagNames.Operation, operation));
    }

    /// <summary>Records the languages the operation actually ran with (auto-detected ones included).</summary>
    internal OcrOperationRecorder WithLanguages(IEnumerable<string>? languages)
    {
        if (languages is not null) _languages = string.Join(",", languages);
        return this;
    }

    /// <summary>Adds to the count of recognized text lines reported for this operation.</summary>
    internal OcrOperationRecorder AddLines(int count)
    {
        if (count > 0) _lines += count;
        return this;
    }

    /// <summary>Adds to the count of pages processed by this operation (PDF pages, TIFF frames, documents).</summary>
    internal OcrOperationRecorder AddPages(int count)
    {
        if (count > 0) _pages += count;
        return this;
    }

    /// <summary>
    /// Attaches the operation's span. The tags are written on <see cref="Dispose"/>, so the span must
    /// outlive the recorder — declare the activity first, the recorder second, and reverse-order disposal
    /// does the rest. Tags set after an activity has stopped are never exported.
    /// </summary>
    internal OcrOperationRecorder Annotate(Activity? activity)
    {
        _activity = activity;
        return this;
    }

    /// <summary>Marks the operation as having completed normally.</summary>
    internal void Success() => _outcome = EasyOcrDiagnostics.Outcomes.Success;

    /// <summary>
    /// Marks the operation as cancelled — used where no exception is available, such as a consumer that
    /// walks away from a streaming enumeration part-way through. Abandoning a stream is a choice, not a
    /// fault, and must not be counted as an error.
    /// </summary>
    internal void Canceled() => _outcome = EasyOcrDiagnostics.Outcomes.Canceled;

    /// <summary>
    /// Marks the operation as failed and records the exception type, classifying <em>why</em> it failed.
    /// </summary>
    /// <remarks>
    /// The four outcomes are separated because they need four different responses, and collapsing them
    /// into one error rate makes the alert built on it useless. Load shedding
    /// (<see cref="OcrBusyException"/>) is the concurrency limit working as designed under a burst — it
    /// means add capacity, and it must not page anyone at 3am as a defect. A budget overrun
    /// (<see cref="OcrTimeoutException"/>) points at one pathological input to quarantine, not at broken
    /// code. A cancellation is a caller's decision. Only what is left is a genuine fault. Tagging them
    /// apart is what lets an SLO alert on <c>outcome=error</c> alone and stay quiet through a traffic
    /// spike that the service handled exactly as configured.
    /// </remarks>
    internal void Failure(Exception exception)
    {
        _outcome = exception switch
        {
            OcrTimeoutException => EasyOcrDiagnostics.Outcomes.Timeout,
            OcrBusyException => EasyOcrDiagnostics.Outcomes.Shed,
            // Checked after the two above: OcrTimeoutException is not itself an OperationCanceledException,
            // but ordering the arms this way keeps the intent obvious if that ever changes.
            OperationCanceledException => EasyOcrDiagnostics.Outcomes.Canceled,
            _ => EasyOcrDiagnostics.Outcomes.Error,
        };
        _errorType = exception.GetType().FullName;
    }

    /// <summary>Emits the measurements and stamps the span. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var elapsed = Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;
        EasyOcrDiagnostics.ActiveOperations.Add(-1, new KeyValuePair<string, object?>(EasyOcrDiagnostics.TagNames.Operation, _operation));

        var tags = new TagList
        {
            { EasyOcrDiagnostics.TagNames.Operation, _operation },
            { EasyOcrDiagnostics.TagNames.Provider, _provider },
            { EasyOcrDiagnostics.TagNames.Outcome, _outcome },
        };
        if (_errorType is not null) tags.Add(EasyOcrDiagnostics.TagNames.ErrorType, _errorType);

        EasyOcrDiagnostics.Operations.Add(1, tags);
        EasyOcrDiagnostics.Duration.Record(elapsed, tags);
        if (_lines > 0) EasyOcrDiagnostics.LinesRecognized.Add(_lines, tags);
        if (_pages > 0) EasyOcrDiagnostics.PagesProcessed.Add(_pages, tags);

        if (_activity is { } activity)
        {
            activity.SetTag(EasyOcrDiagnostics.TagNames.Operation, _operation);
            activity.SetTag(EasyOcrDiagnostics.TagNames.Provider, _provider);
            activity.SetTag(EasyOcrDiagnostics.TagNames.Outcome, _outcome);
            if (_errorType is not null) activity.SetTag(EasyOcrDiagnostics.TagNames.ErrorType, _errorType);
            // Only a genuine failure marks the span as errored: a cancelled call is a caller decision, and
            // colouring shutdown traffic red in a trace view buries the failures worth looking at.
            if (_outcome == EasyOcrDiagnostics.Outcomes.Error) activity.SetStatus(ActivityStatusCode.Error, _errorType);
            if (_languages is not null) activity.SetTag(EasyOcrDiagnostics.TagNames.Languages, _languages);
        }
    }
}
