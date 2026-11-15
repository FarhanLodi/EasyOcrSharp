using EasyOcrSharp.Models;

namespace EasyOcrSharp.Extraction;

/// <summary>
/// How an <see cref="OcrZone"/> is read.
/// </summary>
public enum ZoneMode
{
    /// <summary>
    /// Run normal detection + recognition restricted to the zone (via
    /// <see cref="RecognitionOptions.Region"/>). Suits zones that may hold several lines or words.
    /// </summary>
    Detect = 0,

    /// <summary>
    /// Skip detection and recognize the whole zone rectangle as a single text line (via
    /// <see cref="Services.IEasyOcrService.RecognizeRegionsAsync(EasyImageSharp.Image{EasyImageSharp.PixelFormats.Rgb24}, IEnumerable{IReadOnlyList{OcrPoint}}, IEnumerable{string}, RecognitionOptions?, CancellationToken)"/>,
    /// EasyOCR's <c>recognize()</c>). Suits tight single-line fields such as a date, an amount box or an
    /// MRZ line, where detection might miss faint or short text. Draw the rectangle snugly around the text:
    /// the whole rectangle is resized to the recognizer's line height.
    /// </summary>
    SingleLine = 1,
}

/// <summary>
/// A named field of a fixed-layout form or template, read by
/// <see cref="ZoneOcrExtensions.RecognizeZonesAsync(Services.IEasyOcrService, EasyImageSharp.Image{EasyImageSharp.PixelFormats.Rgb24}, IReadOnlyList{OcrZone}, IEnumerable{string}, RecognitionOptions?, CancellationToken)"/>.
/// </summary>
/// <param name="Name">Unique name of the zone (ordinal comparison); the key in <see cref="ZoneOcrResult.Zones"/>.</param>
/// <param name="Region">
/// The zone rectangle, in pixels (<see cref="OcrRegion.Pixels"/>) or as fractions of the image size
/// (<see cref="OcrRegion.Fraction"/>) — fractions keep one template working across scan resolutions.
/// Clamped to the image.
/// </param>
/// <param name="Mode">How the zone is read. Defaults to <see cref="ZoneMode.Detect"/>.</param>
/// <param name="Allowlist">
/// Optional per-zone recognizer allowlist (same meaning as <see cref="RecognitionOptions.Allowlist"/>,
/// e.g. <c>"0123456789.,"</c> for an amount box). <see langword="null"/> keeps the call's options.
/// </param>
public sealed record OcrZone(
    string Name,
    OcrRegion Region,
    ZoneMode Mode = ZoneMode.Detect,
    string? Allowlist = null);

/// <summary>
/// The text read from one <see cref="OcrZone"/>.
/// </summary>
public sealed record OcrZoneResult
{
    /// <summary>The zone's name.</summary>
    public required string Name { get; init; }

    /// <summary>The zone definition that was read.</summary>
    public required OcrZone Zone { get; init; }

    /// <summary>
    /// The zone rectangle resolved to integer pixels of the source image (clamped to its bounds).
    /// Empty when the zone fell entirely outside the image.
    /// </summary>
    public OcrBoundingBox PixelBounds { get; init; }

    /// <summary>
    /// The zone's text in reading order: rows separated by <c>\n</c>, lines within a row by a space
    /// (<see cref="ZoneMode.SingleLine"/> zones are joined into one line). Empty when nothing was read.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>Character-weighted mean confidence (0–1) of the zone's lines; 0 when nothing was read.</summary>
    public double Confidence { get; init; }

    /// <summary>The recognized lines, with boxes in the full image's coordinates.</summary>
    public required IReadOnlyList<OcrLine> Lines { get; init; }

    /// <summary>The underlying OCR result for the zone.</summary>
    public required OcrResult Result { get; init; }
}

/// <summary>
/// The result of reading a set of <see cref="OcrZone"/>s from one image.
/// </summary>
public sealed record ZoneOcrResult
{
    /// <summary>
    /// The per-zone results keyed by zone name (ordinal), enumerated in the order the zones were supplied.
    /// </summary>
    public required IReadOnlyDictionary<string, OcrZoneResult> Zones { get; init; }

    /// <summary>Gets the result for the zone named <paramref name="name"/>.</summary>
    /// <param name="name">The zone name.</param>
    /// <exception cref="KeyNotFoundException">No zone has that name.</exception>
    public OcrZoneResult this[string name] => Zones[name];

    /// <summary>Total time spent reading all zones.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Width (px) of the image the zones were read from.</summary>
    public int SourceWidth { get; init; }

    /// <summary>Height (px) of the image the zones were read from.</summary>
    public int SourceHeight { get; init; }

    /// <summary>
    /// The zone texts keyed by zone name, in zone order — the shape a form-filling or JSON-serializing
    /// caller usually wants.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToValueMap()
    {
        var map = new OrderedDictionary<string, string>(Zones.Count, StringComparer.Ordinal);
        foreach (var (name, zone) in Zones) map.Add(name, zone.Text);
        return map;
    }
}
