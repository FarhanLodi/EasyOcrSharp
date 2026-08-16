using System.Globalization;

namespace EasyOcrSharp.Cli.CommandLine;

/// <summary>
/// The parsed command line for one command: everything that was not an option is a positional, and
/// options are looked up by their long name (never by alias — the alias is resolved during parsing).
/// </summary>
internal sealed class ParsedArgs
{
    private readonly CliCommand _command;
    private readonly Dictionary<string, List<string>> _values;
    private readonly HashSet<string> _flags;

    internal ParsedArgs(
        CliCommand command,
        IReadOnlyList<string> positionals,
        Dictionary<string, List<string>> values,
        HashSet<string> flags)
    {
        _command = command;
        _values = values;
        _flags = flags;
        Positionals = positionals;
    }

    /// <summary>Free-standing arguments in the order they were typed (file paths, language lists, …).</summary>
    public IReadOnlyList<string> Positionals { get; }

    /// <summary>The command these arguments were parsed for.</summary>
    public CliCommand Command => _command;

    /// <summary>True when the bare flag <c>--<paramref name="name"/></c> was present.</summary>
    public bool Flag(string name) => _flags.Contains(name);

    /// <summary>The last value given for an option, or null when the option was not used.</summary>
    public string? Value(string name)
        => _values.TryGetValue(name, out var list) && list.Count > 0 ? list[^1] : null;

    /// <summary>Every value given for a repeatable option, in order. Empty when unused.</summary>
    public IReadOnlyList<string> Values(string name)
        => _values.TryGetValue(name, out var list) ? list : [];

    /// <summary>
    /// All values of an option flattened across repetitions and comma/semicolon separation, trimmed
    /// and with empties removed — so <c>-l en,fr -l de</c> and <c>-l "en, fr, de"</c> are equivalent.
    /// </summary>
    public IReadOnlyList<string> List(string name)
    {
        var result = new List<string>();
        foreach (var raw in Values(name))
        {
            foreach (var part in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                result.Add(part);
            }
        }
        return result;
    }

    /// <summary>The option's value parsed as an integer, or null when the option was not used.</summary>
    public int? Int(string name)
    {
        if (Value(name) is not { } raw) return null;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new CliUsageException($"--{name} expects a whole number, but got '{raw}'.", _command);
        return value;
    }

    /// <summary>The option's value parsed as a double, or null when the option was not used.</summary>
    public double? Double(string name)
    {
        if (Value(name) is not { } raw) return null;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new CliUsageException($"--{name} expects a number, but got '{raw}'.", _command);
        return value;
    }

    /// <summary>Throws a usage error attributed to this command.</summary>
    public CliUsageException Fail(string message) => new(message, _command);
}

/// <summary>
/// A dependency-free command-line parser. It understands <c>--name value</c>, <c>--name=value</c>,
/// short aliases (<c>-l en</c>, <c>-l=en</c>), bare flags, and <c>--</c> to stop option parsing so a
/// file literally named <c>--weird.png</c> can still be scanned. Unknown options are rejected rather
/// than silently ignored, because a typo'd option in a batch job should fail loudly.
/// </summary>
internal static class ArgParser
{
    /// <summary>Parses <paramref name="args"/> against a command's declared options.</summary>
    /// <exception cref="CliUsageException">An option is unknown, misspelled, or missing its value.</exception>
    public static ParsedArgs Parse(CliCommand command, IReadOnlyList<string> args)
    {
        var byName = new Dictionary<string, CliOption>(StringComparer.Ordinal);
        var byAlias = new Dictionary<string, CliOption>(StringComparer.Ordinal);
        foreach (var option in command.AllOptions)
        {
            byName[option.Name] = option;
            if (option.Alias is { } alias) byAlias[alias] = option;
        }

        var positionals = new List<string>();
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        bool optionsEnded = false;

        for (int i = 0; i < args.Count; i++)
        {
            var token = args[i];

            if (optionsEnded || token.Length == 0 || token == "-")
            {
                positionals.Add(token);
                continue;
            }

            if (token == "--")
            {
                optionsEnded = true;
                continue;
            }

            CliOption option;
            string? inlineValue = null;

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var body = token[2..];
                int eq = body.IndexOf('=');
                if (eq >= 0)
                {
                    inlineValue = body[(eq + 1)..];
                    body = body[..eq];
                }

                if (!byName.TryGetValue(body, out var found))
                    throw new CliUsageException($"Unknown option '--{body}'.{Suggest(body, byName.Keys)}", command);
                option = found;
            }
            else if (token[0] == '-')
            {
                var body = token;
                int eq = body.IndexOf('=');
                if (eq >= 0)
                {
                    inlineValue = body[(eq + 1)..];
                    body = body[..eq];
                }

                if (!byAlias.TryGetValue(body, out var found))
                    throw new CliUsageException($"Unknown option '{body}'.", command);
                option = found;
            }
            else
            {
                positionals.Add(token);
                continue;
            }

            if (!option.TakesValue)
            {
                if (inlineValue is not null)
                    throw new CliUsageException($"--{option.Name} is a flag and does not take a value.", command);
                flags.Add(option.Name);
                continue;
            }

            if (inlineValue is null)
            {
                if (i + 1 >= args.Count)
                    throw new CliUsageException($"--{option.Name} requires a value.", command);
                inlineValue = args[++i];
            }

            if (!values.TryGetValue(option.Name, out var list))
            {
                values[option.Name] = list = [];
            }
            else if (!option.Repeatable)
            {
                // Last one wins, but say so — a repeated non-repeatable option is usually a mistake.
                list.Clear();
            }
            list.Add(inlineValue);
        }

        return new ParsedArgs(command, positionals, values, flags);
    }

    /// <summary>
    /// Offers the closest known option name when the user mistypes one — a one-edit-distance check is
    /// enough to catch the overwhelmingly common slips (<c>--lang</c> vs <c>--langs</c>).
    /// </summary>
    private static string Suggest(string typed, IEnumerable<string> known)
    {
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (var candidate in known)
        {
            int distance = Distance(typed, candidate, bestDistance);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best is not null && bestDistance <= 2 ? $" Did you mean '--{best}'?" : "";
    }

    /// <summary>Levenshtein distance, abandoned early once it can no longer beat <paramref name="limit"/>.</summary>
    private static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) >= limit) return int.MaxValue;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            int rowMin = current[0];
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                if (current[j] < rowMin) rowMin = current[j];
            }
            if (rowMin >= limit) return int.MaxValue;
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
