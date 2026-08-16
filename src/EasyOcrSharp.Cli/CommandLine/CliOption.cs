namespace EasyOcrSharp.Cli.CommandLine;

/// <summary>
/// One option accepted by a command. Options are always spelled <c>--name</c>; an optional
/// single-dash <see cref="Alias"/> (e.g. <c>-l</c>) may be given as a shorthand.
/// </summary>
/// <param name="Name">The long name, without the leading <c>--</c>.</param>
/// <param name="Alias">Optional short form including its leading dash (e.g. <c>-l</c>), or null.</param>
/// <param name="TakesValue">True for <c>--name value</c> / <c>--name=value</c>; false for a bare flag.</param>
/// <param name="Help">One-line description shown by <c>--help</c>.</param>
/// <param name="ValuePlaceholder">Placeholder rendered in help for a value-taking option.</param>
/// <param name="Repeatable">True when the option may be given more than once and every value is kept.</param>
internal sealed record CliOption(
    string Name,
    string? Alias,
    bool TakesValue,
    string Help,
    string ValuePlaceholder = "value",
    bool Repeatable = false)
{
    /// <summary>The option as it is typed, e.g. <c>--lang</c>.</summary>
    public string LongForm => "--" + Name;

    /// <summary>Renders the left-hand column of the help table, e.g. <c>-l, --lang &lt;codes&gt;</c>.</summary>
    public string HelpSyntax
    {
        get
        {
            var head = Alias is null ? $"    {LongForm}" : $"{Alias}, {LongForm}";
            return TakesValue ? $"{head} <{ValuePlaceholder}>" : head;
        }
    }
}
