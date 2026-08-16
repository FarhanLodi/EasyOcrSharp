using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ZXing;

namespace EasyOcrSharp.Barcodes;

/// <summary>
/// Reads 1D barcodes and 2D codes (QR, Data Matrix, PDF417, Aztec…) out of an image. Scanning is pure
/// image processing — no model, no download, no GPU — so this is a plain static class you can call
/// without constructing an OCR service; the same methods are also exposed on
/// <see cref="IEasyOcrService"/> by <see cref="BarcodeExtensions"/> so they sit next to the OCR API when
/// you already have one.
/// </summary>
/// <remarks>
/// All overloads read the image's pixels on the calling thread and then decode on the thread pool, so the
/// decode never blocks your caller and you may dispose the image as soon as the method returns — you do
/// not have to keep it alive until the returned task completes. Cancellation is observed between decode
/// passes; a single pass over a decoded image is short and is not interrupted mid-flight.
/// </remarks>
public static class BarcodeScanner
{
    /// <summary>
    /// Scans an already-decoded image. The caller retains ownership of the image (it is not disposed).
    /// </summary>
    /// <param name="image">The image to scan.</param>
    /// <param name="options">Scan options; null uses <see cref="BarcodeOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Every barcode found, or an empty list — a barcode-free image is not an error.</returns>
    public static Task<IReadOnlyList<BarcodeResult>> ReadBarcodesAsync(
        Image<Rgb24> image,
        BarcodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var opts = options ?? BarcodeOptions.Default;

        var (x, y, width, height) = ResolveRegion(opts.Region, image.Width, image.Height);
        if (width <= 0 || height <= 0)
            return Task.FromResult<IReadOnlyList<BarcodeResult>>(Array.Empty<BarcodeResult>());

        // Read the pixels here, on the caller's thread, so the decode that follows owns a private buffer
        // and the source image is free the moment this method returns.
        var source = ImageSharpLuminanceSource.Create(image, x, y, width, height);
        return Task.Run(() => BarcodeDecoder.Decode(source, x, y, opts, cancellationToken), cancellationToken);
    }

