using EasyOcrSharp.Cli.CommandLine;
using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Services;

namespace EasyOcrSharp.Cli.Internal;

/// <summary>
/// Translates parsed command-line arguments into the library's option records. Every unrecognized
/// value fails with a usage error that lists the accepted spellings, so a typo never silently turns
/// into a default.
/// </summary>
internal static class OptionBinder
{
    /// <summary>
    /// The languages to recognize: <c>--lang</c> values, falling back to English so the most common
    /// invocation (<c>easyocrsharp scan page.png</c>) needs no flags at all.
    /// </summary>
    public static string[] Languages(ParsedArgs args)
    {
        var langs = args.List(CommonOptions.Lang.Name);
        return langs.Count > 0 ? [.. langs] : ["en"];
    }

    /// <summary>Builds the service configuration (provider, cache, offline mode, download progress).</summary>
    public static EasyOcrServiceOptions BuildServiceOptions(ParsedArgs args, CliConsole console)
    {
        bool gpu = args.Flag(CommonOptions.Gpu.Name);
        bool cpu = args.Flag(CommonOptions.Cpu.Name);
        if (gpu && cpu) throw args.Fail("--gpu and --cpu are mutually exclusive.");

        var options = new EasyOcrServiceOptions
        {
            ModelCachePath = args.Value(CommonOptions.Cache.Name),
            ExecutionProvider = gpu ? OcrExecutionProvider.Cuda
                : cpu ? OcrExecutionProvider.Cpu
                : OcrExecutionProvider.Auto,
        };

        options.Download.Offline = args.Flag(CommonOptions.Offline.Name);
        if (console.ProgressEnabled)
        {
            options.Download.Progress = new DownloadProgressReporter(console);
        }

        return options;
    }

    /// <summary>Builds the recognition options: grouping, filters, decoder, preprocessing, detail.</summary>
    public static RecognitionOptions BuildRecognitionOptions(ParsedArgs args, ScanFlags flags)
    {
        var (decoder, beamWidth) = Decoder(args);

        return new RecognitionOptions
        {
            Grouping = Grouping(args, flags),
            Preprocessing = Preprocessing(args),
            Decoder = decoder,
            BeamWidth = beamWidth,
            WordLevelDetail = Detail(args),
            Allowlist = args.Value(CommonOptions.Allowlist.Name),
            Blocklist = args.Value(CommonOptions.Blocklist.Name),
            MinConfidence = MinConfidence(args),
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        };
    }

    /// <summary>Builds the PDF rasterization options, honoring <c>--dpi</c>.</summary>
    public static PdfOcrOptions BuildPdfOptions(ParsedArgs args, IProgress<PdfPageProgress>? progress = null)
    {
        var options = new PdfOcrOptions();
        if (args.Int(CommonOptions.Dpi.Name) is { } dpi)
        {
            if (dpi is < 36 or > 1200) throw args.Fail("--dpi must be between 36 and 1200.");
            options.Dpi = dpi;
        }
        options.Progress = progress;
        return options;
    }

    /// <summary>Which of the mutually exclusive grouping flags was requested.</summary>
    private static TextGrouping Grouping(ParsedArgs args, ScanFlags flags)
    {
        int chosen = (args.Flag(flags.Paragraph.Name) ? 1 : 0)
            + (args.Flag(flags.Line.Name) ? 1 : 0)
            + (args.Flag(flags.Word.Name) ? 1 : 0);
        if (chosen > 1)
            throw args.Fail($"--{flags.Paragraph.Name}, --{flags.Line.Name} and --{flags.Word.Name} are mutually exclusive.");

        if (args.Flag(flags.Paragraph.Name)) return TextGrouping.Paragraph;
        if (args.Flag(flags.Word.Name)) return TextGrouping.Word;
        return TextGrouping.Line;
    }

    private static double MinConfidence(ParsedArgs args)
    {
        if (args.Double(CommonOptions.MinConfidence.Name) is not { } value) return 0;
        if (value is < 0 or > 1) throw args.Fail("--min-confidence must be between 0 and 1.");
        return value;
    }

    private static (DecoderType Decoder, int BeamWidth) Decoder(ParsedArgs args)
    {
        var decoder = args.Value(CommonOptions.Decoder.Name)?.ToLowerInvariant() switch
        {
            null or "greedy" => DecoderType.Greedy,
            "beam" or "beamsearch" or "beam-search" => DecoderType.BeamSearch,
            "wordbeam" or "word-beam" or "wordbeamsearch" => DecoderType.WordBeamSearch,
            var other => throw args.Fail($"Unknown decoder '{other}'. Expected: greedy, beam, wordbeam."),
        };

        int beamWidth = args.Int(CommonOptions.BeamWidth.Name) ?? 5;
        if (beamWidth < 1) throw args.Fail("--beam-width must be at least 1.");
        return (decoder, beamWidth);
    }

    private static WordLevelDetail Detail(ParsedArgs args) =>
        args.Value(CommonOptions.Detail.Name)?.ToLowerInvariant() switch
        {
            null or "none" or "line" => WordLevelDetail.None,
            "word" or "words" => WordLevelDetail.Words,
            "char" or "chars" or "character" or "characters" => WordLevelDetail.Characters,
            var other => throw args.Fail($"Unknown --detail '{other}'. Expected: none, words, chars."),
        };

    private static PreprocessingOptions Preprocessing(ParsedArgs args)
    {
        var steps = args.List(CommonOptions.Preprocess.Name);
        if (steps.Count == 0) return PreprocessingOptions.None;

        var options = PreprocessingOptions.None;
        foreach (var step in steps)
        {
            options = step.ToLowerInvariant() switch
            {
                "deskew" => options with { Deskew = true },
                "binarize" or "binarise" or "threshold" => options with { Binarize = true },
                "denoise" => options with { Denoise = true },
                "sharpen" => options with { Sharpen = true },
                // The cheap single-pass PP-LCNet page-orientation classifier.
                "orientation" => options with { DocumentOrientation = true },
                // The brute-force four-way OCR sweep; ~4x slower but needs no extra model.
                "rotate" or "detect-orientation" => options with { DetectOrientation = true },
                "unwarp" or "dewarp" => options with { DocumentUnwarp = true },
                _ => throw args.Fail(
                    $"Unknown preprocessing step '{step}'. Expected any of: " +
                    "deskew, binarize, denoise, sharpen, orientation, rotate, unwarp."),
            };
        }
        return options;
    }
}

/// <summary>
/// The three mutually exclusive grouping flags, passed to the binder so the command that owns them
/// keeps their help text next to the rest of its own options.
/// </summary>
/// <param name="Paragraph">The <c>--paragraph</c> flag.</param>
/// <param name="Line">The <c>--line</c> flag.</param>
/// <param name="Word">The <c>--word</c> flag.</param>
internal sealed record ScanFlags(CliOption Paragraph, CliOption Line, CliOption Word);
