using System.Reflection;
using EasyOcrSharp.Services;

namespace EasyOcrSharp.Cli.CommandLine;

/// <summary>
/// Identity of the tool itself: the command name users type and the versions reported by
/// <c>--version</c> and <c>info</c>. Versions are read from the assemblies so they can never drift
/// from what was actually shipped.
/// </summary>
internal static class CliMetadata
{
    /// <summary>The command name the tool is installed as (see <c>ToolCommandName</c>).</summary>
    public const string ExecutableName = "easyocrsharp";

    /// <summary>Version of this CLI package.</summary>
    public static string CliVersion { get; } = VersionOf(typeof(CliMetadata).Assembly);

    /// <summary>Version of the EasyOcrSharp library the CLI is built against.</summary>
    public static string LibraryVersion { get; } = VersionOf(typeof(EasyOcrService).Assembly);

    /// <summary>
    /// Reads an assembly's informational version, trimming the <c>+sourcerevision</c> suffix the SDK
    /// appends when <c>SourceLink</c> is enabled. Falls back to the assembly version.
    /// </summary>
    private static string VersionOf(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
