using EasyOcrSharp.Cli.CommandLine;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Unit tests for the CLI's hand-rolled argument parser. The tool ships as a <c>dotnet tool</c>, so a
/// parsing slip is a user-visible bug in a published package — these cover the accepted spellings, the
/// rejections, and the edge cases that separate a forgiving parser from a sloppy one.
/// </summary>
public class CliArgParserTests
{
    // A command exercising every option shape: value option with alias, repeatable, flag, and a
    // value option with no alias.
    private static readonly CliOption Lang = new("lang", "-l", true, "Languages.", "codes", Repeatable: true);
    private static readonly CliOption Output = new("output", "-o", true, "Destination.", "path");
    private static readonly CliOption Dpi = new("dpi", null, true, "Resolution.", "n");
    private static readonly CliOption Recursive = new("recursive", "-r", false, "Descend.");
    private static readonly CliOption Quiet = new("quiet", "-q", false, "Silence.");

    private static readonly CliCommand Command = new(
        "scan",
        "Recognize text.",
        "scan <input...>",
        [Lang, Output, Dpi, Recursive, Quiet]);

    private static ParsedArgs Parse(params string[] args) => ArgParser.Parse(Command, args);

    // ---------------------------------------------------------------- positionals

    [Fact]
    public void Bare_tokens_are_positionals_in_the_order_given()
    {
        var args = Parse("a.png", "b.png", "c.png");

        Assert.Equal(new[] { "a.png", "b.png", "c.png" }, args.Positionals);
    }

    [Fact]
    public void No_arguments_yields_no_positionals_no_flags_and_no_values()
    {
        var args = Parse();

        Assert.Empty(args.Positionals);
        Assert.False(args.Flag(Recursive.Name));
        Assert.Null(args.Value(Lang.Name));
        Assert.Empty(args.Values(Lang.Name));
        Assert.Empty(args.List(Lang.Name));
    }

    [Fact]
    public void Positionals_and_options_can_be_interleaved()
    {
        var args = Parse("a.png", "-l", "en", "b.png", "--recursive", "c.png");

        Assert.Equal(new[] { "a.png", "b.png", "c.png" }, args.Positionals);
        Assert.Equal("en", args.Value(Lang.Name));
        Assert.True(args.Flag(Recursive.Name));
    }

    [Fact]
    public void A_lone_dash_is_a_positional_not_an_option()
    {
        // Conventionally "-" means stdin; either way it must not be parsed as an option.
        Assert.Equal(new[] { "-" }, Parse("-").Positionals);
    }

    [Fact]
    public void An_empty_token_is_kept_as_a_positional()
    {
        Assert.Equal(new[] { "" }, Parse("").Positionals);
    }

    // ---------------------------------------------------------------- value options

    [Theory]
    [InlineData("--lang", "en")]
    [InlineData("--lang=en", null)]
    [InlineData("-l", "en")]
    [InlineData("-l=en", null)]
    public void Every_accepted_spelling_of_a_value_option_parses(string first, string? second)
    {
        var args = second is null ? Parse(first) : Parse(first, second);

        Assert.Equal("en", args.Value(Lang.Name));
    }

    [Fact]
    public void An_option_is_always_looked_up_by_its_long_name_even_when_typed_as_an_alias()
    {
        // The alias is resolved during parsing, so callers never have to know it existed.
        Assert.Equal("out.txt", Parse("-o", "out.txt").Value(Output.Name));
    }

    [Fact]
    public void A_value_containing_an_equals_sign_keeps_everything_after_the_first_one()
    {
        Assert.Equal("a=b=c", Parse("--output=a=b=c").Value(Output.Name));
    }

    [Fact]
    public void An_empty_inline_value_is_accepted_as_an_empty_string()
    {
        Assert.Equal("", Parse("--output=").Value(Output.Name));
    }

    [Fact]
    public void A_value_that_looks_like_an_option_is_still_consumed_as_the_value()
    {
        // Otherwise "--output --lang" would silently produce a null destination.
        Assert.Equal("--lang", Parse("--output", "--lang").Value(Output.Name));
    }

    [Fact]
    public void A_negative_number_can_be_an_option_value()
    {
        Assert.Equal(-1, Parse("--dpi", "-1").Int(Dpi.Name));
    }

