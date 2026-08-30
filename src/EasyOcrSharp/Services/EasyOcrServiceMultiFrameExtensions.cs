using System.Diagnostics;
using System.Runtime.CompilerServices;
using EasyOcrSharp.Diagnostics;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

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
/// <para>
/// One <c>easyocr.operations</c> / <c>easyocr.duration</c> data point covers the whole container, with
/// <c>easyocr.pages</c> counting the frames actually recognized and <c>easyocr.lines</c> their text — see
/// <see cref="EasyOcrDiagnostics"/>. The aggregate overloads drain the streaming ones, so a call is
/// measured exactly once either way. No concurrency slot is taken here: each frame goes through the public
/// single-image API, which is already gated, and holding a second slot across every frame would deadlock a
/// service limited to one concurrent operation.
/// </para>
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
    /// <remarks>
    /// The operation is measured here rather than in the public overloads because this is where the work
    /// happens: an enumeration the caller never starts does none and so emits no data point, and the
    /// aggregate <c>ExtractTextFromFramesAsync</c> overloads — which only drain this — are counted once
    /// rather than twice.
    /// </remarks>
    private static async IAsyncEnumerable<FrameOcrResult> IterateAsync(
        IEasyOcrService service,
        Func<CancellationToken, ValueTask<LoadedImage>> loader,
        string[] languages,
        RecognitionOptions? options,
        MultiFrameOcrOptions frameOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = EasyOcrDiagnostics.ActivitySource.StartActivity("EasyOcr.MultiFrame", ActivityKind.Internal);
        using var recorder = EasyOcrDiagnostics.Begin(EasyOcrDiagnostics.OperationNames.MultiFrame, ProviderOf(service))
            .WithLanguages(languages)
            .Annotate(activity);

        // C# forbids `yield return` inside a try/catch, so the outcome cannot simply be decided by one
        // wrapper around the loop. Instead each step that can throw sits in its own catch that records the
        // failure and rethrows, the yields stay outside those catches, and this local carries the verdict
        // to the finally below (the iterator's state machine preserves it across suspensions). Breaking the
        // iterator here would be far worse than having no metrics, so the shape matters more than brevity.
        bool settled = false;

        // `default` leaves Owned false, so the finally can never dispose a Source that was never loaded.
        LoadedImage loaded = default;
        try
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                loaded = await loader(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                recorder.Failure(ex);
                settled = true;
                throw;
            }

            var image = loaded.Source;
            int count;
            try
            {
                count = image.Frames.Count;
                GuardFrameCount(count, frameOptions.MaxFrames);
            }
            catch (Exception ex)
            {
                recorder.Failure(ex);
                settled = true;
                throw;
            }

            for (int i = 0; i < count; i++)
            {
                Image<Rgb24>? clone;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var frame = image.Frames[i];
                    GuardFramePixels(frame.Width, frame.Height, i, frameOptions.MaxFramePixels);

                    // A single-frame image *is* the frame: hand it straight to the existing single-image call
                    // (which never disposes or mutates it) rather than paying for a full pixel copy. Anything
                    // with more frames gets its own throw-away clone, disposed before the next frame is read.
                    clone = count == 1 ? null : image.Frames.CloneFrame(i);
                }
                catch (Exception ex)
                {
                    recorder.Failure(ex);
                    settled = true;
                    throw;
                }

                try
                {
                    FrameOcrResult result;
                    try
                    {
                        var target = clone ?? image;
                        var ocr = await service.ExtractTextFromImage(target, languages, options, cancellationToken).ConfigureAwait(false);
                        result = new FrameOcrResult
                        {
                            FrameIndex = i,
                            Ocr = ocr,
                            PixelWidth = target.Width,
                            PixelHeight = target.Height,
                        };
                        // Counted as each frame finishes, never from the frame count up front, so a container
                        // that fails on frame 57 reports the 56 frames it really recognized.
                        recorder.AddPages(1).AddLines(ocr.Lines.Count);
                        frameOptions.Progress?.Report(new MultiFrameProgress(i + 1, count));
                    }
                    catch (Exception ex)
                    {
                        recorder.Failure(ex);
                        settled = true;
                        throw;
                    }
                    yield return result;
                }
                finally
                {
                    clone?.Dispose();
                }
            }

            recorder.Success();
            settled = true;
        }
        finally
        {
            if (loaded.Owned)
            {
                loaded.Source.Dispose();
            }

            // A consumer that stops early — break, Take, or a throw in its own loop body — disposes the
            // enumerator with nothing having failed and arrives here unsettled. That is a caller's choice,
            // not a fault: recording it as an error would let one FirstAsync() poison the error rate.
            if (!settled)
            {
                recorder.Canceled();
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
        GuardHeader(info.FrameCount, info.Width, info.Height, opts);
        return new LoadedImage(
            await DecodeLimits.LoadAsync(fullPath, opts.MaxFramePixels, MaxPixelsOption, ct).ConfigureAwait(false), true);
    }

    private static async ValueTask<LoadedImage> LoadFromStreamAsync(Stream stream, MultiFrameOcrOptions opts, CancellationToken ct)
    {
        if (stream.CanSeek)
        {
            long position = stream.Position;
            var info = await Image.IdentifyAsync(stream, ct).ConfigureAwait(false);
            GuardHeader(info.FrameCount, info.Width, info.Height, opts);
            stream.Seek(position, SeekOrigin.Begin);
            return new LoadedImage(
                await DecodeLimits.LoadAsync(stream, opts.MaxFramePixels, MaxPixelsOption, ct).ConfigureAwait(false), true);
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
        GuardHeader(info.FrameCount, info.Width, info.Height, opts);
        return new LoadedImage(DecodeLimits.Load(bytes, opts.MaxFramePixels, MaxPixelsOption), true);
    }

    // ---- guards ----

    /// <summary>The option a caller raises when a frame is rejected for being too large.</summary>
    private const string MaxPixelsOption = "MultiFrameOcrOptions.MaxFrameMegapixels";

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

    /// <summary>
    /// The <see cref="EasyOcrDiagnostics.TagNames.Provider"/> value for a composite operation.
    /// <see cref="EasyOcrService"/> keeps the exact resolved provider private and these are extensions on
    /// the interface, so the tag is coarse here: "Cpu" matches the per-frame points exactly, a GPU service
    /// reports "Gpu" without claiming to know whether it is CUDA, DirectML or CoreML, and a caller's own
    /// <see cref="IEasyOcrService"/> reports "unknown" rather than a guess that would make a dashboard's
    /// CPU/GPU split quietly wrong.
    /// </summary>
    private static string ProviderOf(IEasyOcrService service)
        => service is EasyOcrService concrete ? concrete.ProviderName : "unknown";

    /// <summary>A loaded image plus whether this class allocated it (and must therefore dispose it).</summary>
    private readonly record struct LoadedImage(Image<Rgb24> Source, bool Owned);
}