    /// <summary>Scans an image file on disk.</summary>
    /// <param name="imagePath">Path of the image file.</param>
    /// <param name="options">Scan options; null uses <see cref="BarcodeOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Every barcode found, or an empty list.</returns>
    public static async Task<IReadOnlyList<BarcodeResult>> ReadBarcodesAsync(
        string imagePath,
        BarcodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var opts = options ?? BarcodeOptions.Default;

        if (opts.MaxImagePixels > 0)
        {
            var info = await Image.IdentifyAsync(imagePath, cancellationToken).ConfigureAwait(false);
            GuardPixels(info.Width, info.Height, opts.MaxImagePixels);
        }

        using var image = await Image.LoadAsync<Rgb24>(imagePath, cancellationToken).ConfigureAwait(false);
        return await ReadBarcodesAsync(image, opts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Scans an image from an encoded byte array (PNG/JPEG/etc.).</summary>
    /// <param name="imageBytes">The encoded image bytes.</param>
    /// <param name="options">Scan options; null uses <see cref="BarcodeOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Every barcode found, or an empty list.</returns>
    public static Task<IReadOnlyList<BarcodeResult>> ReadBarcodesAsync(
        byte[] imageBytes,
        BarcodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        return ReadBarcodesAsync(imageBytes.AsMemory(), options, cancellationToken);
    }

    /// <summary>Scans an image from encoded bytes.</summary>
    /// <param name="imageBytes">The encoded image bytes.</param>
    /// <param name="options">Scan options; null uses <see cref="BarcodeOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Every barcode found, or an empty list.</returns>
    public static async Task<IReadOnlyList<BarcodeResult>> ReadBarcodesAsync(
        ReadOnlyMemory<byte> imageBytes,
        BarcodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? BarcodeOptions.Default;
        using var image = LoadGuarded(imageBytes, opts.MaxImagePixels);
        return await ReadBarcodesAsync(image, opts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Scans an image from a stream (format auto-detected).</summary>
    /// <param name="imageStream">Stream positioned at the start of an encoded image.</param>
    /// <param name="options">Scan options; null uses <see cref="BarcodeOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Every barcode found, or an empty list.</returns>
    public static async Task<IReadOnlyList<BarcodeResult>> ReadBarcodesAsync(
        Stream imageStream,
        BarcodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        var opts = options ?? BarcodeOptions.Default;

        using var image = await LoadGuardedAsync(imageStream, opts.MaxImagePixels, cancellationToken).ConfigureAwait(false);
        return await ReadBarcodesAsync(image, opts, cancellationToken).ConfigureAwait(false);
    }

    // ---- internals shared with the IEasyOcrService extensions ----

    internal static (int X, int Y, int Width, int Height) ResolveRegion(OcrRegion? region, int imageWidth, int imageHeight)
        => region is { } r ? r.Resolve(imageWidth, imageHeight) : (0, 0, imageWidth, imageHeight);

    // Takes ReadOnlyMemory rather than ReadOnlySpan so that async callers can use it directly — a
    // ref struct may not live in an async method.
    internal static Image<Rgb24> LoadGuarded(ReadOnlyMemory<byte> bytes, long maxImagePixels)
    {
        if (maxImagePixels > 0)
        {
            var info = Image.Identify(bytes.Span);
            GuardPixels(info.Width, info.Height, maxImagePixels);
        }

        return Image.Load<Rgb24>(bytes.Span);
    }

    internal static async Task<Image<Rgb24>> LoadGuardedAsync(Stream stream, long maxImagePixels, CancellationToken ct)
    {
        if (maxImagePixels <= 0)
            return await Image.LoadAsync<Rgb24>(stream, ct).ConfigureAwait(false);

        if (stream.CanSeek)
        {
            long position = stream.Position;
            var info = await Image.IdentifyAsync(stream, ct).ConfigureAwait(false);
            GuardPixels(info.Width, info.Height, maxImagePixels);
            stream.Seek(position, SeekOrigin.Begin);
            return await Image.LoadAsync<Rgb24>(stream, ct).ConfigureAwait(false);
        }

        // Non-seekable: buffer the (small) compressed bytes so the header can be inspected before the
        // full pixel buffer is allocated.
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return LoadGuarded(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), maxImagePixels);
    }

    internal static void GuardPixels(int width, int height, long maxImagePixels)
    {
        long pixels = (long)width * height;
        if (pixels > maxImagePixels)
            throw new ImageTooLargeException(
                $"Image is {width}x{height} ({pixels:N0} px), exceeding the configured limit of " +
                $"{maxImagePixels:N0} px (BarcodeOptions.MaxImagePixels). Raise the limit or downscale the " +
                "image. This guard protects against decompression-bomb / pixel-flood denial of service.");
    }
}

/// <summary>
/// Barcode helpers on <see cref="IEasyOcrService"/>: the same scans <see cref="BarcodeScanner"/> offers,
/// surfaced next to the OCR API, plus <c>ExtractTextAndBarcodesAsync</c>, which reads a page once and
/// returns its text and its barcodes together.
/// </summary>
/// <remarks>
/// Documents that carry both — invoices with a payment QR, shipping labels, tickets, ID pages — otherwise
/// force you to decode the image twice. These overloads decode it once, hand the pixels to the barcode
/// decoder up front, and then run recognition and barcode scanning concurrently.
/// </remarks>
public static class BarcodeExtensions
{
    /// <summary>Scans an already-decoded image for barcodes. The image is not disposed.</summary>
    /// <param name="service">The OCR service (only used for discoverability — scanning needs no model).</param>
    /// <param name="image">The image to scan.</param>
    /// <param name="options">Scan options; null uses <see cref="BarcodeOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Every barcode found, or an empty list.</returns>
    public static Task<IReadOnlyList<BarcodeResult>> ReadBarcodesAsync(
        this IEasyOcrService service,
        Image<Rgb24> image,
        BarcodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        return BarcodeScanner.ReadBarcodesAsync(image, options, cancellationToken);
    }

    /// <summary>Scans an image file on disk for barcodes.</summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imagePath">Path of the image file.</param>
    /// <param name="options">Scan options; null uses <see cref="BarcodeOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Every barcode found, or an empty list.</returns>
    public static Task<IReadOnlyList<BarcodeResult>> ReadBarcodesAsync(
        this IEasyOcrService service,
        string imagePath,
        BarcodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        return BarcodeScanner.ReadBarcodesAsync(imagePath, options, cancellationToken);
    }

    /// <summary>Scans an image held as an encoded byte array for barcodes.</summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imageBytes">The encoded image bytes.</param>
    /// <param name="options">Scan options; null uses <see cref="BarcodeOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Every barcode found, or an empty list.</returns>
    public static Task<IReadOnlyList<BarcodeResult>> ReadBarcodesAsync(
        this IEasyOcrService service,
        byte[] imageBytes,
        BarcodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        return BarcodeScanner.ReadBarcodesAsync(imageBytes, options, cancellationToken);
    }

    /// <summary>Scans an image held as encoded bytes for barcodes.</summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imageBytes">The encoded image bytes.</param>
    /// <param name="options">Scan options; null uses <see cref="BarcodeOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Every barcode found, or an empty list.</returns>
    public static Task<IReadOnlyList<BarcodeResult>> ReadBarcodesAsync(
        this IEasyOcrService service,
        ReadOnlyMemory<byte> imageBytes,
        BarcodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        return BarcodeScanner.ReadBarcodesAsync(imageBytes, options, cancellationToken);
    }

    /// <summary>Scans an image read from a stream for barcodes (format auto-detected).</summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imageStream">Stream positioned at the start of an encoded image.</param>
    /// <param name="options">Scan options; null uses <see cref="BarcodeOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Every barcode found, or an empty list.</returns>
    public static Task<IReadOnlyList<BarcodeResult>> ReadBarcodesAsync(
        this IEasyOcrService service,
        Stream imageStream,
        BarcodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        return BarcodeScanner.ReadBarcodesAsync(imageStream, options, cancellationToken);
    }

    // ---- text + barcodes from one decode of the page ----

    /// <summary>
    /// Recognizes the text of an already-decoded image and scans it for barcodes, from a single pass over
    /// the image. The caller retains ownership of the image (it is not disposed).
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="image">The page to read.</param>
    /// <param name="languages">Languages to recognize, as for <c>ExtractTextFromImage</c>.</param>
    /// <param name="recognitionOptions">OCR options; null uses <see cref="RecognitionOptions.Default"/>.</param>
    /// <param name="barcodeOptions">Scan options; null uses <see cref="BarcodeOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels both halves of the read.</param>
    /// <returns>The OCR result and the barcodes found on the same page.</returns>
    public static async Task<OcrAndBarcodeResult> ExtractTextAndBarcodesAsync(
        this IEasyOcrService service,
        Image<Rgb24> image,
        IEnumerable<string> languages,
        RecognitionOptions? recognitionOptions = null,
        BarcodeOptions? barcodeOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(languages);

        // Started first on purpose: this call copies the pixels it needs synchronously and decodes on the
        // thread pool, so recognition never contends with it for the image.
        var barcodeTask = BarcodeScanner.ReadBarcodesAsync(image, barcodeOptions, cancellationToken);
        var ocrTask = service.ExtractTextFromImage(image, languages, recognitionOptions, cancellationToken);

        await Task.WhenAll(ocrTask, barcodeTask).ConfigureAwait(false);

        return new OcrAndBarcodeResult
        {
            Ocr = await ocrTask.ConfigureAwait(false),
            Barcodes = await barcodeTask.ConfigureAwait(false),
        };
    }

    /// <summary>Reads the text and the barcodes of an image file, from a single decode of the file.</summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imagePath">Path of the image file.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="recognitionOptions">OCR options; null uses <see cref="RecognitionOptions.Default"/>.</param>
    /// <param name="barcodeOptions">Scan options; null uses <see cref="BarcodeOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels both halves of the read.</param>
    /// <returns>The OCR result and the barcodes found on the same page.</returns>
    public static async Task<OcrAndBarcodeResult> ExtractTextAndBarcodesAsync(
        this IEasyOcrService service,
        string imagePath,
        IEnumerable<string> languages,
        RecognitionOptions? recognitionOptions = null,
        BarcodeOptions? barcodeOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        var opts = barcodeOptions ?? BarcodeOptions.Default;
        if (opts.MaxImagePixels > 0)
        {
            var info = await Image.IdentifyAsync(imagePath, cancellationToken).ConfigureAwait(false);
            BarcodeScanner.GuardPixels(info.Width, info.Height, opts.MaxImagePixels);
        }

        using var image = await Image.LoadAsync<Rgb24>(imagePath, cancellationToken).ConfigureAwait(false);
        return await service.ExtractTextAndBarcodesAsync(image, languages, recognitionOptions, opts, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads the text and the barcodes of an encoded image, from a single decode of the bytes.</summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imageBytes">The encoded image bytes.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="recognitionOptions">OCR options; null uses <see cref="RecognitionOptions.Default"/>.</param>
    /// <param name="barcodeOptions">Scan options; null uses <see cref="BarcodeOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels both halves of the read.</param>
    /// <returns>The OCR result and the barcodes found on the same page.</returns>
    public static async Task<OcrAndBarcodeResult> ExtractTextAndBarcodesAsync(
        this IEasyOcrService service,
        ReadOnlyMemory<byte> imageBytes,
        IEnumerable<string> languages,
        RecognitionOptions? recognitionOptions = null,
        BarcodeOptions? barcodeOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);

        var opts = barcodeOptions ?? BarcodeOptions.Default;
        using var image = BarcodeScanner.LoadGuarded(imageBytes, opts.MaxImagePixels);
        return await service.ExtractTextAndBarcodesAsync(image, languages, recognitionOptions, opts, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads the text and the barcodes of an encoded image, from a single decode of the bytes.</summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imageBytes">The encoded image bytes.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="recognitionOptions">OCR options; null uses <see cref="RecognitionOptions.Default"/>.</param>
    /// <param name="barcodeOptions">Scan options; null uses <see cref="BarcodeOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels both halves of the read.</param>
    /// <returns>The OCR result and the barcodes found on the same page.</returns>
    public static Task<OcrAndBarcodeResult> ExtractTextAndBarcodesAsync(
        this IEasyOcrService service,
        byte[] imageBytes,
        IEnumerable<string> languages,
        RecognitionOptions? recognitionOptions = null,
        BarcodeOptions? barcodeOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(imageBytes);
        return service.ExtractTextAndBarcodesAsync(
            imageBytes.AsMemory(), languages, recognitionOptions, barcodeOptions, cancellationToken);
    }

    /// <summary>Reads the text and the barcodes of an image stream, from a single decode of the stream.</summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imageStream">Stream positioned at the start of an encoded image.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="recognitionOptions">OCR options; null uses <see cref="RecognitionOptions.Default"/>.</param>
    /// <param name="barcodeOptions">Scan options; null uses <see cref="BarcodeOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels both halves of the read.</param>
    /// <returns>The OCR result and the barcodes found on the same page.</returns>
    public static async Task<OcrAndBarcodeResult> ExtractTextAndBarcodesAsync(
        this IEasyOcrService service,
        Stream imageStream,
        IEnumerable<string> languages,
        RecognitionOptions? recognitionOptions = null,
        BarcodeOptions? barcodeOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(imageStream);

        var opts = barcodeOptions ?? BarcodeOptions.Default;
        using var image = await BarcodeScanner
            .LoadGuardedAsync(imageStream, opts.MaxImagePixels, cancellationToken).ConfigureAwait(false);
        return await service.ExtractTextAndBarcodesAsync(image, languages, recognitionOptions, opts, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// The decode loop itself: configures the barcode engine from <see cref="BarcodeOptions"/>, drives the
/// optional rotation passes, and projects the engine's results back into EasyOcrSharp's own types and the
/// source image's coordinate space. Internal, because no engine type may reach the public surface.
/// </summary>
internal static class BarcodeDecoder
{
    /// <summary>
    /// Decodes <paramref name="source"/> and returns the results shifted by
    /// (<paramref name="offsetX"/>, <paramref name="offsetY"/>) — the origin of the scanned region within
    /// the original image. Runs synchronously; callers put it on the thread pool.
    /// </summary>
    internal static IReadOnlyList<BarcodeResult> Decode(
        ImageSharpLuminanceSource source, int offsetX, int offsetY, BarcodeOptions options, CancellationToken ct)
    {
        var reader = CreateReader(options);

        int sourceWidth = source.Width;
        int sourceHeight = source.Height;
        int passes = options.AutoRotate ? 4 : 1;

        LuminanceSource current = source;
        for (int rotation = 0; rotation < passes; rotation++)
        {
            ct.ThrowIfCancellationRequested();
            if (rotation > 0)
                current = current.rotateCounterClockwise();

            var found = options.MultipleCodes
                ? reader.DecodeMultiple(current)
                : Single(reader.Decode(current));

            if (found is { Length: > 0 })
                return Project(found, rotation, sourceWidth, sourceHeight, offsetX, offsetY);
        }

        return Array.Empty<BarcodeResult>();
    }

    private static Result[]? Single(Result? result) => result is null ? null : new[] { result };

    private static BarcodeReaderGeneric CreateReader(BarcodeOptions options)
    {
        var reader = new BarcodeReaderGeneric
        {
            // Rotation is driven by this class rather than by the engine: the engine reports coordinates
            // in whichever orientation it succeeded in, and only the caller of the rotation knows how to
            // map them back to the source image.
            AutoRotate = false,
        };

        reader.Options.TryInverted = options.TryInverted;
        reader.Options.TryHarder = options.TryHarder;

        var formats = BarcodeFormatMap.ToZXingList(options.Formats);
        if (formats is not null)
            reader.Options.PossibleFormats = formats;

        return reader;
    }

    private static BarcodeResult[] Project(
        Result[] results, int rotation, int sourceWidth, int sourceHeight, int offsetX, int offsetY)
    {
        var projected = new List<BarcodeResult>(results.Length);
        foreach (var result in results)
        {
            if (result is null) continue;
            projected.Add(ToBarcodeResult(result, rotation, sourceWidth, sourceHeight, offsetX, offsetY));
        }

        return projected.ToArray();
    }

    private static BarcodeResult ToBarcodeResult(
        Result result, int rotation, int sourceWidth, int sourceHeight, int offsetX, int offsetY)
    {
        var polygon = ToPolygon(result.ResultPoints, rotation, sourceWidth, sourceHeight, offsetX, offsetY);

        return new BarcodeResult
        {
            Text = result.Text ?? string.Empty,
            Format = BarcodeFormatMap.FromZXing(result.BarcodeFormat),
            RawBytes = result.RawBytes is { Length: > 0 } raw ? new ReadOnlyMemory<byte>(raw) : default,
            BoundingPolygon = polygon,
            BoundingBox = OcrBoundingBox.FromPoints(polygon),
        };
    }

    private static OcrPoint[] ToPolygon(
        ResultPoint[]? points, int rotation, int sourceWidth, int sourceHeight, int offsetX, int offsetY)
    {
        if (points is null || points.Length == 0)
            return Array.Empty<OcrPoint>();

        var polygon = new List<OcrPoint>(points.Length);
        foreach (var point in points)
        {
            if (point is null) continue;
            var mapped = ImageSharpLuminanceSource.Unrotate(point.X, point.Y, rotation, sourceWidth, sourceHeight);
            polygon.Add(new OcrPoint(mapped.X + offsetX, mapped.Y + offsetY));
        }

        return polygon.ToArray();
    }
}
