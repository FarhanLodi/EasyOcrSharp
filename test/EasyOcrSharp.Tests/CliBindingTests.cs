using EasyOcrSharp.Cli.CommandLine;
using EasyOcrSharp.Cli.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Services;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Unit tests for the CLI's option binding — the layer that turns parsed arguments into the library's
/// option records. Every unrecognized value is supposed to fail loudly rather than silently fall back
/// to a default, so the negative cases matter as much as the positive ones.
/// </summary>
public class CliBindingTests
{
    private static readonly CliOption Paragraph = new("paragraph", null, false, "Group into paragraphs.");
    private static readonly CliOption Line = new("line", null, false, "Group into lines.");
    private static readonly CliOption Word = new("word", null, false, "One result per word.");
    private static readonly ScanFlags Flags = new(Paragraph, Line, Word);

    private static readonly CliCommand Command = new(
        "scan",
        "Recognize text.",
        "scan <input...>",
        [
            CommonOptions.Lang, CommonOptions.Gpu, CommonOptions.Cpu, CommonOptions.Cache,
            CommonOptions.Offline, CommonOptions.Quiet, CommonOptions.Dpi, CommonOptions.Preprocess,
            CommonOptions.Allowlist, CommonOptions.Blocklist, CommonOptions.MinConfidence,
            CommonOptions.Decoder, CommonOptions.BeamWidth, CommonOptions.Detail,
            Paragraph, Line, Word,
        ]);

    private static ParsedArgs Parse(params string[] args) => ArgParser.Parse(Command, args);

    private static RecognitionOptions Recognition(params string[] args)
        => OptionBinder.BuildRecognitionOptions(Parse(args), Flags);

    // A quiet console never touches the real stdout and reports ProgressEnabled == false.
    private static CliConsole QuietConsole => new(quiet: true);

    // ---------------------------------------------------------------- languages

    [Fact]
    public void Languages_default_to_english_so_the_simplest_invocation_needs_no_flags()
    {
        Assert.Equal(new[] { "en" }, OptionBinder.Languages(Parse("page.png")));
    }

    [Fact]
    public void Languages_come_from_lang_in_every_accepted_spelling()
    {
        Assert.Equal(new[] { "en", "fr", "de" }, OptionBinder.Languages(Parse("-l", "en,fr,de")));
        Assert.Equal(new[] { "en", "fr", "de" }, OptionBinder.Languages(Parse("-l", "en", "-l", "fr", "-l", "de")));
        Assert.Equal(new[] { "ch_sim" }, OptionBinder.Languages(Parse("--lang=ch_sim")));
    }

    // ---------------------------------------------------------------- service options

    [Fact]
    public void The_execution_provider_defaults_to_auto()
    {
        var options = OptionBinder.BuildServiceOptions(Parse(), QuietConsole);

        Assert.Equal(OcrExecutionProvider.Auto, options.ExecutionProvider);
        Assert.Null(options.ModelCachePath);
        Assert.False(options.Download.Offline);
    }

    [Fact]
    public void Gpu_and_cpu_select_their_providers()
    {
        Assert.Equal(OcrExecutionProvider.Cuda, OptionBinder.BuildServiceOptions(Parse("--gpu"), QuietConsole).ExecutionProvider);
        Assert.Equal(OcrExecutionProvider.Cpu, OptionBinder.BuildServiceOptions(Parse("--cpu"), QuietConsole).ExecutionProvider);
    }

