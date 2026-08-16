using EasyOcrSharp.Cli.CommandLine;
using EasyOcrSharp.Cli.Internal;
using EasyOcrSharp.Services;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Unit tests for the CLI's console policy and download-progress rendering.
/// </summary>
/// <remarks>
/// The live progress line is drawn only on a real terminal — when either stream is redirected it is
/// suppressed so a pipe or a CI log stays clean. A test host always runs with redirected streams, so
/// these tests verify the <i>suppressed</i> side of that contract (which is the side that would
/// corrupt a user's pipeline if it ever broke) plus the byte formatting, which is pure.
/// </remarks>
public class CliConsoleTests
{
    // ---------------------------------------------------------------- byte formatting

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]
    public void Small_sizes_are_reported_in_bytes(long bytes, string expected)
    {
        Assert.Equal(expected, DownloadProgressReporter.Format(bytes));
    }

    [Theory]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024 * 1024 - 1, "1024.0 KB")]
    public void Kilobyte_sizes_get_one_decimal(long bytes, string expected)
    {
        Assert.Equal(expected, DownloadProgressReporter.Format(bytes));
    }

    [Theory]
    [InlineData(1024 * 1024, "1.0 MB")]
    [InlineData(45L * 1024 * 1024, "45.0 MB")]
    [InlineData(23L * 1024 * 1024 + 512 * 1024, "23.5 MB")]
    public void Megabyte_sizes_get_one_decimal(long bytes, string expected)
    {
        Assert.Equal(expected, DownloadProgressReporter.Format(bytes));
    }

    [Theory]
    [InlineData(1024L * 1024 * 1024, "1.00 GB")]
    [InlineData(3L * 1024 * 1024 * 1024 + 512L * 1024 * 1024, "3.50 GB")]
    public void Gigabyte_sizes_get_two_decimals(long bytes, string expected)
    {
        Assert.Equal(expected, DownloadProgressReporter.Format(bytes));
    }

    [Fact]
    public void Each_unit_boundary_switches_cleanly()
    {
        // The exact thresholds matter: an off-by-one here shows a user "1024.0 KB" instead of "1.0 MB".
        Assert.EndsWith("B", DownloadProgressReporter.Format(1023), StringComparison.Ordinal);
        Assert.EndsWith("KB", DownloadProgressReporter.Format(1024), StringComparison.Ordinal);
        Assert.EndsWith("KB", DownloadProgressReporter.Format((1024 * 1024) - 1), StringComparison.Ordinal);
        Assert.EndsWith("MB", DownloadProgressReporter.Format(1024 * 1024), StringComparison.Ordinal);
        Assert.EndsWith("MB", DownloadProgressReporter.Format((1024L * 1024 * 1024) - 1), StringComparison.Ordinal);
        Assert.EndsWith("GB", DownloadProgressReporter.Format(1024L * 1024 * 1024), StringComparison.Ordinal);
    }

    [Fact]
    public void Formatting_never_produces_a_negative_or_empty_label()
    {
        foreach (long bytes in new[] { 0L, 1L, 999L, 100_000L, 50_000_000L, 9_000_000_000L })
        {
            var text = DownloadProgressReporter.Format(bytes);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain('-', text);
        }
    }

    // ---------------------------------------------------------------- console policy

    [Fact]
    public void Progress_is_disabled_when_a_stream_is_redirected()
    {
        // The whole point: `easyocrsharp scan page.png | jq` must not receive redraw characters.
        // A test host always has redirected streams, so this is the real condition under test.
        Assert.False(new CliConsole(quiet: false).ProgressEnabled);
        Assert.False(new CliConsole(quiet: true).ProgressEnabled);
    }

    [Fact]
    public void Quiet_is_reported_as_asked_for()
    {
        Assert.True(new CliConsole(quiet: true).Quiet);
        Assert.False(new CliConsole(quiet: false).Quiet);
    }

    [Fact]
    public void Informational_output_goes_to_stderr_so_stdout_stays_the_payload()
    {
        var stderr = new StringWriter();
        var original = Console.Error;
        try
        {
            Console.SetError(stderr);
            new CliConsole(quiet: false).Info("scanning 3 files");
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Contains("scanning 3 files", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Quiet_suppresses_informational_output()
    {
        var stderr = new StringWriter();
        var original = Console.Error;
        try
        {
            Console.SetError(stderr);
            new CliConsole(quiet: true).Info("scanning 3 files");
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public void Quiet_never_suppresses_an_error()
    {
        // A failure has to stay visible, otherwise --quiet turns a broken run into a silent one.
        var stderr = new StringWriter();
        var original = Console.Error;
        try
        {
            Console.SetError(stderr);
            new CliConsole(quiet: true).Error("could not read page.png");
        }
        finally
        {
            Console.SetError(original);
        }

        var text = stderr.ToString();
        Assert.Contains("could not read page.png", text, StringComparison.Ordinal);
        Assert.Contains("error:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_warning_is_labelled_and_follows_the_quiet_rule()
    {
        var loud = new StringWriter();
        var silent = new StringWriter();
        var original = Console.Error;
        try
        {
            Console.SetError(loud);
            new CliConsole(quiet: false).Warn("no GPU found");

            Console.SetError(silent);
            new CliConsole(quiet: true).Warn("no GPU found");
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Contains("warning: no GPU found", loud.ToString(), StringComparison.Ordinal);
        Assert.Equal("", silent.ToString());
    }

    [Fact]
    public void Drawing_progress_while_redirected_emits_nothing_at_all()
    {
        // Not merely "no visible line" — literally no bytes, so a captured log has no stray \r padding.
        var stderr = new StringWriter();
        var original = Console.Error;
        try
        {
            Console.SetError(stderr);
            var console = new CliConsole(quiet: false);
            console.Progress("[1/10] page.png");
            console.Progress("[2/10] a-much-longer-file-name.png");
            console.ClearProgress();
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public void A_download_report_writes_nothing_when_progress_is_disabled()
    {
        var stderr = new StringWriter();
        var original = Console.Error;
        try
        {
            Console.SetError(stderr);
            var reporter = new DownloadProgressReporter(new CliConsole(quiet: false));
            reporter.Report(new ModelDownloadProgress("latin_g2.onnx", 1024, 4096));
            reporter.Report(new ModelDownloadProgress("latin_g2.onnx", 4096, 4096));
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public void Clearing_progress_that_was_never_drawn_is_harmless()
    {
        var stderr = new StringWriter();
        var original = Console.Error;
        try
        {
            Console.SetError(stderr);
            new CliConsole(quiet: false).ClearProgress();
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal("", stderr.ToString());
    }

    // ---------------------------------------------------------------- progress payload

    [Fact]
    public void The_download_fraction_is_reported_for_a_known_total()
    {
        var progress = new ModelDownloadProgress("latin_g2.onnx", 1024, 4096);

        Assert.Equal(0.25, progress.Fraction);
    }

    [Fact]
    public void The_fraction_is_null_when_the_server_sends_no_content_length()
    {
        // Then the reporter shows bytes-so-far instead of a percentage, rather than dividing by zero.
        Assert.Null(new ModelDownloadProgress("latin_g2.onnx", 1024, 0).Fraction);
    }
}
