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

    // Kept in step with <Version> in EasyOcrSharp.csproj: a stale value here silently mislabels every
    // metric and span the library emits, so a 3.x deployment reported itself as 2.2.1.
    private const string Version = "3.1.0";

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

    internal static readonly Counter<long> PagesProcessed =
        Meter.CreateCounter<long>("easyocr.pages", unit: "{page}", description: "Pages / documents processed.");

    // Saturation instruments. Without these, a service that has just been given a concurrency limit is
    // configured blind: a rising queue wait is the signal that MaxConcurrentOperations is set too low,
    // and it is invisible in latency alone because a shed request is fast, not slow.
    internal static readonly Histogram<double> QueueWait =
        Meter.CreateHistogram<double>("easyocr.queue.wait", unit: "ms", description: "Time an operation waited for a concurrency slot.");

    internal static readonly UpDownCounter<long> ActiveOperations =
        Meter.CreateUpDownCounter<long>("easyocr.operations.active", unit: "{operation}", description: "Operations currently executing inside the engine.");

    internal static readonly UpDownCounter<long> QueuedOperations =
        Meter.CreateUpDownCounter<long>("easyocr.operations.queued", unit: "{operation}", description: "Operations currently waiting for a concurrency slot.");

    /// <summary>
    /// Tag keys carried by every operation measurement and by the matching span, so a metric and a trace
    /// can be filtered the same way. <see cref="ErrorType"/> keeps the OpenTelemetry semantic-convention
    /// name rather than a library-specific one, so existing error dashboards pick it up unchanged.
    /// </summary>
    public static class TagNames
    {
        /// <summary>Which entry point ran — one of <see cref="OperationNames"/>.</summary>
        public const string Operation = "easyocr.operation";

        /// <summary>The resolved ONNX execution provider (Cpu, Cuda, DirectMl, CoreMl).</summary>
        public const string Provider = "easyocr.provider";

        /// <summary>How the operation ended — one of <see cref="Outcomes"/>.</summary>
        public const string Outcome = "easyocr.outcome";

        /// <summary>Full CLR type name of the exception that ended the operation. Absent on success.</summary>
        public const string ErrorType = "error.type";

        /// <summary>Comma-separated language codes the operation ran with.</summary>
        public const string Languages = "easyocr.languages";
    }

    /// <summary>Values of the <see cref="TagNames.Outcome"/> tag.</summary>
    public static class Outcomes
    {
        /// <summary>The operation completed normally.</summary>
        public const string Success = "success";

        /// <summary>The operation threw — see <see cref="TagNames.ErrorType"/>.</summary>
        public const string Error = "error";

        /// <summary>The caller cancelled, or abandoned a streamed enumeration part-way through.</summary>
        public const string Canceled = "canceled";

        /// <summary>
        /// The operation exceeded <c>EasyOcrServiceOptions.OperationTimeout</c> and was abandoned.
        /// </summary>
        public const string Timeout = "timeout";

        /// <summary>
        /// Load shed: the service was at <c>EasyOcrServiceOptions.MaxConcurrentOperations</c> and no slot
        /// freed up within <c>QueueTimeout</c>.
        /// </summary>
        public const string Shed = "shed";
    }

    /// <summary>
    /// Values of the <see cref="TagNames.Operation"/> tag — one per public entry point, so a 40 ms region
    /// recognition and a 4 s document analysis are not averaged into one meaningless latency figure.
    /// </summary>
    public static class OperationNames
    {
        /// <summary>Buffered image OCR — every <c>ExtractTextFromImage</c> overload.</summary>
        public const string Extract = "extract";

        /// <summary>Streaming image OCR — the <c>ExtractTextStreamAsync</c> family.</summary>
        public const string ExtractStream = "extract_stream";

        /// <summary>Text-region detection without recognition — <c>DetectRegionsAsync</c>.</summary>
        public const string Detect = "detect";

        /// <summary>Script/language auto-detection — <c>DetectLanguagesAsync</c>.</summary>
        public const string DetectLanguages = "detect_languages";

        /// <summary>Recognition of caller-supplied regions — <c>RecognizeRegionsAsync</c>.</summary>
        public const string Recognize = "recognize";

        /// <summary>Handwriting recognition — <c>RecognizeHandwritingAsync</c>.</summary>
        public const string Handwriting = "handwriting";

        /// <summary>Document-structure analysis — <c>AnalyzeDocumentAsync</c>.</summary>
        public const string AnalyzeDocument = "analyze_document";

        /// <summary>Model warm-up — <c>WarmUp</c>.</summary>
        public const string WarmUp = "warmup";

        /// <summary>PDF OCR — the <c>ExtractTextFromPdfAsync</c> family.</summary>
        public const string Pdf = "pdf";

        /// <summary>Searchable-PDF generation — <c>CreateSearchablePdfAsync</c>.</summary>
        public const string PdfSearchable = "pdf_searchable";

        /// <summary>Multi-frame (TIFF) OCR — the <c>ExtractTextFromFramesAsync</c> family.</summary>
        public const string MultiFrame = "multi_frame";

        /// <summary>Batch OCR over many images — <c>ExtractTextFromImagesAsync</c>.</summary>
        public const string Batch = "batch";
    }

    /// <summary>
    /// Starts recording one logical operation. Dispose the returned recorder to emit the measurements —
    /// which happens even when the operation throws, because an unrecorded failure is how a broken
    /// service comes to look like an idle one.
    /// </summary>
    internal static OcrOperationRecorder Begin(string operation, string provider) => new(operation, provider);
}
