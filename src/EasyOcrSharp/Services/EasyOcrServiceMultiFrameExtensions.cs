using System.Diagnostics;
using System.Runtime.CompilerServices;
using EasyOcrSharp.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace EasyOcrSharp.Services;

/// <summary>
/// Multi-frame image helpers for <see cref="IEasyOcrService"/> — OCR <b>every</b> frame of a container
/// that holds more than one, such as the multi-page TIFFs scanners produce (or an animated GIF/WebP).
/// The single-image API decodes such a file too, but recognizes only its first frame; these methods walk
/// the whole container and report one result per frame.
/// </summary>
/// <remarks>
/// Frames are cloned into their own image one at a time and disposed immediately after recognition, so a
/// 200-page TIFF costs one extra page of pixels rather than two hundred. An image the caller supplies is
/// never disposed or mutated. Every overload also has a streaming twin
/// (<c>StreamTextFromFramesAsync</c>) that yields each frame as it completes, so a long document starts
/// producing text immediately instead of after the last page.
/// </remarks>
public static class EasyOcrServiceMultiFrameExtensions
{
    // ---- aggregate: every frame, one result ----

    /// <summary>
    /// OCRs every frame of a multi-frame image file (e.g. a multi-page TIFF) and returns all frames'
    /// results together. A single-frame file yields exactly one result.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imagePath">Path to the image file.</param>
    /// <param name="languages">Languages to recognize (same as the single-image API).</param>
    /// <param name="options">Recognition options applied to every frame.</param>
    /// <param name="frameOptions">Frame-count / per-frame pixel guards and progress reporting.</param>
    /// <param name="cancellationToken">Cancels between frames.</param>
    public static Task<MultiFrameOcrResult> ExtractTextFromFramesAsync(
        this IEasyOcrService service,
        string imagePath,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        MultiFrameOcrOptions? frameOptions = null,
        CancellationToken cancellationToken = default)
        => CollectAsync(StreamTextFromFramesAsync(service, imagePath, languages, options, frameOptions, cancellationToken));

    /// <summary>
    /// OCRs every frame of a multi-frame image read from a stream (format auto-detected) and returns all
    /// frames' results together. The stream is read but not disposed.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imageStream">Stream positioned at the start of the encoded image.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="options">Recognition options applied to every frame.</param>
    /// <param name="frameOptions">Frame-count / per-frame pixel guards and progress reporting.</param>
    /// <param name="cancellationToken">Cancels between frames.</param>
    public static Task<MultiFrameOcrResult> ExtractTextFromFramesAsync(
        this IEasyOcrService service,
        Stream imageStream,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        MultiFrameOcrOptions? frameOptions = null,
        CancellationToken cancellationToken = default)
        => CollectAsync(StreamTextFromFramesAsync(service, imageStream, languages, options, frameOptions, cancellationToken));

    /// <summary>
    /// OCRs every frame of a multi-frame image held in an encoded byte array (TIFF/GIF/PNG/…) and returns
    /// all frames' results together.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imageBytes">The encoded image bytes.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="options">Recognition options applied to every frame.</param>
    /// <param name="frameOptions">Frame-count / per-frame pixel guards and progress reporting.</param>
    /// <param name="cancellationToken">Cancels between frames.</param>
    public static Task<MultiFrameOcrResult> ExtractTextFromFramesAsync(
        this IEasyOcrService service,
        byte[] imageBytes,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        MultiFrameOcrOptions? frameOptions = null,
        CancellationToken cancellationToken = default)
        => CollectAsync(StreamTextFromFramesAsync(service, imageBytes, languages, options, frameOptions, cancellationToken));

    /// <summary>
    /// OCRs every frame of an already-decoded multi-frame image and returns all frames' results together.
    /// The caller retains ownership: the image is neither disposed nor modified — each frame beyond the
    /// first is cloned into a temporary image that this method disposes itself.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="image">The decoded image, possibly holding several frames.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="options">Recognition options applied to every frame.</param>
    /// <param name="frameOptions">Frame-count / per-frame pixel guards and progress reporting.</param>
    /// <param name="cancellationToken">Cancels between frames.</param>
    public static Task<MultiFrameOcrResult> ExtractTextFromFramesAsync(
        this IEasyOcrService service,
        Image<Rgb24> image,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        MultiFrameOcrOptions? frameOptions = null,
        CancellationToken cancellationToken = default)
        => CollectAsync(StreamTextFromFramesAsync(service, image, languages, options, frameOptions, cancellationToken));

    // ---- streaming: one result at a time, as each frame finishes ----

