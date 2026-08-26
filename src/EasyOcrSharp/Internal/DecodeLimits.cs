using EasyImageSharp;
using EasyImageSharp.Formats;
using EasyImageSharp.PixelFormats;

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
            return await Image.LoadAsync<Rgb24>(path, For(maxPixels), ct).ConfigureAwait(false);
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
            return await Image.LoadAsync<Rgb24>(stream, For(maxPixels), ct).ConfigureAwait(false);
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
            return Image.Load<Rgb24>(bytes, For(maxPixels));
        }
        catch (ImageSizeLimitExceededException ex)
        {
            throw Restate(ex, optionName);
        }
    }

    private static ImageTooLargeException Restate(ImageSizeLimitExceededException inner, string optionName)
        => new(
            "The image decodes to more pixels than its header declared, exceeding the configured limit " +
            $"({optionName}). Raise the limit or downscale the image. This guard protects against " +
            $"decompression-bomb / pixel-flood denial of service. Decoder detail: {inner.Message}");
}
