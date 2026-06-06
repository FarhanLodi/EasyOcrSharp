using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EasyOcrSharp.Diagnostics;

/// <summary>
/// OpenTelemetry-friendly diagnostics for EasyOcrSharp. Subscribe with the public
/// <see cref="MeterName"/> / <see cref="ActivitySourceName"/>:
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithMetrics(m => m.AddMeter(EasyOcrDiagnostics.MeterName))
///     .WithTracing(t => t.AddSource(EasyOcrDiagnostics.ActivitySourceName));
/// </code>
/// Instruments have near-zero cost when nobody is listening, so they are always on.
/// </summary>
public static class EasyOcrDiagnostics
{
    /// <summary>Meter name to register with your metrics pipeline.</summary>
    public const string MeterName = "EasyOcrSharp";

    /// <summary>ActivitySource name to register with your tracing pipeline.</summary>
    public const string ActivitySourceName = "EasyOcrSharp";

    private const string Version = "2.2.1";

    /// <summary>Activity source for per-operation OCR spans.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);

    internal static readonly Meter Meter = new(MeterName, Version);

    internal static readonly Counter<long> Operations =
        Meter.CreateCounter<long>("easyocr.operations", unit: "{operation}", description: "OCR operations performed.");

    internal static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("easyocr.duration", unit: "ms", description: "OCR operation wall-clock duration.");

    internal static readonly Counter<long> LinesRecognized =
        Meter.CreateCounter<long>("easyocr.lines", unit: "{line}", description: "Text lines returned by recognition.");

    internal static readonly Counter<long> ModelLoads =
        Meter.CreateCounter<long>("easyocr.model.loads", unit: "{model}", description: "ONNX model sessions created.");

    internal static readonly Counter<long> ModelDownloadBytes =
        Meter.CreateCounter<long>("easyocr.model.download_bytes", unit: "By", description: "Bytes downloaded for model assets.");
}