    [Fact]
    public void A_repeated_non_repeatable_option_keeps_the_last_value()
    {
        Assert.Equal("second.txt", Parse("-o", "first.txt", "-o", "second.txt").Value(Output.Name));
        Assert.Single(Parse("-o", "first.txt", "-o", "second.txt").Values(Output.Name));
    }

    [Fact]
    public void A_repeatable_option_keeps_every_value_in_order()
    {
        var args = Parse("-l", "en", "-l", "fr", "-l", "de");

        Assert.Equal(new[] { "en", "fr", "de" }, args.Values(Lang.Name));
        Assert.Equal("de", args.Value(Lang.Name));
    }

    // ---------------------------------------------------------------- flags

    [Theory]
    [InlineData("--recursive")]
    [InlineData("-r")]
    public void A_flag_is_recognized_in_both_spellings(string spelling)
    {
        Assert.True(Parse(spelling).Flag(Recursive.Name));
    }

    [Fact]
    public void An_absent_flag_is_false()
    {
        Assert.False(Parse("--quiet").Flag(Recursive.Name));
    }

    [Fact]
    public void Repeating_a_flag_is_harmless()
    {
        Assert.True(Parse("-r", "--recursive", "-r").Flag(Recursive.Name));
    }

    [Fact]
    public void Several_flags_are_independent()
    {
        var args = Parse("-r", "-q");

        Assert.True(args.Flag(Recursive.Name));
        Assert.True(args.Flag(Quiet.Name));
    }

    // ---------------------------------------------------------------- the -- separator

    [Fact]
    public void A_double_dash_stops_option_parsing()
    {
        // So a file genuinely named "--weird.png" can still be scanned.
        var args = Parse("-r", "--", "--lang", "--weird.png");

        Assert.True(args.Flag(Recursive.Name));
        Assert.Equal(new[] { "--lang", "--weird.png" }, args.Positionals);
        Assert.Null(args.Value(Lang.Name));
    }

    [Fact]
    public void A_second_double_dash_after_the_separator_is_a_positional()
    {
        Assert.Equal(new[] { "--", "x" }, Parse("--", "--", "x").Positionals);
    }

    [Fact]
    public void A_trailing_double_dash_with_nothing_after_it_is_harmless()
    {
        Assert.Empty(Parse("--").Positionals);
    }

    // ---------------------------------------------------------------- rejections

    [Fact]
    public void An_unknown_long_option_is_rejected_rather_than_ignored()
    {
        // A typo'd option in a batch job must fail loudly, not silently change behaviour.
        var ex = Assert.Throws<CliUsageException>(() => Parse("--nonsense"));

        Assert.Contains("--nonsense", ex.Message, StringComparison.Ordinal);
        Assert.Same(Command, ex.Command);
    }