    [Fact]
    public void Gpu_and_cpu_together_are_rejected()
    {
        var ex = Assert.Throws<CliUsageException>(
            () => OptionBinder.BuildServiceOptions(Parse("--gpu", "--cpu"), QuietConsole));

        Assert.Contains("mutually exclusive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cache_and_offline_are_carried_through()
    {
        var options = OptionBinder.BuildServiceOptions(Parse("--cache", "/models", "--offline"), QuietConsole);

        Assert.Equal("/models", options.ModelCachePath);
        Assert.True(options.Download.Offline);
    }

    [Fact]
    public void A_quiet_console_attaches_no_download_progress_reporter()
    {
        // Progress writes control characters; piping the output must stay clean.
        Assert.Null(OptionBinder.BuildServiceOptions(Parse(), QuietConsole).Download.Progress);
    }

    // ---------------------------------------------------------------- grouping

    [Fact]
    public void Grouping_defaults_to_line()
    {
        Assert.Equal(TextGrouping.Line, Recognition().Grouping);
        Assert.Equal(TextGrouping.Line, Recognition("--line").Grouping);
    }

    [Theory]
    [InlineData("--paragraph", TextGrouping.Paragraph)]
    [InlineData("--word", TextGrouping.Word)]
    public void Each_grouping_flag_selects_its_mode(string flag, TextGrouping expected)
    {
        Assert.Equal(expected, Recognition(flag).Grouping);
    }

    [Theory]
    [InlineData("--paragraph", "--word")]
    [InlineData("--paragraph", "--line")]
    [InlineData("--line", "--word")]
    public void Two_grouping_flags_at_once_are_rejected(string first, string second)
    {
        var ex = Assert.Throws<CliUsageException>(() => Recognition(first, second));

        Assert.Contains("mutually exclusive", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- decoder

    [Fact]
    public void The_decoder_defaults_to_greedy_with_a_beam_width_of_five()
    {
        var options = Recognition();

        Assert.Equal(DecoderType.Greedy, options.Decoder);
        Assert.Equal(5, options.BeamWidth);
    }

    [Theory]
    [InlineData("greedy", DecoderType.Greedy)]
    [InlineData("beam", DecoderType.BeamSearch)]
    [InlineData("beamsearch", DecoderType.BeamSearch)]
    [InlineData("beam-search", DecoderType.BeamSearch)]
    [InlineData("wordbeam", DecoderType.WordBeamSearch)]
    [InlineData("word-beam", DecoderType.WordBeamSearch)]
    [InlineData("wordbeamsearch", DecoderType.WordBeamSearch)]
    [InlineData("BEAM", DecoderType.BeamSearch)]
    public void Every_documented_decoder_spelling_is_accepted_case_insensitively(string spelling, DecoderType expected)
    {
        Assert.Equal(expected, Recognition("--decoder", spelling).Decoder);
    }

    [Fact]
    public void An_unknown_decoder_is_rejected_and_lists_the_accepted_names()
    {
        var ex = Assert.Throws<CliUsageException>(() => Recognition("--decoder", "magic"));

        Assert.Contains("greedy, beam, wordbeam", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Beam_width_is_honoured_and_must_be_at_least_one()
    {
        Assert.Equal(12, Recognition("--beam-width", "12").BeamWidth);
        Assert.Throws<CliUsageException>(() => Recognition("--beam-width", "0"));
        Assert.Throws<CliUsageException>(() => Recognition("--beam-width", "-3"));
    }

    // ---------------------------------------------------------------- detail

    [Fact]
    public void Detail_defaults_to_none_so_nothing_extra_is_computed()
    {
        Assert.Equal(WordLevelDetail.None, Recognition().WordLevelDetail);
    }

    [Theory]
    [InlineData("none", WordLevelDetail.None)]
    [InlineData("line", WordLevelDetail.None)]
    [InlineData("word", WordLevelDetail.Words)]
    [InlineData("words", WordLevelDetail.Words)]
    [InlineData("char", WordLevelDetail.Characters)]
    [InlineData("chars", WordLevelDetail.Characters)]
    [InlineData("character", WordLevelDetail.Characters)]
    [InlineData("characters", WordLevelDetail.Characters)]
    [InlineData("WORDS", WordLevelDetail.Words)]
    public void Every_documented_detail_spelling_is_accepted(string spelling, WordLevelDetail expected)
    {
        Assert.Equal(expected, Recognition("--detail", spelling).WordLevelDetail);
    }

    [Fact]
    public void An_unknown_detail_level_is_rejected()
    {
        var ex = Assert.Throws<CliUsageException>(() => Recognition("--detail", "everything"));

        Assert.Contains("none, words, chars", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- filters

    [Fact]
    public void Allowlist_and_blocklist_are_passed_through_verbatim()
    {
        var options = Recognition("--allowlist", "0123456789", "--blocklist", "!?");

        Assert.Equal("0123456789", options.Allowlist);
        Assert.Equal("!?", options.Blocklist);
    }

    [Fact]
    public void Character_filters_are_null_by_default()
    {
        var options = Recognition();

        Assert.Null(options.Allowlist);
        Assert.Null(options.Blocklist);
    }

    [Fact]
    public void Min_confidence_defaults_to_zero_and_accepts_the_whole_valid_range()
    {
        Assert.Equal(0, Recognition().MinConfidence);
        Assert.Equal(0, Recognition("--min-confidence", "0").MinConfidence);
        Assert.Equal(0.85, Recognition("--min-confidence", "0.85").MinConfidence);
        Assert.Equal(1, Recognition("--min-confidence", "1").MinConfidence);
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.1")]
    [InlineData("42")]
    public void Min_confidence_outside_zero_to_one_is_rejected(string value)
    {
        var ex = Assert.Throws<CliUsageException>(() => Recognition("--min-confidence", value));

        Assert.Contains("between 0 and 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_numeric_min_confidence_is_rejected()
    {
        Assert.Throws<CliUsageException>(() => Recognition("--min-confidence", "high"));
    }

    // ---------------------------------------------------------------- preprocessing

    [Fact]
    public void Preprocessing_is_off_by_default()
    {
        Assert.False(Recognition().Preprocessing.IsAnyEnabled);
    }

    [Fact]
    public void Each_preprocessing_step_sets_only_its_own_switch()
    {
        Assert.True(Recognition("--preprocess", "deskew").Preprocessing.Deskew);
        Assert.True(Recognition("--preprocess", "binarize").Preprocessing.Binarize);
        Assert.True(Recognition("--preprocess", "binarise").Preprocessing.Binarize);
        Assert.True(Recognition("--preprocess", "threshold").Preprocessing.Binarize);
        Assert.True(Recognition("--preprocess", "denoise").Preprocessing.Denoise);
        Assert.True(Recognition("--preprocess", "sharpen").Preprocessing.Sharpen);
        Assert.True(Recognition("--preprocess", "orientation").Preprocessing.DocumentOrientation);
        Assert.True(Recognition("--preprocess", "rotate").Preprocessing.DetectOrientation);
        Assert.True(Recognition("--preprocess", "detect-orientation").Preprocessing.DetectOrientation);
        Assert.True(Recognition("--preprocess", "unwarp").Preprocessing.DocumentUnwarp);
        Assert.True(Recognition("--preprocess", "dewarp").Preprocessing.DocumentUnwarp);
    }

    [Fact]
    public void Orientation_and_rotate_are_different_strategies_not_synonyms()
    {
        // 'orientation' is the cheap single-pass classifier; 'rotate' is the 4x OCR sweep.
        var cheap = Recognition("--preprocess", "orientation").Preprocessing;
        var sweep = Recognition("--preprocess", "rotate").Preprocessing;

        Assert.True(cheap.DocumentOrientation);
        Assert.False(cheap.DetectOrientation);
        Assert.True(sweep.DetectOrientation);
        Assert.False(sweep.DocumentOrientation);
    }

    [Fact]
    public void Preprocessing_steps_combine_and_accept_repetition_and_whitespace()
    {
        var options = Recognition("--preprocess", " deskew , sharpen ", "--preprocess", "binarize").Preprocessing;

        Assert.True(options.Deskew);
        Assert.True(options.Sharpen);
        Assert.True(options.Binarize);
        Assert.False(options.Denoise);
    }

    [Fact]
    public void Repeating_the_same_step_is_idempotent()
    {
        Assert.True(Recognition("--preprocess", "deskew,deskew,deskew").Preprocessing.Deskew);
    }

    [Fact]
    public void Repeating_the_option_adds_steps_rather_than_replacing_them()
    {
        // --preprocess is repeatable like --lang: writing it twice must not silently drop the first
        // value, which is exactly the kind of quiet surprise the parser is designed to avoid.
        var separate = Recognition("--preprocess", "deskew", "--preprocess", "sharpen").Preprocessing;
        var combined = Recognition("--preprocess", "deskew,sharpen").Preprocessing;

        Assert.Equal(combined, separate);
        Assert.True(separate.Deskew);
        Assert.True(separate.Sharpen);
    }

    [Fact]
    public void An_unknown_preprocessing_step_is_rejected_and_lists_the_valid_ones()
    {
        var ex = Assert.Throws<CliUsageException>(() => Recognition("--preprocess", "deskew,enhance"));

        Assert.Contains("enhance", ex.Message, StringComparison.Ordinal);
        Assert.Contains("deskew", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- pdf options

    [Fact]
    public void Pdf_dpi_defaults_to_the_library_default_and_accepts_the_documented_range()
    {
        Assert.Equal(new PdfOcrOptions().Dpi, OptionBinder.BuildPdfOptions(Parse()).Dpi);
        Assert.Equal(36, OptionBinder.BuildPdfOptions(Parse("--dpi", "36")).Dpi);
        Assert.Equal(600, OptionBinder.BuildPdfOptions(Parse("--dpi", "600")).Dpi);
        Assert.Equal(1200, OptionBinder.BuildPdfOptions(Parse("--dpi", "1200")).Dpi);
    }

    [Theory]
    [InlineData("35")]
    [InlineData("1201")]
    [InlineData("0")]
    [InlineData("-100")]
    public void Pdf_dpi_outside_the_supported_range_is_rejected(string dpi)
    {
        var ex = Assert.Throws<CliUsageException>(() => OptionBinder.BuildPdfOptions(Parse("--dpi", dpi)));

        Assert.Contains("between 36 and 1200", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pdf_progress_reporter_is_attached_when_supplied()
    {
        var progress = new Progress<PdfPageProgress>();

        Assert.Same(progress, OptionBinder.BuildPdfOptions(Parse(), progress).Progress);
        Assert.Null(OptionBinder.BuildPdfOptions(Parse()).Progress);
    }

    // ---------------------------------------------------------------- whole-line binding

    [Fact]
    public void A_realistic_command_line_binds_every_option_at_once()
    {
        var options = Recognition(
            "scans/", "-l", "en,de", "--paragraph", "--detail", "words",
            "--decoder", "beam", "--beam-width", "8",
            "--min-confidence", "0.6", "--allowlist", "ABC123",
            "--preprocess", "deskew,sharpen");

        Assert.Equal(TextGrouping.Paragraph, options.Grouping);
        Assert.Equal(WordLevelDetail.Words, options.WordLevelDetail);
        Assert.Equal(DecoderType.BeamSearch, options.Decoder);
        Assert.Equal(8, options.BeamWidth);
        Assert.Equal(0.6, options.MinConfidence);
        Assert.Equal("ABC123", options.Allowlist);
        Assert.True(options.Preprocessing.Deskew);
        Assert.True(options.Preprocessing.Sharpen);
        Assert.Equal(new[] { "en", "de" }, OptionBinder.Languages(Parse("-l", "en,de")));
    }
}
