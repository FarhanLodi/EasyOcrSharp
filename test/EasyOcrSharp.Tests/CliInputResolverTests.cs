using EasyOcrSharp.Cli.CommandLine;
using EasyOcrSharp.Cli.Internal;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Unit tests for input expansion (files, folders, globs) and model-cache location. These touch a
/// throwaway temp directory rather than the user's disk, and never the network.
/// </summary>
public sealed class CliInputResolverTests : IDisposable
{
    private readonly string _root;

    public CliInputResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "easyocr-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A locked file must never fail the test run; the temp folder is disposable either way.
        }
    }

    /// <summary>Creates an empty file (and any parent folders) under the test root.</summary>
    private string Touch(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, []);
        return full;
    }

    private static IReadOnlyList<string> Resolve(bool recursive, params string[] tokens)
        => InputResolver.Resolve(tokens, recursive);

    // ---------------------------------------------------------------- single files

    [Fact]
    public void A_file_resolves_to_its_absolute_path()
    {
        var file = Touch("page.png");

        var resolved = Resolve(false, file);

        Assert.Equal(new[] { Path.GetFullPath(file) }, resolved);
    }

    [Fact]
    public void An_explicit_file_is_taken_even_when_its_extension_is_unusual()
    {
        // Directory scans filter by extension; naming a file explicitly is an instruction, not a guess.
        var file = Touch("scan.weird");

        Assert.Equal(new[] { Path.GetFullPath(file) }, Resolve(false, file));
    }

    [Fact]
    public void Several_files_keep_the_order_they_were_typed()
    {
        var c = Touch("c.png");
        var a = Touch("a.png");
        var b = Touch("b.png");

        Assert.Equal(new[] { c, a, b }.Select(Path.GetFullPath), Resolve(false, c, a, b));
    }

    [Fact]
    public void Duplicates_are_dropped_while_first_position_is_kept()
    {
        var a = Touch("a.png");
        var b = Touch("b.png");

        Assert.Equal(new[] { a, b }.Select(Path.GetFullPath), Resolve(false, a, b, a));
    }

    [Fact]
    public void Blank_tokens_are_ignored_rather_than_failing_the_run()
    {
        var file = Touch("page.png");

        Assert.Equal(new[] { Path.GetFullPath(file) }, Resolve(false, "", "   ", file));
    }

    // ---------------------------------------------------------------- directories

    [Fact]
    public void A_directory_yields_its_supported_files_sorted_for_reproducibility()
    {
        Touch("scans/b.png");
        Touch("scans/a.jpg");
        Touch("scans/c.pdf");

        var resolved = Resolve(false, Path.Combine(_root, "scans"));

        Assert.Equal(3, resolved.Count);
        Assert.Equal(resolved.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), resolved);
    }

    [Fact]
    public void A_directory_scan_skips_unsupported_extensions()
    {
        Touch("scans/page.png");
        Touch("scans/notes.txt");
        Touch("scans/archive.zip");
        Touch("scans/readme.md");

        var resolved = Resolve(false, Path.Combine(_root, "scans"));

        Assert.Single(resolved);
        Assert.EndsWith("page.png", resolved[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_directory_scan_is_shallow_unless_recursive_is_requested()
    {
        Touch("scans/top.png");
        Touch("scans/nested/deep.png");

        var shallow = Resolve(false, Path.Combine(_root, "scans"));
        var deep = Resolve(true, Path.Combine(_root, "scans"));

        Assert.Single(shallow);
        Assert.Equal(2, deep.Count);
    }

    [Fact]
    public void An_empty_directory_is_reported_as_matching_nothing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

        var ex = Assert.Throws<CliUsageException>(() => Resolve(false, Path.Combine(_root, "empty")));

        Assert.Contains("matched no readable file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_directory_containing_only_unsupported_files_matches_nothing()
    {
        Touch("junk/notes.txt");

        Assert.Throws<CliUsageException>(() => Resolve(false, Path.Combine(_root, "junk")));
    }

    // ---------------------------------------------------------------- globs

    [Fact]
    public void A_star_glob_expands_to_the_matching_supported_files()
    {
        Touch("scans/a.png");
        Touch("scans/b.png");
        Touch("scans/c.jpg");

        var resolved = Resolve(false, Path.Combine(_root, "scans", "*.png"));

        Assert.Equal(2, resolved.Count);
        Assert.All(resolved, p => Assert.EndsWith(".png", p, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_question_mark_glob_matches_a_single_character()
    {
        Touch("scans/a1.png");
        Touch("scans/a2.png");
        Touch("scans/a10.png");

        var resolved = Resolve(false, Path.Combine(_root, "scans", "a?.png"));

        Assert.Equal(2, resolved.Count);
    }

    [Fact]
    public void A_glob_still_filters_by_supported_extension()
    {
        Touch("scans/page.txt");

        // '*' matches the name, but .txt is not something the tool can decode.
        Assert.Throws<CliUsageException>(() => Resolve(false, Path.Combine(_root, "scans", "*")));
    }

    [Fact]
    public void A_glob_descends_only_when_recursive_is_requested()
    {
        Touch("scans/top.png");
        Touch("scans/nested/deep.png");

        var shallow = Resolve(false, Path.Combine(_root, "scans", "*.png"));
        var deep = Resolve(true, Path.Combine(_root, "scans", "*.png"));

        Assert.Single(shallow);
        Assert.Equal(2, deep.Count);
    }

    [Fact]
    public void A_wildcard_in_a_directory_segment_is_rejected_with_advice()
    {
        var ex = Assert.Throws<CliUsageException>(
            () => Resolve(false, Path.Combine(_root, "*", "page.png")));

        Assert.Contains("--recursive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_glob_rooted_at_a_missing_directory_says_so()
    {
        var ex = Assert.Throws<CliUsageException>(
            () => Resolve(false, Path.Combine(_root, "nope", "*.png")));

        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_glob_matching_nothing_is_an_error_rather_than_an_empty_run()
    {
        Directory.CreateDirectory(Path.Combine(_root, "scans"));

        Assert.Throws<CliUsageException>(() => Resolve(false, Path.Combine(_root, "scans", "*.png")));
    }

    // ---------------------------------------------------------------- mixed & missing

    [Fact]
    public void Files_directories_and_globs_can_be_mixed_and_deduplicated_across_forms()
    {
        var single = Touch("single.png");
        Touch("scans/a.png");
        Touch("scans/b.png");

        var resolved = Resolve(
            false,
            single,
            Path.Combine(_root, "scans"),
            Path.Combine(_root, "scans", "*.png"));   // overlaps the directory scan

        Assert.Equal(3, resolved.Count);
        Assert.Equal(resolved.Count, resolved.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void A_missing_path_names_the_offending_token()
    {
        var ex = Assert.Throws<CliUsageException>(() => Resolve(false, Path.Combine(_root, "ghost.png")));

        Assert.Contains("ghost.png", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_token_list_resolves_to_nothing_without_throwing()
    {
        Assert.Empty(Resolve(false));
    }

    // ---------------------------------------------------------------- pdf detection

    [Theory]
    [InlineData("scan.pdf", true)]
    [InlineData("scan.PDF", true)]
    [InlineData("a.b.pdf", true)]
    [InlineData("scan.png", false)]
    [InlineData("scan.pdf.png", false)]
    [InlineData("pdf", false)]
    [InlineData("", false)]
    public void Pdf_detection_looks_at_the_extension_case_insensitively(string path, bool expected)
    {
        Assert.Equal(expected, InputResolver.IsPdf(path));
    }

    // ---------------------------------------------------------------- model cache

    [Fact]
    public void An_explicit_cache_path_wins_and_is_made_absolute()
    {
        var resolved = ModelCacheLocator.Resolve(_root);

        Assert.Equal(Path.GetFullPath(_root), resolved);
        Assert.True(Path.IsPathRooted(resolved));
    }

    [Fact]
    public void A_relative_cache_path_is_resolved_against_the_working_directory()
    {
        var resolved = ModelCacheLocator.Resolve("models");

        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith("models", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_override_falls_back_to_the_ambient_cache_location(string? blank)
    {
        // Deliberately not asserting the exact folder: it depends on EASYOCRSHARP_CACHE, and mutating
        // that variable here would race with the model-backed integration tests running in parallel.
        var resolved = ModelCacheLocator.Resolve(blank);

        Assert.False(string.IsNullOrWhiteSpace(resolved));
        Assert.True(Path.IsPathRooted(resolved));
    }

    [Fact]
    public void The_documented_environment_variable_name_is_the_one_the_library_reads()
    {
        Assert.Equal("EASYOCRSHARP_CACHE", ModelCacheLocator.CacheEnvironmentVariable);
    }

    [Fact]
    public void A_cache_directory_that_does_not_exist_lists_nothing_instead_of_throwing()
    {
        Assert.Empty(ModelCacheLocator.CachedFiles(Path.Combine(_root, "never-created")));
    }

    [Fact]
    public void Cached_files_are_listed_sorted_with_partial_downloads_hidden()
    {
        var cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "latin_g2.onnx"), [1]);
        File.WriteAllBytes(Path.Combine(cache, "craft_mlt_25k.onnx"), [1]);
        File.WriteAllBytes(Path.Combine(cache, "latin_g2.vocab.json"), [1]);
        File.WriteAllBytes(Path.Combine(cache, "half.onnx.tmp"), [1]);
        File.WriteAllBytes(Path.Combine(cache, "half.onnx.part"), [1]);

        var files = ModelCacheLocator.CachedFiles(cache);

        Assert.Equal(3, files.Count);
        Assert.DoesNotContain(files, f => f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(files.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal), files.Select(f => f.Name));
    }

    [Fact]
    public void Cached_files_listing_is_shallow()
    {
        var cache = Path.Combine(_root, "cache2");
        Directory.CreateDirectory(Path.Combine(cache, "nested"));
        File.WriteAllBytes(Path.Combine(cache, "top.onnx"), [1]);
        File.WriteAllBytes(Path.Combine(cache, "nested", "deep.onnx"), [1]);

        Assert.Single(ModelCacheLocator.CachedFiles(cache));
    }
}
