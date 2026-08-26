using System.Diagnostics;
using System.Diagnostics.Metrics;

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

    private const string Version = "1.0.0";

    /// <summary>
    /// Activity source for per-operation OCR spans.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);

    internal static readonly Meter Meter = new(MeterName, Version);

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
}
