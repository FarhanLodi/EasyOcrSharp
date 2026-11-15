using EasyImageSharp;
using EasyImageSharp.Formats;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Carries this library's pixel budgets — <c>EasyOcrServiceOptions.MaxImagePixels</c>,
/// <c>BarcodeOptions.MaxImagePixels</c>, <c>RedactionOptions.MaxSourcePixels</c>,
/// <c>MultiFrameOcrOptions.MaxFrameMegapixels</c> — down into the decoder.
/// <para>
/// Every load path already inspects the header first and rejects an oversized image with
/// <see cref="ImageTooLargeException"/> naming the option to raise. Handing the same budget to the
/// decoder does two further things. It closes the gap the header check cannot cover — a container whose
/// header under-reports what it actually decodes, a multi-page TIFF being the obvious case. And it
/// replaces the decoder's own default ceiling, which would otherwise quietly cap a caller who raised
/// their budget above it or set it to 0 to turn the guard off.
/// </para>
/// <para>
/// A rejection that does come from the decoder is restated as <see cref="ImageTooLargeException"/>, so
/// callers see one exception type and one actionable message whichever check fired.
/// </para>
/// <para>
/// The request's <see cref="CancellationToken"/> travels with the limits, as
/// <see cref="DecoderOptions.ExecutionOptions"/>, so every entry point — path, stream, encoded bytes, and
/// the all-frames decode behind the multi-frame API — stops at EasyImageSharp's decode checkpoints instead
/// of finishing a large TIFF or JPEG nobody is waiting for any more. A canceled decode surfaces as
/// <see cref="OperationCanceledException"/>: only <see cref="ImageSizeLimitExceededException"/> is restated,
/// so a cancellation is never reported as an oversized image. A token that cannot be canceled leaves the
/// options exactly as they were before cancellation was wired in.
/// </para>
/// </summary>
internal static class DecodeLimits
{
    /// <summary>No ceiling — what "set to 0 to disable" has to mean once the budget reaches the decoder.</summary>
    private static readonly DecoderOptions Unlimited = new() { MaxPixels = long.MaxValue, MaxTotalPixels = long.MaxValue };

    /// <summary>Decoder limits for a pixel budget; 0 (or less) means unlimited.</summary>
    /// <param name="maxPixels">Per-frame pixel budget; 0 (or less) lifts the ceiling entirely.</param>
    /// <param name="ct">
    /// The request's token, handed to the decoder as request-local execution options so decoding stops at
    /// its checkpoints once the request is canceled. <see cref="CancellationToken.None"/> (or any token that
    /// cannot be canceled) adds nothing.
    /// </param>
    /// <remarks>
    /// <c>MaxTotalPixels</c> is lifted in both branches. EasyImageSharp 1.1.0 introduced it with a default of
    /// 2^30 pixels summed over every frame, which would silently cap a multi-page TIFF that the caller's own
    /// budget allows (a few hundred A4 pages at 300 dpi), even with the guard set to 0. Frames are already
    /// bounded here: per frame by <paramref name="maxPixels"/>, and by count through
    /// <c>MultiFrameOcrOptions.MaxFrames</c> checked against the header before decoding.
    /// </remarks>
    internal static DecoderOptions For(long maxPixels, CancellationToken ct = default)
    {
        var execution = ExecutionFor(ct);
        if (execution is null)
            return maxPixels > 0 ? new DecoderOptions { MaxPixels = maxPixels, MaxTotalPixels = long.MaxValue } : Unlimited;

        return maxPixels > 0
            ? new DecoderOptions { MaxPixels = maxPixels, MaxTotalPixels = long.MaxValue, ExecutionOptions = execution }
            : new DecoderOptions { MaxPixels = long.MaxValue, MaxTotalPixels = long.MaxValue, ExecutionOptions = execution };
    }

    /// <summary>
    /// Options for header-only inspection (<c>Image.Identify</c>, which ignores pixel limits): the library
    /// defaults plus the request's token. <see cref="DecoderOptions.Default"/> when the token cannot be canceled.
    /// </summary>
    internal static DecoderOptions ForHeader(CancellationToken ct)
        => ExecutionFor(ct) is { } execution ? new DecoderOptions { ExecutionOptions = execution } : DecoderOptions.Default;

    /// <summary>Request-local execution options carrying <paramref name="ct"/>; null when it cannot be canceled.</summary>
    private static ImageExecutionOptions? ExecutionFor(CancellationToken ct)
        => ct.CanBeCanceled ? new ImageExecutionOptions { CancellationToken = ct } : null;

    /// <summary>
    /// Reads the header of encoded <paramref name="bytes"/> (dimensions, format, frame count) without
    /// decoding pixels, observing <paramref name="ct"/>. A pre-canceled token throws
    /// <see cref="OperationCanceledException"/> before the header is parsed.
    /// </summary>
    public static ImageInfo Identify(ReadOnlySpan<byte> bytes, CancellationToken ct = default)
        => Image.Identify(bytes, ForHeader(ct));

    public static async Task<Image<Rgb24>> LoadAsync(string path, long maxPixels, string optionName, CancellationToken ct)
    {
        try
        {
            using var decoded = await Image.LoadAsync<Rgba32>(path, For(maxPixels, ct), ct).ConfigureAwait(false);
            return Normalize(decoded, ct);
        }
        catch (ImageSizeLimitExceededException ex)
        {
            throw Restate(ex, optionName);
        }
    }

