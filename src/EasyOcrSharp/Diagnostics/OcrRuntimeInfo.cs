using System.Text;
using EasyOcrSharp.Services;
using Microsoft.ML.OnnxRuntime;

namespace EasyOcrSharp.Diagnostics;

/// <summary>
/// A snapshot answering "why is my GPU not used?" in one call: the loaded ONNX Runtime build, the
/// execution providers it actually contains, the provider journey requested → resolved → active, the
/// host GPU probe result, and — when an accelerator was requested or detected but is not running — an
/// actionable hint. <see cref="ToString"/> renders it as a readable multi-line report, so
/// <c>Console.WriteLine(info)</c> or logging the instance directly is enough.
/// Obtain one from <see cref="EasyOcrService.GetRuntimeInfo"/>.
/// <para>
/// Only one ONNX Runtime native package can be loaded per app, so <see cref="AvailableProviders"/> is
/// fixed at build time by the installed package (<c>EasyOcrSharp.Gpu</c> for CUDA); a provider absent
/// from that list can never attach at runtime.
/// </para>
/// </summary>
public sealed record OcrRuntimeInfo
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
    public IReadOnlyList<string> AvailableProviders { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The provider the service was configured with (possibly <see cref="OcrExecutionProvider.Auto"/>;
    /// the legacy <c>UseGpu</c> flag shows up here as <see cref="OcrExecutionProvider.Cuda"/>).
    /// </summary>
    public OcrExecutionProvider RequestedProvider { get; init; }

    /// <summary>
    /// The concrete provider <see cref="RequestedProvider"/> resolved to at startup — the provider
    /// EasyOcrSharp <i>attempted</i>. It differs from <see cref="ActiveProvider"/> when that attempt failed.
    /// </summary>
    public OcrExecutionProvider ResolvedProvider { get; init; }

    /// <summary>
    /// The provider inference is actually running on — <see cref="OcrExecutionProvider.Cpu"/> whenever an
    /// accelerator failed to attach or its first session failed to initialize.
    /// </summary>
    public OcrExecutionProvider ActiveProvider { get; init; }

    /// <summary>
    /// Best-effort host GPU probe summary (e.g. "NVIDIA GPU detected"); "unknown" where the probe cannot
    /// run, such as non-Windows hosts. Purely advisory.
    /// </summary>
    public string GpuProbe { get; init; } = "unknown";

    /// <summary>
    /// The same text as <see cref="EasyOcrService.GpuAccelerationHint"/>: why acceleration is not active
    /// and how to fix it. Null when nothing needs fixing.
    /// </summary>
    public string? Hint { get; init; }

    /// <summary>
    /// Builds the snapshot by querying the loaded ONNX Runtime and the host GPU probe; the provider journey
    /// and hint come from the engine that owns them. Never throws.
    /// </summary>
    internal static OcrRuntimeInfo Describe(
        OcrExecutionProvider requestedProvider,
        OcrExecutionProvider resolvedProvider,
        OcrExecutionProvider activeProvider,
        string? hint)
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

        IReadOnlyList<string> providers;
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
            GpuProbe = Internal.GpuProbe.Describe(),
            Hint = hint,
        };
    }

    /// <summary>Renders the snapshot as a multi-line report suitable for printing or logging as-is.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("EasyOcrSharp runtime:");
        sb.AppendLine($"  ONNX Runtime:        {OnnxRuntimeVersion}");
        sb.AppendLine($"  Available providers: {(AvailableProviders.Count == 0 ? "unknown" : string.Join(", ", AvailableProviders))}");
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