    /// <summary>
    /// OCRs a multi-frame image file, yielding each frame's result as soon as that frame is recognized —
    /// so a long scan can be written to disk, indexed or displayed page by page instead of being buffered
    /// in full. Results arrive in file order.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imagePath">Path to the image file.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="options">Recognition options applied to every frame.</param>
    /// <param name="frameOptions">Frame-count / per-frame pixel guards and progress reporting.</param>
    /// <param name="cancellationToken">Cancels between frames.</param>
    public static IAsyncEnumerable<FrameOcrResult> StreamTextFromFramesAsync(
        this IEasyOcrService service,
        string imagePath,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        MultiFrameOcrOptions? frameOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var (langs, opts) = Prepare(languages, frameOptions);
        var fullPath = Path.GetFullPath(imagePath);
        return IterateAsync(service, ct => LoadFromPathAsync(fullPath, opts, ct), langs, options, opts, cancellationToken);
    }

    /// <summary>
    /// OCRs a multi-frame image read from a stream, yielding each frame's result as soon as that frame is
    /// recognized. The stream is read but not disposed.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imageStream">Stream positioned at the start of the encoded image.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="options">Recognition options applied to every frame.</param>
    /// <param name="frameOptions">Frame-count / per-frame pixel guards and progress reporting.</param>
    /// <param name="cancellationToken">Cancels between frames.</param>
    public static IAsyncEnumerable<FrameOcrResult> StreamTextFromFramesAsync(
        this IEasyOcrService service,
        Stream imageStream,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        MultiFrameOcrOptions? frameOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(imageStream);
        var (langs, opts) = Prepare(languages, frameOptions);
        return IterateAsync(service, ct => LoadFromStreamAsync(imageStream, opts, ct), langs, options, opts, cancellationToken);
    }

    /// <summary>
    /// OCRs a multi-frame image held in an encoded byte array, yielding each frame's result as soon as
    /// that frame is recognized.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imageBytes">The encoded image bytes.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="options">Recognition options applied to every frame.</param>
    /// <param name="frameOptions">Frame-count / per-frame pixel guards and progress reporting.</param>
    /// <param name="cancellationToken">Cancels between frames.</param>
    public static IAsyncEnumerable<FrameOcrResult> StreamTextFromFramesAsync(
        this IEasyOcrService service,
        byte[] imageBytes,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        MultiFrameOcrOptions? frameOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
            throw new ArgumentException("Image bytes must not be empty.", nameof(imageBytes));

        var (langs, opts) = Prepare(languages, frameOptions);
        return IterateAsync(service, _ => new ValueTask<LoadedImage>(LoadFromBytes(imageBytes, opts)), langs, options, opts, cancellationToken);
    }

    /// <summary>
    /// OCRs an already-decoded multi-frame image, yielding each frame's result as soon as that frame is
    /// recognized. The caller retains ownership: the image is neither disposed nor modified.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="image">The decoded image, possibly holding several frames.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="options">Recognition options applied to every frame.</param>
    /// <param name="frameOptions">Frame-count / per-frame pixel guards and progress reporting.</param>
    /// <param name="cancellationToken">Cancels between frames.</param>
    public static IAsyncEnumerable<FrameOcrResult> StreamTextFromFramesAsync(
        this IEasyOcrService service,
        Image<Rgb24> image,
        IEnumerable<string> languages,
        RecognitionOptions? options = null,
        MultiFrameOcrOptions? frameOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(image);
        var (langs, opts) = Prepare(languages, frameOptions);
        // Owned: false — the caller's image outlives this call and must not be disposed.
        return IterateAsync(service, _ => new ValueTask<LoadedImage>(new LoadedImage(image, false)), langs, options, opts, cancellationToken);
    }

    // ---- core ----

