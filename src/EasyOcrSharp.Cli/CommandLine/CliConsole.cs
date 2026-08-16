namespace EasyOcrSharp.Cli.CommandLine;

/// <summary>
/// The tool's output policy in one place: <b>results go to stdout, everything else to stderr</b>, so
/// <c>easyocrsharp scan page.png | jq</c> works without a single extra flag. The transient progress
/// line is only drawn on a real terminal — when stdout or stderr is redirected (a pipe, a file, a CI
/// log) it would just pollute the capture, so it is suppressed, as is everything else under
/// <c>--quiet</c>.
/// </summary>
internal sealed class CliConsole(bool quiet)
{
    private int _progressWidth;

    /// <summary>True when informational chatter and progress are suppressed.</summary>
    public bool Quiet { get; } = quiet;

    /// <summary>
    /// Whether a live progress line can be drawn: not quiet, and neither stream is redirected.
    /// </summary>
    public bool ProgressEnabled { get; } = !quiet && !Console.IsOutputRedirected && !Console.IsErrorRedirected;

    /// <summary>Writes a result line to stdout (the pipeline's payload).</summary>
    public void Write(string text) => Console.Out.Write(text);

    /// <summary>Writes a result line to stdout, followed by a newline.</summary>
    public void WriteLine(string text = "") => Console.Out.WriteLine(text);

    /// <summary>Writes an informational message to stderr unless <c>--quiet</c> was given.</summary>
    public void Info(string message)
    {
        if (Quiet) return;
        ClearProgress();
        Console.Error.WriteLine(message);
    }

    /// <summary>Writes a warning to stderr unless <c>--quiet</c> was given.</summary>
    public void Warn(string message) => Info("warning: " + message);

    /// <summary>Writes an error to stderr. Never suppressed — a failure must always be visible.</summary>
    public void Error(string message)
    {
        ClearProgress();
        Console.Error.WriteLine("error: " + message);
    }

    /// <summary>
    /// Redraws the single-line progress indicator in place. A no-op when progress is disabled, so
    /// callers never need to check.
    /// </summary>
    public void Progress(string message)
    {
        if (!ProgressEnabled) return;

        // Pad to the previous width so a shorter line doesn't leave the old tail behind.
        int pad = Math.Max(0, _progressWidth - message.Length);
        Console.Error.Write('\r');
        Console.Error.Write(message);
        if (pad > 0) Console.Error.Write(new string(' ', pad));
        _progressWidth = message.Length;
    }

    /// <summary>Erases the progress line, if one is currently drawn.</summary>
    public void ClearProgress()
    {
        if (_progressWidth == 0) return;
        Console.Error.Write('\r');
        Console.Error.Write(new string(' ', _progressWidth));
        Console.Error.Write('\r');
        _progressWidth = 0;
    }
}
