namespace EasyOcrSharp.Cli.CommandLine;

/// <summary>
/// The exit codes the tool returns. They are part of its contract: a build script or a cron job
/// branches on these, so they must stay stable.
/// </summary>
internal static class ExitCodes
{
    /// <summary>Everything the command was asked to do succeeded.</summary>
    public const int Success = 0;

    /// <summary>The command ran but at least one input or step failed.</summary>
    public const int Failure = 1;

    /// <summary>The command line was invalid — unknown option, missing argument, bad value.</summary>
    public const int Usage = 2;

    /// <summary>Interrupted by Ctrl-C (128 + SIGINT, the shell convention).</summary>
    public const int Interrupted = 130;
}
