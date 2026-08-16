namespace EasyOcrSharp.Cli.CommandLine;

/// <summary>
/// The command line itself was wrong (unknown option, missing argument, bad value). Reported to
/// stderr together with the offending command's usage, and mapped to exit code 2.
/// </summary>
internal sealed class CliUsageException : Exception
{
    /// <summary>Creates a usage error for the given command (null for a top-level error).</summary>
    public CliUsageException(string message, CliCommand? command = null)
        : base(message)
    {
        Command = command;
    }

    /// <summary>The command whose usage should be printed alongside the message, if known.</summary>
    public CliCommand? Command { get; }
}

/// <summary>
/// A command failed while doing its work (unreadable file, download failure, …). The message is the
/// user-facing explanation; mapped to exit code 1.
/// </summary>
internal sealed class CliException(string message, Exception? innerException = null)
    : Exception(message, innerException);
