using System.Diagnostics;
using System.Diagnostics.Metrics;
using EasyOcrSharp.Diagnostics;

namespace EasyOcrSharp.WebApi.Internal;

/// <summary>
/// Subscribes to the library's meter and activity source and writes what they publish to
/// <see cref="ILogger"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is a dependency-free stand-in for a real OpenTelemetry pipeline. It listens to exactly the
/// names a production app would register — <see cref="EasyOcrDiagnostics.MeterName"/> and
/// <see cref="EasyOcrDiagnostics.ActivitySourceName"/> — so you can see the instrumentation working
/// before you decide on a backend, then delete this file and uncomment the OpenTelemetry block in
/// <c>Program.cs</c>.
/// </para>
/// <para>
/// Off by default (<c>EasyOcr:LogTelemetry</c>): a log line per measurement is fine for a demo and
/// wrong for production, where metrics belong in a metrics store rather than in the log stream.
/// </para>
/// </remarks>
internal sealed class EasyOcrTelemetryLogger : IHostedService, IDisposable
{
    private readonly ILogger<EasyOcrTelemetryLogger> _logger;
    private MeterListener? _meterListener;
    private ActivityListener? _activityListener;

    /// <summary>Creates the listener.</summary>
    public EasyOcrTelemetryLogger(ILogger<EasyOcrTelemetryLogger> logger) => _logger = logger;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == EasyOcrDiagnostics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>(OnLongMeasurement);
        meterListener.SetMeasurementEventCallback<double>(OnDoubleMeasurement);
        meterListener.Start();
        _meterListener = meterListener;

        var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == EasyOcrDiagnostics.ActivitySourceName,
            Sample = SampleAllData,
            SampleUsingParentId = SampleAllDataByParentId,
            ActivityStopped = OnActivityStopped,
        };
        ActivitySource.AddActivityListener(activityListener);
        _activityListener = activityListener;

        _logger.LogInformation(
            "Listening to meter '{Meter}' and activity source '{Source}'.",
            EasyOcrDiagnostics.MeterName,
            EasyOcrDiagnostics.ActivitySourceName);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _meterListener?.Dispose();
        _meterListener = null;
        _activityListener?.Dispose();
        _activityListener = null;
    }

    private static ActivitySamplingResult SampleAllData(ref ActivityCreationOptions<ActivityContext> options)
        => ActivitySamplingResult.AllDataAndRecorded;

    private static ActivitySamplingResult SampleAllDataByParentId(ref ActivityCreationOptions<string> options)
        => ActivitySamplingResult.AllDataAndRecorded;

    private void OnActivityStopped(Activity activity) => _logger.LogInformation(
        "ocr span {Operation} took {DurationMs:F1} ms ({Status}).",
        activity.OperationName,
        activity.Duration.TotalMilliseconds,
        activity.Status);

    private void OnLongMeasurement(
        Instrument instrument,
        long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
        => Log(instrument, measurement, tags);

    private void OnDoubleMeasurement(
        Instrument instrument,
        double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
        => Log(instrument, measurement, tags);

    /// <summary>
    /// Formats the tag span before logging. The span cannot cross the async/boxing boundary a logger
    /// call implies, so it is flattened here.
    /// </summary>
    private void Log(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var rendered = tags.IsEmpty ? string.Empty : Render(tags);
        _logger.LogInformation("{Instrument} {Measurement} {Unit} {Tags}",
            instrument.Name, measurement, instrument.Unit ?? string.Empty, rendered);
    }

    private static string Render(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var parts = new string[tags.Length];
        for (var i = 0; i < tags.Length; i++)
        {
            parts[i] = $"{tags[i].Key}={tags[i].Value}";
        }

        return string.Join(", ", parts);
    }
}
