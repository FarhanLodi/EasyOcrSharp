using System.Text;

namespace EasyOcrSharp.Cli.CommandLine;

/// <summary>
/// The static description of a command: what it is called, what it does, the options it accepts and
/// how its help text reads. Keeping this declarative means <c>--help</c> is generated from the same
/// data the parser validates against, so they can never drift apart.
/// </summary>
/// <param name="Name">Command name as typed (e.g. <c>scan</c>, <c>models pull</c>).</param>
/// <param name="Summary">One or two sentences describing the command.</param>
/// <param name="Usage">The usage line, without the executable name.</param>
/// <param name="Options">Options accepted in addition to the implicit <c>--help</c>.</param>
internal sealed record CliCommand(
    string Name,
    string Summary,
    string Usage,
    IReadOnlyList<CliOption> Options)
{
    /// <summary>The name every command answers to for help.</summary>
    public static readonly CliOption HelpOption = new("help", "-h", false, "Show this help text and exit.");

    /// <summary>Optional example invocations appended to the help text.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];

    /// <summary>Optional extra paragraph printed under the options table.</summary>
    public string? Remarks { get; init; }

    /// <summary>The declared options plus the implicit <c>--help</c>.</summary>
    public IReadOnlyList<CliOption> AllOptions { get; } = [.. Options, HelpOption];

    /// <summary>Renders the full help page for this command.</summary>
    public string HelpText()
    {
        var sb = new StringBuilder();
        sb.Append("Usage:").Append('\n');
        sb.Append("  ").Append(CliMetadata.ExecutableName).Append(' ').Append(Usage).Append('\n').Append('\n');
        sb.Append(Summary).Append('\n');

        if (AllOptions.Count > 0)
        {
            sb.Append('\n').Append("Options:").Append('\n');
            int width = AllOptions.Max(o => o.HelpSyntax.Length);
            foreach (var option in AllOptions)
            {
                sb.Append("  ").Append(option.HelpSyntax.PadRight(width)).Append("  ").Append(option.Help).Append('\n');
            }
        }

        if (!string.IsNullOrWhiteSpace(Remarks))
        {
            sb.Append('\n').Append(Remarks).Append('\n');
        }

        if (Examples.Count > 0)
        {
            sb.Append('\n').Append("Examples:").Append('\n');
            foreach (var example in Examples)
            {
                sb.Append("  ").Append(example).Append('\n');
            }
        }

        return sb.ToString();
    }
}
