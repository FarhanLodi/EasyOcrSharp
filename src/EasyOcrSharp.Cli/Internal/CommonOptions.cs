using EasyOcrSharp.Cli.CommandLine;

namespace EasyOcrSharp.Cli.Internal;

/// <summary>
/// Option definitions shared by more than one command. Declaring them once keeps the spelling, the
/// short alias and the help wording identical everywhere they appear, which is what makes a
/// multi-command tool feel like one program rather than four.
/// </summary>
internal static class CommonOptions
{
    /// <summary>Languages to recognize.</summary>
    public static readonly CliOption Lang = new(
        "lang", "-l", true,
        "Language codes to recognize, comma-separated (default: en). Repeatable.",
        "codes", Repeatable: true);

    /// <summary>Request GPU acceleration.</summary>
    public static readonly CliOption Gpu = new(
        "gpu", null, false,
        "Force the CUDA execution provider (needs the EasyOcrSharp.Gpu package). Default: auto-detect.");

    /// <summary>Force CPU execution.</summary>
    public static readonly CliOption Cpu = new(
        "cpu", null, false,
        "Force CPU execution even when an accelerator is available.");

    /// <summary>Override the model cache directory.</summary>
    public static readonly CliOption Cache = new(
        "cache", null, true,
        "Model cache directory (default: $EASYOCRSHARP_CACHE or the per-user cache).",
        "dir");

    /// <summary>Refuse to download anything.</summary>
    public static readonly CliOption Offline = new(
        "offline", null, false,
        "Never download: a model missing from the cache is a hard error (air-gapped hosts).");

    /// <summary>Suppress progress and informational output.</summary>
    public static readonly CliOption Quiet = new(
        "quiet", "-q", false,
        "Suppress progress and informational output on stderr. Errors are still reported.");

    /// <summary>Rasterization DPI for PDF input.</summary>
    public static readonly CliOption Dpi = new(
        "dpi", null, true,
        "Rasterization resolution for PDF input, 36-1200 (default: 200).",
        "n");

    /// <summary>
    /// Pre-OCR image clean-up steps. Repeatable, so <c>--preprocess deskew --preprocess sharpen</c>
    /// means the same as <c>--preprocess deskew,sharpen</c> — writing it twice adds a step rather than
    /// silently discarding the first one.
    /// </summary>
    public static readonly CliOption Preprocess = new(
        "preprocess", null, true,
        "Comma-separated clean-up: deskew,binarize,denoise,sharpen,orientation,unwarp,rotate. Repeatable.",
        "steps", Repeatable: true);

    /// <summary>Restrict the recognizable character set.</summary>
    public static readonly CliOption Allowlist = new(
        "allowlist", null, true,
        "Only ever emit these characters (e.g. 0123456789). Sharpens constrained fields.",
        "chars");

    /// <summary>Forbid characters from the output.</summary>
    public static readonly CliOption Blocklist = new(
        "blocklist", null, true,
        "Never emit these characters. Ignored when --allowlist is set.",
        "chars");

    /// <summary>Drop low-confidence lines.</summary>
    public static readonly CliOption MinConfidence = new(
        "min-confidence", null, true,
        "Drop recognized lines below this confidence, 0-1 (default: 0, keep all).",
        "0-1");

    /// <summary>CTC decoder selection.</summary>
    public static readonly CliOption Decoder = new(
        "decoder", null, true,
        "CTC decoder: greedy (default), beam, wordbeam.",
        "name");

    /// <summary>Beam width for the beam-search decoders.</summary>
    public static readonly CliOption BeamWidth = new(
        "beam-width", null, true,
        "Beam width for --decoder beam/wordbeam (default: 5).",
        "n");

    /// <summary>Sub-line geometry detail.</summary>
    public static readonly CliOption Detail = new(
        "detail", null, true,
        "Sub-line geometry: none (default), words, chars. Gives hOCR/ALTO/TSV true word boxes.",
        "level");
}
