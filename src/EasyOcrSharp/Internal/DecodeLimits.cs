using EasyImageSharp;
using EasyImageSharp.Formats;
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
/// </summary>
internal static class DecodeLimits
{
    /// <summary>No ceiling — what "set to 0 to disable" has to mean once the budget reaches the decoder.</summary>
    private static readonly DecoderOptions Unlimited = new() { MaxPixels = long.MaxValue };

    /// <summary>Decoder limits for a pixel budget; 0 (or less) means unlimited.</summary>
    internal static DecoderOptions For(long maxPixels)
        => maxPixels > 0 ? new DecoderOptions { MaxPixels = maxPixels } : Unlimited;

    public static async Task<Image<Rgb24>> LoadAsync(string path, long maxPixels, string optionName, CancellationToken ct)
    {
        try
        {
            using var decoded = await Image.LoadAsync<Rgba32>(path, For(maxPixels), ct).ConfigureAwait(false);
            return Normalize(decoded);
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
            using var decoded = await Image.LoadAsync<Rgba32>(stream, For(maxPixels), ct).ConfigureAwait(false);
            return Normalize(decoded);
        }
        catch (ImageSizeLimitExceededException ex)
        {
            throw Restate(ex, optionName);
        }
    }

    public static Image<Rgb24> Load(ReadOnlySpan<byte> bytes, long maxPixels, string optionName)
    {
        try
        {
            using var decoded = Image.Load<Rgba32>(bytes, For(maxPixels));
            return Normalize(decoded);
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
    /// </summary>
    private static Image<Rgb24> Normalize(Image<Rgba32> decoded)
    {
        decoded.Mutate(c => c.AutoOrient().BackgroundColor(Color.White));
        return decoded.CloneAs<Rgb24>();
    }

    private static ImageTooLargeException Restate(ImageSizeLimitExceededException inner, string optionName)
        => new(
            "The image decodes to more pixels than its header declared, exceeding the configured limit " +
            $"({optionName}). Raise the limit or downscale the image. This guard protects against " +
            $"decompression-bomb / pixel-flood denial of service. Decoder detail: {inner.Message}");
}