    [Fact]
    public void An_unknown_short_option_is_rejected()
    {
        var ex = Assert.Throws<CliUsageException>(() => Parse("-z"));

        Assert.Contains("-z", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--lng", "lang")]
    [InlineData("--langs", "lang")]
    [InlineData("--outpu", "output")]
    public void A_near_miss_option_suggests_the_intended_one(string typo, string expected)
    {
        var ex = Assert.Throws<CliUsageException>(() => Parse(typo));

        Assert.Contains($"Did you mean '--{expected}'?", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wildly_wrong_option_offers_no_misleading_suggestion()
    {
        var ex = Assert.Throws<CliUsageException>(() => Parse("--zzzzzzzzzzzz"));

        Assert.DoesNotContain("Did you mean", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_option_at_the_end_with_no_value_is_rejected()
    {
        var ex = Assert.Throws<CliUsageException>(() => Parse("a.png", "--lang"));

        Assert.Contains("requires a value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Giving_a_flag_a_value_is_rejected()
    {
        var ex = Assert.Throws<CliUsageException>(() => Parse("--recursive=yes"));

        Assert.Contains("does not take a value", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- List()

    [Fact]
    public void List_flattens_repetition_and_comma_separation_identically()
    {
        var repeated = Parse("-l", "en", "-l", "fr", "-l", "de").List(Lang.Name);
        var commas = Parse("-l", "en,fr,de").List(Lang.Name);
        var mixed = Parse("-l", "en,fr", "-l", "de").List(Lang.Name);

        Assert.Equal(new[] { "en", "fr", "de" }, repeated);
        Assert.Equal(new[] { "en", "fr", "de" }, commas);
        Assert.Equal(new[] { "en", "fr", "de" }, mixed);
    }

    [Fact]
    public void List_trims_whitespace_and_drops_empty_entries()
    {
        Assert.Equal(new[] { "en", "fr", "de" }, Parse("-l", " en , fr ,, de , ").List(Lang.Name));
    }

    [Fact]
    public void List_accepts_semicolons_as_well_as_commas()
    {
        Assert.Equal(new[] { "en", "fr" }, Parse("-l", "en;fr").List(Lang.Name));
    }

    [Fact]
    public void List_of_an_unused_option_is_empty_rather_than_null()
    {
        Assert.Empty(Parse().List(Lang.Name));
    }

    [Fact]
    public void List_of_a_value_that_is_only_separators_is_empty()
    {
        Assert.Empty(Parse("-l", " , ; ,").List(Lang.Name));
    }

    // ---------------------------------------------------------------- Int() / Double()

    [Fact]
    public void Int_parses_a_whole_number_and_returns_null_when_unused()
    {
        Assert.Equal(300, Parse("--dpi", "300").Int(Dpi.Name));
        Assert.Null(Parse().Int(Dpi.Name));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12.5")]
    [InlineData("")]
    [InlineData("1e3")]
    [InlineData("0x10")]
    public void Int_rejects_anything_that_is_not_a_whole_number(string raw)
    {
        var ex = Assert.Throws<CliUsageException>(() => Parse("--dpi", raw).Int(Dpi.Name));

        Assert.Contains("whole number", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Double_parses_a_decimal_and_returns_null_when_unused()
    {
        Assert.Equal(0.75, Parse("--dpi", "0.75").Double(Dpi.Name));
        Assert.Null(Parse().Double(Dpi.Name));
    }

    [Fact]
    public void Double_uses_the_invariant_culture_so_a_comma_is_not_a_decimal_point()
    {
        // "0,75" must not silently become 0.75 on a European machine — it is a usage error.
        Assert.Throws<CliUsageException>(() => Parse("--dpi", "0,75").Double(Dpi.Name));
    }

    [Fact]
    public void Int_uses_the_invariant_culture_so_group_separators_are_rejected()
    {
        Assert.Throws<CliUsageException>(() => Parse("--dpi", "1,200").Int(Dpi.Name));
    }

    [Fact]
    public void Numeric_accessors_read_the_last_value_of_a_repeated_option()
    {
        Assert.Equal(600, Parse("--dpi", "300", "--dpi", "600").Int(Dpi.Name));
    }

    // ---------------------------------------------------------------- metadata

    [Fact]
    public void Help_is_available_on_every_command_without_being_declared()
    {
        Assert.True(Parse("--help").Flag(CliCommand.HelpOption.Name));
        Assert.True(Parse("-h").Flag(CliCommand.HelpOption.Name));
    }

    [Fact]
    public void The_parsed_args_carry_the_command_for_error_attribution()
    {
        Assert.Same(Command, Parse().Command);
        Assert.Same(Command, Parse().Fail("boom").Command);
    }

    [Fact]
    public void Fail_produces_a_usage_exception_carrying_the_message()
    {
        Assert.Equal("boom", Parse().Fail("boom").Message);
    }

    // ---------------------------------------------------------------- help rendering

    [Fact]
    public void Help_syntax_renders_aliases_values_and_flags_distinctly()
    {
        Assert.Equal("-l, --lang <codes>", Lang.HelpSyntax);
        Assert.Equal("    --dpi <n>", Dpi.HelpSyntax);
        Assert.Equal("-r, --recursive", Recursive.HelpSyntax);
        Assert.Equal("--lang", Lang.LongForm);
    }

    [Fact]
    public void Help_text_lists_the_command_and_every_option_including_help()
    {
        string help = Command.HelpText();

        Assert.Contains("scan", help, StringComparison.Ordinal);
        Assert.Contains("--lang", help, StringComparison.Ordinal);
        Assert.Contains("--recursive", help, StringComparison.Ordinal);
        Assert.Contains("--help", help, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_declared_option_is_reachable_and_help_is_appended_once()
    {
        Assert.Equal(6, Command.AllOptions.Count);   // five declared + --help
        Assert.Contains(Command.AllOptions, o => o.Name == CliCommand.HelpOption.Name);
        Assert.Equal(
            Command.AllOptions.Select(o => o.Name).Distinct().Count(),
            Command.AllOptions.Count);
    }
}
