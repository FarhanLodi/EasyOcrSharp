using System.Collections.ObjectModel;
using System.Diagnostics;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Extraction;

/// <summary>
/// Zone (template) OCR for <see cref="IEasyOcrService"/>: read the named rectangles of a fixed-layout form
/// (invoice number, date, total, MRZ, …) in one call, each with its own mode and allowlist.
/// </summary>
/// <remarks>
/// <para>
/// No new engine code: a <see cref="ZoneMode.Detect"/> zone is an ordinary
/// <see cref="IEasyOcrService.ExtractTextFromImage(Image{Rgb24}, IEnumerable{string}, RecognitionOptions?, CancellationToken)"/>
/// call with <see cref="RecognitionOptions.Region"/> set, and a <see cref="ZoneMode.SingleLine"/> zone is
/// one <see cref="IEasyOcrService.RecognizeRegionsAsync(Image{Rgb24}, IEnumerable{IReadOnlyList{OcrPoint}}, IEnumerable{string}, RecognitionOptions?, CancellationToken)"/>
/// call over the zone rectangle. Every returned box is therefore in the full image's coordinates, and the
/// service's own concurrency limits, timeouts and metrics apply to each zone.
/// </para>
/// <para>
/// Zones are read sequentially, in the order given. A custom <see cref="IEasyOcrService"/> that does not
/// implement <c>RecognizeRegionsAsync</c> can still read <see cref="ZoneMode.Detect"/> zones.
/// </para>
/// </remarks>
public static class ZoneOcrExtensions
{
    /// <summary>
    /// Pixel budget for the file-path overload, matching <see cref="EasyOcrServiceOptions.MaxImagePixels"/>'s
    /// default. Decode larger pages yourself and use the <see cref="Image{TPixel}"/> overload.
    /// </summary>
    private const long MaxImagePixels = 100_000_000;

    private const string MaxPixelsOption =
        "RecognizeZonesAsync(imagePath) 100 MP budget; decode the image yourself and call the Image<Rgb24> overload for larger pages";

    /// <summary>
    /// Reads each zone of an already-decoded image. The caller keeps ownership of the image. A zone that
    /// resolves to an empty rectangle (smaller than 2×2 px after clamping) yields an empty result without
    /// calling the service.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="image">The image to read.</param>
    /// <param name="zones">The zones, with unique names.</param>
    /// <param name="languages">The recognition languages (same as the single-image OCR API).</param>
    /// <param name="options">
    /// Base recognition options; each zone overrides <see cref="RecognitionOptions.Region"/> and, when the
    /// zone sets one, <see cref="RecognitionOptions.Allowlist"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The per-zone results in zone order.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A zone is <see langword="null"/>, unnamed, duplicated, has an unknown mode or a non-finite region.</exception>
    public static async Task<ZoneOcrResult> RecognizeZonesAsync(
        this IEasyOcrService service,
        Image<Rgb24> image,
        IReadOnlyList<OcrZone> zones,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(languages);
        ValidateZones(zones);

        var langs = languages.ToArray();
        var baseOptions = options ?? RecognitionOptions.Default;
        var stopwatch = Stopwatch.StartNew();
        var results = new OrderedDictionary<string, OcrZoneResult>(zones.Count, StringComparer.Ordinal);
        foreach (var zone in zones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var zoneResult = await RecognizeZoneAsync(service, image, zone, langs, baseOptions, cancellationToken).ConfigureAwait(false);
            results.Add(zone.Name, zoneResult);
        }

        return new ZoneOcrResult
        {
            Zones = new ReadOnlyDictionary<string, OcrZoneResult>(results),
            Duration = stopwatch.Elapsed,
            SourceWidth = image.Width,
            SourceHeight = image.Height,
        };
    }