    public static async Task<Image<Rgb24>> LoadAsync(Stream stream, long maxPixels, string optionName, CancellationToken ct)
    {
        try
        {
            using var decoded = await Image.LoadAsync<Rgba32>(stream, For(maxPixels, ct), ct).ConfigureAwait(false);
            return Normalize(decoded, ct);
        }
        catch (ImageSizeLimitExceededException ex)
        {
            throw Restate(ex, optionName);
        }
    }

    /// <summary>
    /// Decodes encoded <paramref name="bytes"/> under the pixel budget. <paramref name="ct"/> is observed at
    /// the decoder's checkpoints (and up front: a pre-canceled token never starts decoding).
    /// </summary>
    public static Image<Rgb24> Load(ReadOnlySpan<byte> bytes, long maxPixels, string optionName, CancellationToken ct = default)
    {
        try
        {
            using var decoded = Image.Load<Rgba32>(bytes, For(maxPixels, ct));
            return Normalize(decoded, ct);
        }
        catch (ImageSizeLimitExceededException ex)
        {
            throw Restate(ex, optionName);
        }
    }

    /// <summary>
    /// Turns a freshly decoded image into the upright, opaque RGB buffer the models expect.
    /// <para>
    /// Two steps, both of which the pipeline previously skipped by decoding straight to
    /// <see cref="Rgb24"/>:
    /// </para>
    /// <para>
    /// <b>EXIF orientation.</b> A phone photo of a receipt is normally stored landscape with an
    /// <c>Orientation</c> tag of 6; every viewer shows it upright, but the raw buffer is sideways and the
    /// detector finds almost nothing. <c>AutoOrient</c> applies the tag and resets it — a no-op when there is
    /// no EXIF profile or the orientation is already 1, and lossless for the 90° cases.
    /// </para>
    /// <para>
    /// <b>Alpha compositing.</b> Converting RGBA to RGB discards alpha rather than compositing it, so a
    /// transparent background (a logo, an exported diagram, a screenshot with transparency) becomes
    /// <c>(0,0,0)</c> — dark glyphs on a black page, which returns empty text with no error.
    /// </para>
    /// <para>
    /// Both steps are applied <b>in place, to every frame</b>, and the RGB image is then produced with
    /// <c>CloneAs</c>. That matters: building a fresh single-frame <c>Image&lt;Rgb24&gt;</c> and drawing onto it
    /// would silently flatten a multi-frame TIFF to its first page, breaking the multi-frame API.
    /// </para>
    /// <para>
    /// A cancelable <paramref name="ct"/> runs the two steps under the same request-local execution options
    /// as the decode, so a canceled request also stops here; the pixels produced are unchanged.
    /// </para>
    /// </summary>
    private static Image<Rgb24> Normalize(Image<Rgba32> decoded, CancellationToken ct)
    {
        bool transposes = TransposesAxes(decoded);
        if (ExecutionFor(ct) is { } execution)
            decoded.Mutate(execution, c => c.AutoOrient().BackgroundColor(Color.White));
        else
            decoded.Mutate(c => c.AutoOrient().BackgroundColor(Color.White));
        if (transposes) SwapResolutionAxes(decoded);
        return decoded.CloneAs<Rgb24>();
    }

    /// <summary>
    /// True when the image's EXIF orientation (5–8) makes <c>AutoOrient</c> swap its width and height.
    /// </summary>
    private static bool TransposesAxes(Image<Rgba32> image)
        => image.Metadata.ExifProfile is { } exif
        && exif.TryGetValue(ExifTag.Orientation, out var orientation)
        && orientation is not null
        && orientation.Value is >= 5 and <= 8;

    /// <summary>
    /// <c>AutoOrient</c> turns the pixels a quarter but leaves the resolution metadata describing the stored
    /// axes. Swapping it keeps the horizontal resolution describing the new horizontal axis, which is what
    /// non-square-pixel correction reads (<c>PreprocessingOptions.CorrectNonSquarePixels</c>): a sideways
    /// 204×98 DPI fax would otherwise be stretched along the wrong axis. Square-pixel images are unaffected.
    /// </summary>
    internal static void SwapResolutionAxes<TPixel>(Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var metadata = image.Metadata;
        double horizontal = metadata.HorizontalResolution;
        metadata.HorizontalResolution = metadata.VerticalResolution;
        metadata.VerticalResolution = horizontal;

        // Each distinct profile is swapped once, even if a decoder shares one between the image and a frame.
        var swapped = new HashSet<ExifProfile>(ReferenceEqualityComparer.Instance);
        SwapExifResolution(metadata.ExifProfile, swapped);
        foreach (var frame in image.Frames)
        {
            SwapExifResolution(frame.Metadata.ExifProfile, swapped);
        }
    }

    private static void SwapExifResolution(ExifProfile? exif, HashSet<ExifProfile> swapped)
    {
        if (exif is not null && swapped.Add(exif)
            && exif.TryGetValue(ExifTag.XResolution, out var x) && x is not null
            && exif.TryGetValue(ExifTag.YResolution, out var y) && y is not null)
        {
            (x.Value, y.Value) = (y.Value, x.Value);
        }
    }

    private static ImageTooLargeException Restate(ImageSizeLimitExceededException inner, string optionName)
        => new(
            "The image decodes to more pixels than its header declared, exceeding the configured limit " +
            $"({optionName}). Raise the limit or downscale the image. This guard protects against " +
            $"decompression-bomb / pixel-flood denial of service. Decoder detail: {inner.Message}");
}
