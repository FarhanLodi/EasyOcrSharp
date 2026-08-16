using System.Diagnostics;
using EasyOcrSharp.Cli.CommandLine;
using EasyOcrSharp.Services;

namespace EasyOcrSharp.Cli.Internal;

/// <summary>
/// Draws model downloads on the terminal's progress line. Reports arrive per network read, so they
/// are throttled to a redraw every 100 ms (or on completion) — otherwise the console write would cost
/// more than the download itself on a fast link.
/// </summary>
internal sealed class DownloadProgressReporter(CliConsole console) : IProgress<ModelDownloadProgress>
{
    private readonly Stopwatch _sinceLastDraw = Stopwatch.StartNew();
    private readonly Lock _gate = new();
    private string? _currentFile;

    /// <inheritdoc />
    public void Report(ModelDownloadProgress value)
    {
        if (!console.ProgressEnabled) return;

        lock (_gate)
        {
            bool newFile = !string.Equals(_currentFile, value.FileName, StringComparison.Ordinal);
            bool complete = value.TotalBytes > 0 && value.BytesDownloaded >= value.TotalBytes;
            if (!newFile && !complete && _sinceLastDraw.ElapsedMilliseconds < 100) return;

            _currentFile = value.FileName;
            _sinceLastDraw.Restart();

            var size = value.TotalBytes > 0
                ? $"{Format(value.BytesDownloaded)} / {Format(value.TotalBytes)} ({value.Fraction * 100:F0}%)"
                : Format(value.BytesDownloaded);
            console.Progress($"downloading {value.FileName}  {size}");
        }
    }

    /// <summary>Formats a byte count the way a download manager would.</summary>
    internal static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B",
    };
}
