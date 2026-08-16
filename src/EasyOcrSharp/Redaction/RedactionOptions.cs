using System.Text.RegularExpressions;
using EasyOcrSharp.Models;
using SixLabors.ImageSharp.PixelFormats;

namespace EasyOcrSharp.Redaction;

/// <summary>
/// How a matched region is obscured on the raster.
/// </summary>
public enum RedactionStyle
{
    /// <summary>
    /// Paint the region with a solid <see cref="RedactionOptions.FillColor"/>. The only style that is
    /// genuinely irreversible — the original pixels are gone, and nothing about them survives.
    /// </summary>
    FilledBox,

    /// <summary>
    /// Replace the region with a box-blurred version of itself. Visually softer, but blurring is a
    /// linear filter: with a known kernel the text can sometimes be recovered. Use it for
    /// "de-emphasize", never for secrets.
    /// </summary>
    Blur,

    /// <summary>
    /// Replace the region with block averages (mosaic). Like <see cref="Blur"/> this is a lossy but
    /// structured transform and is <b>not</b> a security control for short, guessable strings.
    /// </summary>
    Pixelate,
}

/// <summary>
/// How much of a matching line is obscured.
/// </summary>
public enum RedactionScope
{
    /// <summary>Obscure the whole line whenever anything in it matches. Always available.</summary>
    Line,

    /// <summary>
    /// Obscure only the words that overlap a match, leaving the rest of the sentence readable
    /// ("the account number in this sentence"). Requires per-word geometry, i.e.
    /// <see cref="RecognitionOptions.WordLevelDetail"/> of <see cref="WordLevelDetail.Words"/> or
    /// <see cref="WordLevelDetail.Characters"/>; when <see cref="OcrLine.Words"/> is empty — the
    /// default for every result produced without that opt-in — this silently degrades to
    /// <see cref="Line"/>, which redacts strictly more, never less.
    /// </summary>
    MatchedWords,
}

/// <summary>
/// A named detection rule: a regular expression plus an optional post-check that the matched text has
/// to survive. The post-check exists because the interesting identifiers are not regular languages —
/// a credit-card number is 13-19 digits <i>that pass Luhn</i>, an IBAN is an alphanumeric run <i>whose
/// mod-97 residue is 1</i>. Matching on the shape alone floods a document with false positives.
/// </summary>
/// <seealso cref="RedactionPatterns"/>
public sealed record RedactionRule
{
    /// <summary>
    /// Gets the rule's name, reported as <see cref="RedactionMatch.Rule"/> so an audit log can say
    /// which rule fired. Use something a human will recognize, e.g. <c>"CreditCard"</c>.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>Gets the regular expression that proposes candidate spans of a line's text.</summary>
    public required Regex Pattern { get; init; }

    /// <summary>
    /// Gets an optional validator applied to each candidate; the candidate is redacted only when it
    /// returns true. Null accepts every regex match. Must be pure and thread-safe: redaction evaluates
    /// lines without ordering guarantees.
    /// </summary>
    public Func<string, bool>? Validator { get; init; }

    /// <summary>Returns true when <paramref name="candidate"/> passes the <see cref="Validator"/> (or there is none).</summary>
    /// <param name="candidate">The text matched by <see cref="Pattern"/>.</param>
    public bool Accepts(string candidate) => Validator is null || Validator(candidate);
}

/// <summary>
/// Everything that governs a redaction pass: what counts as sensitive, how much of the line to cover,
/// and how the covering pixels are produced.
/// </summary>
/// <remarks>
/// The three selectors — <see cref="Rules"/>, <see cref="Patterns"/>, <see cref="Keywords"/> — plus the
/// <see cref="LinePredicate"/> escape hatch are combined with OR: a line is redacted when any of them
/// fires, and every hit is reported in <see cref="RedactedLine.Matches"/>. An options instance with no
/// selector at all matches nothing and returns the image unchanged.
/// </remarks>
public sealed record RedactionOptions
{
    /// <summary>
    /// Gets the validated detection rules to apply, normally taken from <see cref="RedactionPatterns"/>
    /// (e.g. <c>[RedactionPatterns.CreditCard, RedactionPatterns.Email]</c>). Prefer these over raw
    /// <see cref="Patterns"/> whenever a preset exists — they carry the check digit validation that a
    /// regular expression cannot express.
    /// </summary>
    public IReadOnlyList<RedactionRule> Rules { get; init; } = Array.Empty<RedactionRule>();

    /// <summary>
    /// Gets ad-hoc regular expressions; every match is redacted with no further validation. Supply a
    /// <see cref="Regex.MatchTimeout"/> when the patterns come from untrusted input — a pathological
    /// pattern is evaluated against every recognized line.
    /// </summary>
    public IReadOnlyList<Regex> Patterns { get; init; } = Array.Empty<Regex>();

    /// <summary>
    /// Gets literal substrings to redact (e.g. a customer name, <c>"CONFIDENTIAL"</c>). Matched with
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> unless <see cref="CaseSensitiveKeywords"/> is set.
    /// Overlapping occurrences of the same keyword are reported once each, left to right.
    /// </summary>
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets a value indicating whether <see cref="Keywords"/> are compared case-sensitively.
    /// Defaults to false (case-insensitive), which is what OCR output usually needs — recognizers
    /// routinely disagree with the page about letter case.
    /// </summary>
    public bool CaseSensitiveKeywords { get; init; }

