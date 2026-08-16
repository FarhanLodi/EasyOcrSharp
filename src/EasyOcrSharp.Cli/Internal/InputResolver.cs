using System.Collections.Frozen;
using EasyOcrSharp.Cli.CommandLine;

namespace EasyOcrSharp.Cli.Internal;

/// <summary>
/// Expands the positional arguments of <c>scan</c> into a concrete, de-duplicated file list. A token
/// may be a file, a directory (scanned for supported types), or a shell-style glob — Windows shells
/// do not expand globs for us, so the tool does it itself and behaves the same on every platform.
/// </summary>
internal static class InputResolver
{
    /// <summary>File types the tool will attempt: everything ImageSharp decodes, plus PDF.</summary>
    private static readonly FrozenSet<string> SupportedExtensions = new[]
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff",
        ".webp", ".tga", ".pbm", ".pgm", ".ppm", ".qoi", ".pdf",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Path comparison follows the platform: Windows paths are case-insensitive, POSIX ones are not.
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>True when the path names a PDF, which takes the page-by-page route instead of the image one.</summary>
    public static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves input tokens to absolute file paths, preserving the order they were given and
    /// dropping duplicates. Directory and glob expansions are sorted so runs are reproducible.
    /// </summary>
    /// <param name="tokens">Raw positional arguments.</param>
    /// <param name="recursive">Descend into sub-directories for directory and glob inputs.</param>
    /// <exception cref="CliUsageException">A token matches nothing on disk.</exception>
    public static IReadOnlyList<string> Resolve(IReadOnlyList<string> tokens, bool recursive)
    {
        var seen = new HashSet<string>(PathComparer);
        var result = new List<string>();

        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token)) continue;

            var matches = ExpandToken(token, recursive);
            if (matches.Count == 0)
            {
                throw new CliUsageException(
                    $"'{token}' matched no readable file. Pass an image, a PDF, a folder, or a glob such as 'scans/*.png'.");
            }

            foreach (var match in matches)
            {
                if (seen.Add(match)) result.Add(match);
            }
        }

        return result;
    }

    private static List<string> ExpandToken(string token, bool recursive)
    {
        if (token.Contains('*') || token.Contains('?'))
        {
            var directory = Path.GetDirectoryName(token);
            var pattern = Path.GetFileName(token);
            if (string.IsNullOrEmpty(pattern))
                throw new CliUsageException($"'{token}' is not a usable glob: the wildcard must be in the file name.");
            if (directory is not null && (directory.Contains('*') || directory.Contains('?')))
                throw new CliUsageException($"'{token}' is not supported: wildcards are only allowed in the file name. Use --recursive to descend into sub-folders.");

            var root = string.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : Path.GetFullPath(directory);
            if (!Directory.Exists(root))
                throw new CliUsageException($"Directory '{root}' does not exist.");

            return EnumerateSupported(root, pattern, recursive);
        }

        if (Directory.Exists(token))
        {
            return EnumerateSupported(Path.GetFullPath(token), "*", recursive);
        }

        if (File.Exists(token))
        {
            return [Path.GetFullPath(token)];
        }

        return [];
    }

    /// <summary>Collects the supported files under <paramref name="root"/>, sorted for reproducibility.</summary>
    private static List<string> EnumerateSupported(string root, string pattern, bool recursive)
    {
        var matches = new List<string>();
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                // A folder the user cannot read shouldn't abort a 10,000-file batch.
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.PlatformDefault,
            };

            foreach (var file in Directory.EnumerateFiles(root, pattern, options))
            {
                if (SupportedExtensions.Contains(Path.GetExtension(file)))
                {
                    matches.Add(Path.GetFullPath(file));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliException($"Could not read '{root}': {ex.Message}", ex);
        }

        matches.Sort(PathComparer);
        return matches;
    }
}
