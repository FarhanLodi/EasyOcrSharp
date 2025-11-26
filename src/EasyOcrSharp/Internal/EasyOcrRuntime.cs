using Microsoft.Extensions.Logging;
using System.Threading;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Coordinates runtime-wide GPU detection, downloads, and activation flags.
/// </summary>
internal static class EasyOcrRuntime
{
    private static int _initialized;

    /// <summary>
    /// Ensures the runtime detection flow has executed exactly once.
    /// </summary>
    internal static void Initialize(ILogger? logger)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        GpuRuntimeManager.Initialize(logger);
    }

    /// <summary>
    /// Registers the resolved Python home directory so GPU artifacts can be applied.
    /// </summary>
    internal static void RegisterPythonHome(string pythonHome, ILogger? logger)
    {
        GpuRuntimeManager.RegisterPythonHome(pythonHome, logger);
    }

    /// <summary>
    /// Attempts to activate the GPU runtime if it has finished downloading.
    /// Returns true when the activation has just completed (need to refresh PyTorch modules).
    /// </summary>
    internal static bool TryActivateGpuRuntime(ILogger? logger)
    {
        return GpuRuntimeManager.TryActivateGpuRuntime(logger);
    }

    /// <summary>
    /// Exposes the current runtime status flags.
    /// </summary>
    internal static RuntimeStatus CurrentStatus => GpuRuntimeManager.GetStatus();

    /// <summary>
    /// Immutable bag of runtime state flags for downstream consumers.
    /// </summary>
    /// <param name="GpuAvailable">True when a CUDA-capable GPU was detected.</param>
    /// <param name="RuntimeDownloaded">True when the GPU runtime payload has been downloaded.</param>
    /// <param name="GpuReady">True when the GPU payload has been applied to the Python runtime.</param>
    /// <param name="IsRunningOnCpu">True when the active runtime is still CPU-only.</param>
    internal readonly record struct RuntimeStatus(
        bool GpuAvailable,
        bool RuntimeDownloaded,
        bool GpuReady,
        bool IsRunningOnCpu);
}