    /// <summary>
    /// Gets an arbitrary per-line escape hatch — position on the page, confidence, length, anything the
    /// pattern languages cannot express (e.g. <c>l =&gt; l.BoundingBox.MinY &lt; 120</c> to redact a
    /// letterhead). Returning true redacts the whole line regardless of <see cref="Scope"/>, because a
    /// predicate has no notion of which characters offended. Must be pure and thread-safe.
    /// </summary>
    public Predicate<OcrLine>? LinePredicate { get; init; }

    /// <summary>Gets how the matched pixels are obscured. Defaults to <see cref="RedactionStyle.FilledBox"/>.</summary>
    public RedactionStyle Style { get; init; } = RedactionStyle.FilledBox;

    /// <summary>
    /// Gets the colour painted by <see cref="RedactionStyle.FilledBox"/>. Defaults to opaque black.
    /// </summary>
    public Rgb24 FillColor { get; init; } = new(0, 0, 0);

    /// <summary>
    /// Gets the box-blur radius in pixels used by <see cref="RedactionStyle.Blur"/>. Default 8. The
    /// radius has to be comparable to the stroke width to destroy legibility; a radius far smaller than
    /// the text height leaves the text readable.
    /// </summary>
    public int BlurRadius { get; init; } = 8;

    /// <summary>
    /// Gets the mosaic block size in pixels used by <see cref="RedactionStyle.Pixelate"/>. Default 12.
    /// Blocks are anchored to the redacted region's bounding box, so the mosaic never shifts with the
    /// image origin.
    /// </summary>
    public int PixelateBlockSize { get; init; } = 12;

    /// <summary>
    /// Gets the margin added around every redacted quad, as a fraction of that quad's height —
    /// 0.15 (the default) grows a 40px-tall line by 6px on each side. Padding matters: detector
    /// polygons hug the glyphs, so ascenders, descenders and diacritics routinely poke outside them.
    /// The margin is applied along the quad's own axes, so a rotated line stays rotated. 0 disables it.
    /// </summary>
    public double Padding { get; init; } = 0.15;

    /// <summary>
    /// Gets whether whole lines or only matched words are covered. Defaults to
    /// <see cref="RedactionScope.Line"/>; see <see cref="RedactionScope.MatchedWords"/> for the
    /// requirements and the automatic fallback.
    /// </summary>
    public RedactionScope Scope { get; init; } = RedactionScope.Line;

    /// <summary>
    /// Gets the recognition options for the OCR pass that precedes redaction. Defaults to
    /// <see cref="RecognitionOptions.Default"/>. When <see cref="Scope"/> is
    /// <see cref="RedactionScope.MatchedWords"/> and this still carries
    /// <see cref="WordLevelDetail.None"/>, the redaction pass raises it to
    /// <see cref="WordLevelDetail.Words"/> for its own OCR call — otherwise word-level scope could never
    /// do anything. Nothing else about the supplied options is altered, and the instance you pass in is
    /// never mutated.
    /// </summary>
    public RecognitionOptions Recognition { get; init; } = RecognitionOptions.Default;

    /// <summary>
    /// Gets the character used to mask matched text in <see cref="RedactionResult.SanitizedText"/>.
    /// Defaults to <c>'█'</c> (U+2588). The raster is unaffected by this — it only shapes the
    /// safe-to-log transcript.
    /// </summary>
    public char MaskCharacter { get; init; } = '█';

    /// <summary>
    /// Gets the pixel-count ceiling (width × height) for images loaded from a path, stream, or byte
    /// buffer by the redaction overloads, checked from the image header before the pixels are decoded.
    /// Default 100,000,000 (100 MP), matching <c>EasyOcrServiceOptions.MaxImagePixels</c>; set to 0 to
    /// disable. Already-decoded <see cref="SixLabors.ImageSharp.Image{TPixel}"/> inputs are the caller's
    /// own allocation and are never checked.
    /// </summary>
    public long MaxSourcePixels { get; init; } = 100_000_000;

    /// <summary>
    /// The default options: filled black boxes, whole-line scope, 15% padding, no selectors — add at
    /// least one of <see cref="Rules"/>, <see cref="Patterns"/>, <see cref="Keywords"/> or
    /// <see cref="LinePredicate"/> for anything to be redacted.
    /// </summary>
    public static RedactionOptions Default { get; } = new();

    /// <summary>True when no selector is configured, in which case the pass is a no-op.</summary>
    internal bool HasNoSelectors
        => Rules.Count == 0 && Patterns.Count == 0 && Keywords.Count == 0 && LinePredicate is null;

    /// <summary>The recognition options actually used, with word detail raised if the scope needs it.</summary>
    internal RecognitionOptions EffectiveRecognition
        => Scope == RedactionScope.MatchedWords && Recognition.WordLevelDetail == WordLevelDetail.None
            ? Recognition with { WordLevelDetail = WordLevelDetail.Words }
            : Recognition;

    internal void Validate()
    {
        if (BlurRadius < 1)
            throw new ArgumentOutOfRangeException(nameof(BlurRadius), BlurRadius, "BlurRadius must be at least 1 pixel.");
        if (PixelateBlockSize < 1)
            throw new ArgumentOutOfRangeException(nameof(PixelateBlockSize), PixelateBlockSize, "PixelateBlockSize must be at least 1 pixel.");
        if (double.IsNaN(Padding) || Padding < 0)
            throw new ArgumentOutOfRangeException(nameof(Padding), Padding, "Padding must be 0 or a positive fraction of the line height.");
        if (MaxSourcePixels < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxSourcePixels), MaxSourcePixels, "MaxSourcePixels must be 0 (unlimited) or positive.");
    }
}