    /// <summary>
    /// Walks the frames of the loaded image, recognizing one at a time. The image is materialized lazily,
    /// on the first <c>MoveNextAsync</c>, so nothing is decoded for an enumeration the caller abandons.
    /// </summary>
    private static async IAsyncEnumerable<FrameOcrResult> IterateAsync(
        IEasyOcrService service,
        Func<CancellationToken, ValueTask<LoadedImage>> loader,
        string[] languages,
        RecognitionOptions? options,
        MultiFrameOcrOptions frameOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = await loader(cancellationToken).ConfigureAwait(false);
        try
        {
            var image = loaded.Source;
            int count = image.Frames.Count;
            GuardFrameCount(count, frameOptions.MaxFrames);

            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = image.Frames[i];
                GuardFramePixels(frame.Width, frame.Height, i, frameOptions.MaxFramePixels);

                // A single-frame image *is* the frame: hand it straight to the existing single-image call
                // (which never disposes or mutates it) rather than paying for a full pixel copy. Anything
                // with more frames gets its own throw-away clone, disposed before the next frame is read.
                Image<Rgb24>? clone = count == 1 ? null : image.Frames.CloneFrame(i);
                try
                {
                    var target = clone ?? image;
                    var ocr = await service.ExtractTextFromImage(target, languages, options, cancellationToken).ConfigureAwait(false);
                    var result = new FrameOcrResult
                    {
                        FrameIndex = i,
                        Ocr = ocr,
                        PixelWidth = target.Width,
                        PixelHeight = target.Height,
                    };
                    frameOptions.Progress?.Report(new MultiFrameProgress(i + 1, count));
                    yield return result;
                }
                finally
                {
                    clone?.Dispose();
                }
            }
        }
        finally
        {
            if (loaded.Owned)
            {
                loaded.Source.Dispose();
            }
        }
    }

    /// <summary>Drains a frame stream into the aggregate result, timing the whole run.</summary>
    private static async Task<MultiFrameOcrResult> CollectAsync(IAsyncEnumerable<FrameOcrResult> frames)
    {
        var sw = Stopwatch.StartNew();
        var collected = new List<FrameOcrResult>();
        await foreach (var frame in frames.ConfigureAwait(false))
        {
            collected.Add(frame);
        }
        sw.Stop();

        return new MultiFrameOcrResult { Frames = collected, Duration = sw.Elapsed };
    }

    /// <summary>Validates and materializes the arguments shared by every overload.</summary>
    private static (string[] Languages, MultiFrameOcrOptions Options) Prepare(
        IEnumerable<string> languages, MultiFrameOcrOptions? frameOptions)
    {
        ArgumentNullException.ThrowIfNull(languages);
        var opts = frameOptions ?? new MultiFrameOcrOptions();
        opts.Validate();
        return (languages as string[] ?? languages.ToArray(), opts);
    }

    // ---- loading (guards mirror EasyOcrService's decompression-bomb checks) ----

    private static async ValueTask<LoadedImage> LoadFromPathAsync(string fullPath, MultiFrameOcrOptions opts, CancellationToken ct)
    {
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"The image file '{fullPath}' could not be found.", fullPath);

        var info = await Image.IdentifyAsync(fullPath, ct).ConfigureAwait(false);
        GuardHeader(info.FrameMetadataCollection.Count, info.Width, info.Height, opts);
        return new LoadedImage(await Image.LoadAsync<Rgb24>(fullPath, ct).ConfigureAwait(false), true);
    }

    private static async ValueTask<LoadedImage> LoadFromStreamAsync(Stream stream, MultiFrameOcrOptions opts, CancellationToken ct)
    {
        if (stream.CanSeek)
        {
            long position = stream.Position;
            var info = await Image.IdentifyAsync(stream, ct).ConfigureAwait(false);
            GuardHeader(info.FrameMetadataCollection.Count, info.Width, info.Height, opts);
            stream.Seek(position, SeekOrigin.Begin);
            return new LoadedImage(await Image.LoadAsync<Rgb24>(stream, ct).ConfigureAwait(false), true);
        }

        // Non-seekable: buffer the (compressed) bytes once so the header can be inspected before the frames
        // are decoded into memory.
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return LoadFromBytes(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), opts);
    }

    private static LoadedImage LoadFromBytes(ReadOnlySpan<byte> bytes, MultiFrameOcrOptions opts)
    {
        var info = Image.Identify(bytes);
        GuardHeader(info.FrameMetadataCollection.Count, info.Width, info.Height, opts);
        return new LoadedImage(Image.Load<Rgb24>(bytes), true);
    }

    // ---- guards ----

    /// <summary>
    /// Rejects an oversized container from its header, before any pixel buffer is allocated. The same
    /// checks run again on the decoded image, so a format whose header does not enumerate its frames is
    /// still bounded.
    /// </summary>
    private static void GuardHeader(int frameCount, int width, int height, MultiFrameOcrOptions opts)
    {
        GuardFrameCount(frameCount, opts.MaxFrames);
        GuardFramePixels(width, height, 0, opts.MaxFramePixels);
    }

    private static void GuardFrameCount(int frameCount, int maxFrames)
    {
        if (maxFrames > 0 && frameCount > maxFrames)
            throw new ImageTooLargeException(
                $"The image contains {frameCount} frames, exceeding the limit of {maxFrames} " +
                "(MultiFrameOcrOptions.MaxFrames). Raise the limit to process it, or split the file.");
    }

    private static void GuardFramePixels(int width, int height, int frameIndex, long maxFramePixels)
    {
        long pixels = (long)width * height;
        if (maxFramePixels > 0 && pixels > maxFramePixels)
            throw new ImageTooLargeException(
                $"Frame {frameIndex} is {width}x{height} ({pixels:N0} px), exceeding the per-frame limit of " +
                $"{maxFramePixels:N0} px (MultiFrameOcrOptions.MaxFrameMegapixels). Raise the limit or downscale " +
                "the image. This guard protects against decompression-bomb / pixel-flood denial of service.");
    }

    /// <summary>A loaded image plus whether this class allocated it (and must therefore dispose it).</summary>
    private readonly record struct LoadedImage(Image<Rgb24> Source, bool Owned);
}
