using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// OpenTelemetry-friendly diagnostics for the structure engine. Subscribe with the public
/// <see cref="MeterName"/> / <see cref="ActivitySourceName"/>:
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithMetrics(m => m.AddMeter(StructureDiagnostics.MeterName))
///     .WithTracing(t => t.AddSource(StructureDiagnostics.ActivitySourceName));
/// </code>
/// Instruments have near-zero cost when nobody is listening, so they are always on.
/// </summary>
internal static class StructureDiagnostics
{
    /// <summary>
    /// Meter name to register with your metrics pipeline.
    /// </summary>
    public const string MeterName = "EasyOcrSharp.Structure";

    /// <summary>
    /// ActivitySource name to register with your tracing pipeline.
    /// </summary>
    public const string ActivitySourceName = "EasyOcrSharp.Structure";

    /// <summary>
    /// The library version reported on the activity source and meter: the assembly's informational version
    /// without the <c>+commit</c> source-link suffix.
    /// </summary>
    internal static readonly string Version = ResolveVersion(
        typeof(StructureDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        typeof(StructureDiagnostics).Assembly.GetName().Version);

    /// <summary>
    /// Activity source for per-operation OCR spans.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);

    internal static readonly Meter Meter = new(MeterName, Version);

    /// <summary>
    /// Strips build metadata (<c>+sha</c>) from an informational version, falling back to the assembly
    /// version when no informational version is present.
    /// </summary>
    internal static string ResolveVersion(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            int plus = informationalVersion.IndexOf('+');
            var trimmed = (plus >= 0 ? informationalVersion[..plus] : informationalVersion).Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return assemblyVersion?.ToString(3) ?? "0.0.0";
    }

    internal static readonly Counter<long> Operations =
        Meter.CreateCounter<long>("paddleocr.operations", unit: "{operation}", description: "OCR operations performed.");

    internal static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("paddleocr.duration", unit: "ms", description: "OCR operation wall-clock duration.");

    internal static readonly Counter<long> LinesRecognized =
        Meter.CreateCounter<long>("paddleocr.lines", unit: "{line}", description: "Text lines returned by recognition.");

    internal static readonly Counter<long> ModelLoads =
        Meter.CreateCounter<long>("paddleocr.model.loads", unit: "{model}", description: "ONNX model sessions created.");

    internal static readonly Counter<long> ModelDownloadBytes =
        Meter.CreateCounter<long>("paddleocr.model.download_bytes", unit: "By", description: "Bytes downloaded for model assets.");

    internal static readonly Histogram<double> StageDuration =
        Meter.CreateHistogram<double>("paddleocr.stage.duration", unit: "ms", description: "Stage wall-clock duration, including failed stages.");

    internal static readonly Histogram<long> RecognitionTensorBytes =
        Meter.CreateHistogram<long>("paddleocr.recognition.tensor_bytes", unit: "By", description: "Input tensor bytes per recognition batch; excludes output and native allocations.");

    internal static readonly Histogram<double> RecognitionPadding =
        Meter.CreateHistogram<double>("paddleocr.recognition.padding_fraction", description: "Fraction of recognition input pixels used for right padding.");

    // Stage names are library constants, never document content or paths.
    internal static StageScope MeasureStage(string stage) => new(stage);

    internal readonly struct StageScope : IDisposable
    {
        private readonly string _stage;
        private readonly long _started;
        private readonly Activity? _activity;
        internal StageScope(string stage)
        {
            _stage = stage;
            _started = StageDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
            _activity = ActivitySource.HasListeners() ? ActivitySource.StartActivity("paddleocr.stage." + stage) : null;
        }

        public void Dispose()
        {
            if (_started != 0)
                StageDuration.Record(Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                    new KeyValuePair<string, object?>("stage", _stage));
            _activity?.Dispose();
        }
    }
}
