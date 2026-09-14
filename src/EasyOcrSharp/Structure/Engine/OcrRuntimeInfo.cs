using System.Text;
using Microsoft.ML.OnnxRuntime;
using EasyOcrSharp.Structure.Engine.Services;

namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// A snapshot answering "why is my GPU not used?" in one call: the loaded ONNX Runtime build, the
/// execution providers it actually contains, the provider journey requested → resolved → active, the
/// host GPU probe result, and — when an accelerator was requested or detected but is not running — an
/// actionable hint. <see cref="ToString"/> renders it as a readable multi-line report, so
/// <c>Console.WriteLine(info)</c> or logging the instance directly is enough.
/// <para>
/// Only one ONNX Runtime native package can be loaded per app, so <see cref="AvailableProviders"/> is
/// fixed at build time by the installed package (e.g. <c>EasyOcrSharp.Gpu</c> for CUDA,
/// <c>Microsoft.ML.OnnxRuntime.DirectML</c> for DirectML); a provider absent from that list can never
/// attach at runtime. Note that a provider append failure is otherwise silent when no
/// <see cref="Microsoft.Extensions.Logging.ILogger"/> is configured, apart from a one-time
/// process-wide warning on standard error.
/// </para>
/// </summary>
internal sealed record OcrRuntimeInfo
{
    /// <summary>
    /// Version of the loaded ONNX Runtime native library, or a value starting with "unknown" when the
    /// runtime could not be queried.
    /// </summary>
    public string OnnxRuntimeVersion { get; init; } = "unknown";

    /// <summary>
    /// Execution providers compiled into the loaded ONNX Runtime, as reported by
    /// <c>OrtEnv.GetAvailableProviders()</c> (e.g. <c>CUDAExecutionProvider</c>,
    /// <c>CPUExecutionProvider</c>). Empty when the runtime could not be queried.
    /// </summary>
    public string[] AvailableProviders { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The provider the caller configured (possibly <see cref="OcrExecutionProvider.Auto"/>).
    /// </summary>
    public OcrExecutionProvider RequestedProvider { get; init; }

    /// <summary>
    /// The concrete provider <see cref="RequestedProvider"/> resolved to at startup. This is the
    /// provider the structure engine <i>attempted</i>; it may differ from <see cref="ActiveProvider"/> when
    /// the attempt failed (e.g. a CUDA-major mismatch).
    /// </summary>
    public OcrExecutionProvider ResolvedProvider { get; init; }

    /// <summary>
    /// The provider inference is actually running on right now — <see cref="OcrExecutionProvider.Cpu"/>
    /// whenever an accelerator failed to attach or its first session failed to initialize.
    /// </summary>
    public OcrExecutionProvider ActiveProvider { get; init; }

    /// <summary>
    /// Best-effort host GPU probe summary (e.g. "NVIDIA GPU detected"). "unknown" where the probe
    /// cannot run, such as non-Windows hosts; purely advisory.
    /// </summary>
    public string GpuProbe { get; init; } = "unknown";

    /// <summary>
    /// Actionable explanation when acceleration is not active but could be — which CUDA runtime the
    /// loaded ONNX Runtime wants, which package to install or pin, or the DirectML alternative. Null
    /// when nothing needs fixing.
    /// </summary>
    public string? Hint { get; init; }

    /// <summary>
    /// Builds the snapshot by querying the loaded ONNX Runtime and the host GPU probe; the provider
    /// journey and hint are supplied by the engine that owns them. Never throws — runtime queries that
    /// fail degrade to "unknown" values.
    /// </summary>
    /// <param name="requestedProvider">The provider the caller configured (possibly Auto).</param>
    /// <param name="resolvedProvider">The concrete provider resolved at startup.</param>
    /// <param name="activeProvider">The provider inference is actually running on.</param>
    /// <param name="hint">Accelerator-failure or upgrade hint, when one exists.</param>
    public static OcrRuntimeInfo Describe(
        OcrExecutionProvider requestedProvider,
        OcrExecutionProvider resolvedProvider,
        OcrExecutionProvider activeProvider,
        string? hint = null)
    {
        string version;
        try
        {
            version = OrtEnv.Instance().GetVersionString();
        }
        catch (Exception ex)
        {
            version = $"unknown ({ex.Message})";
        }

        string[] providers;
        try
        {
            providers = OrtEnv.Instance().GetAvailableProviders();
        }
        catch
        {
            providers = Array.Empty<string>();
        }

        return new OcrRuntimeInfo
        {
            OnnxRuntimeVersion = version,
            AvailableProviders = providers,
            RequestedProvider = requestedProvider,
            ResolvedProvider = resolvedProvider,
            ActiveProvider = activeProvider,
            GpuProbe = Engine.GpuProbe.Describe(),
            Hint = hint,
        };
    }

    /// <summary>
    /// Renders the snapshot as a multi-line report suitable for printing or logging as-is.
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("EasyOcrSharp structure-engine runtime:");
        sb.AppendLine($"  ONNX Runtime:        {OnnxRuntimeVersion}");
        sb.AppendLine($"  Available providers: {(AvailableProviders.Length == 0 ? "unknown" : string.Join(", ", AvailableProviders))}");
        sb.AppendLine($"  Requested provider:  {RequestedProvider}");
        sb.AppendLine($"  Resolved provider:   {ResolvedProvider}");
        sb.AppendLine($"  Active provider:     {ActiveProvider}");
        sb.Append($"  GPU probe:           {GpuProbe}");
        if (Hint is not null)
        {
            sb.AppendLine();
            sb.Append($"  Hint:                {Hint}");
        }
        return sb.ToString();
    }
}