    /// <summary>
    /// Reads each zone of an image file. The image is decoded once (EXIF orientation applied, transparency
    /// composited on white, as the service does) and every zone is read from it.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imagePath">Path to the image file.</param>
    /// <param name="zones">The zones, with unique names.</param>
    /// <param name="languages">The recognition languages.</param>
    /// <param name="options">Base recognition options; each zone overrides the region and (when set) the allowlist.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The per-zone results in zone order.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The path is blank, or a zone is invalid (see the image overload).</exception>
    /// <exception cref="FileNotFoundException">The image file does not exist.</exception>
    /// <exception cref="ImageTooLargeException">The image exceeds 100 megapixels.</exception>
    public static async Task<ZoneOcrResult> RecognizeZonesAsync(
        this IEasyOcrService service,
        string imagePath,
        IReadOnlyList<OcrZone> zones,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(languages);
        ValidateZones(zones);

        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"The image file '{fullPath}' could not be found.", fullPath);

        var info = await Image.IdentifyAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if ((long)info.Width * info.Height > MaxImagePixels)
        {
            throw new ImageTooLargeException(
                $"The image is {info.Width}x{info.Height} ({(long)info.Width * info.Height:N0} pixels), exceeding the " +
                $"{MaxImagePixels:N0}-pixel limit of {nameof(RecognizeZonesAsync)}(imagePath). Decode it yourself and " +
                "call the Image<Rgb24> overload to read larger pages.");
        }

        using var image = await DecodeLimits.LoadAsync(fullPath, MaxImagePixels, MaxPixelsOption, cancellationToken).ConfigureAwait(false);
        return await RecognizeZonesAsync(service, image, zones, languages, options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OcrZoneResult> RecognizeZoneAsync(
        IEasyOcrService service,
        Image<Rgb24> image,
        OcrZone zone,
        string[] languages,
        RecognitionOptions baseOptions,
        CancellationToken cancellationToken)
    {
        var allowlist = zone.Allowlist ?? baseOptions.Allowlist;
        var (x, y, width, height) = zone.Region.Resolve(image.Width, image.Height);
        var bounds = width > 0 && height > 0
            ? new OcrBoundingBox(x, y, x + width, y + height)
            : OcrBoundingBox.Empty;

        OcrResult result;
        if (width < 2 || height < 2)
        {
            result = OcrResult.Empty with
            {
                Languages = languages,
                SourceWidth = image.Width,
                SourceHeight = image.Height,
            };
        }
        else if (zone.Mode == ZoneMode.SingleLine)
        {
            IReadOnlyList<OcrPoint> polygon = new OcrPoint[]
            {
                new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height),
            };
            var zoneOptions = baseOptions with { Region = null, Allowlist = allowlist };
            result = await service.RecognizeRegionsAsync(image, new[] { polygon }, languages, zoneOptions, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var zoneOptions = baseOptions with { Region = zone.Region, Allowlist = allowlist };
            result = await service.ExtractTextFromImage(image, languages, zoneOptions, cancellationToken).ConfigureAwait(false);
        }

        var lines = result.Lines;
        string text = zone.Mode == ZoneMode.SingleLine
            ? string.Join(' ', lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0))
            : TextGeometry.JoinReadingOrder(lines);

        return new OcrZoneResult
        {
            Name = zone.Name,
            Zone = zone,
            PixelBounds = bounds,
            Text = text,
            Confidence = TextGeometry.WeightedConfidence(lines),
            Lines = lines,
            Result = result,
        };
    }

    private static void ValidateZones(IReadOnlyList<OcrZone> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var zone in zones)
        {
            if (zone is null)
                throw new ArgumentException("Zones must not contain null entries.", nameof(zones));
            if (string.IsNullOrWhiteSpace(zone.Name))
                throw new ArgumentException("Every zone needs a non-blank name.", nameof(zones));
            if (!names.Add(zone.Name))
                throw new ArgumentException($"Duplicate zone name '{zone.Name}'.", nameof(zones));
            if (!Enum.IsDefined(zone.Mode))
                throw new ArgumentException($"Zone '{zone.Name}' has an unknown mode '{zone.Mode}'.", nameof(zones));

            var r = zone.Region;
            if (!double.IsFinite(r.X) || !double.IsFinite(r.Y) || !double.IsFinite(r.Width) || !double.IsFinite(r.Height))
                throw new ArgumentException($"Zone '{zone.Name}' has a non-finite region.", nameof(zones));
        }
    }
}
