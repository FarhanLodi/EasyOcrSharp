using System.Text;
using System.Text.RegularExpressions;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Pdf;
using EasyOcrSharp.Pdf.Internal;
using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Redaction;

/// <summary>
/// One-call redaction: run OCR, decide what is sensitive, and burn the covering pixels into the image.
/// Provided as extensions so they work with any <see cref="IEasyOcrService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The covering is destructive. Matched quads are painted directly onto a copy of the source raster —
/// there is no annotation, no overlay object and no separate layer, so nothing can be peeled off,
/// toggled, or recovered by flattening. The caller's own image is never modified; the redacted copy
/// comes back in <see cref="RedactionResult.Image"/> and is the caller's to dispose.
/// </para>
/// <para>
/// Quads, not rectangles. A line detected at an angle is covered by the rotated quadrilateral the
/// detector actually reported, so a skewed scan does not lose a wedge of text at each end (which an
/// axis-aligned box would) and does not blot out neighbouring lines (which its corners would).
/// </para>
/// <para>
/// What redaction cannot do is see what OCR missed. Text the detector never found is text no rule can
/// match, so treat a redaction pass as a very strong assistant and verify the output when the stakes
/// justify it — especially with <see cref="RedactionStyle.Blur"/> or
/// <see cref="RedactionStyle.Pixelate"/>, which are cosmetic transforms rather than destruction of
/// information. <see cref="RedactionStyle.FilledBox"/> is the only style that leaves nothing behind.
/// </para>
/// </remarks>
public static class OcrRedactionExtensions
{
    // ---- image entry points ---------------------------------------------------------------------

    /// <summary>
    /// OCRs an already-decoded image and returns a redacted copy of it. The input image is neither
    /// modified nor disposed.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="image">The image to redact.</param>
    /// <param name="languages">Languages to recognize (same as the single-image OCR API).</param>
    /// <param name="options">What to redact and how. Defaults to <see cref="RedactionOptions.Default"/>,
    /// which selects nothing — supply at least one rule, pattern, keyword or predicate.</param>
    /// <param name="cancellationToken">Cancels the OCR pass.</param>
    public static async Task<RedactionResult> RedactAsync(
        this IEasyOcrService service,
        Image<Rgb24> image,
        IEnumerable<string> languages,
        RedactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(languages);

        options ??= RedactionOptions.Default;
        options.Validate();

        var ocr = await service.ExtractTextFromImage(image, languages, options.EffectiveRecognition, cancellationToken)
            .ConfigureAwait(false);
        return RedactionEngine.Apply(image, ocr, options);
    }

    /// <summary>OCRs an image file and returns a redacted copy of it.</summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imagePath">Path of the image to redact.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="options">What to redact and how.</param>
    /// <param name="cancellationToken">Cancels the OCR pass.</param>
    public static async Task<RedactionResult> RedactAsync(
        this IEasyOcrService service,
        string imagePath,
        IEnumerable<string> languages,
        RedactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        options ??= RedactionOptions.Default;
        options.Validate();

        string full = Path.GetFullPath(imagePath);
        if (options.MaxSourcePixels > 0)
        {
            var info = await Image.IdentifyAsync(full, cancellationToken).ConfigureAwait(false);
            GuardPixels(info.Width, info.Height, options.MaxSourcePixels);
        }

        using var image = await DecodeLimits
            .LoadAsync(full, options.MaxSourcePixels, MaxPixelsOption, cancellationToken).ConfigureAwait(false);
        return await service.RedactAsync(image, languages, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>OCRs an image held in an encoded byte array (PNG/JPEG/…) and returns a redacted copy.</summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imageBytes">The encoded image.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="options">What to redact and how.</param>
    /// <param name="cancellationToken">Cancels the OCR pass.</param>
    public static Task<RedactionResult> RedactAsync(
        this IEasyOcrService service,
        byte[] imageBytes,
        IEnumerable<string> languages,
        RedactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        return service.RedactAsync(new ReadOnlyMemory<byte>(imageBytes), languages, options, cancellationToken);
    }

    /// <summary>OCRs an image held in encoded bytes and returns a redacted copy.</summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imageBytes">The encoded image.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="options">What to redact and how.</param>
    /// <param name="cancellationToken">Cancels the OCR pass.</param>
    public static async Task<RedactionResult> RedactAsync(
        this IEasyOcrService service,
        ReadOnlyMemory<byte> imageBytes,
        IEnumerable<string> languages,
        RedactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);

        options ??= RedactionOptions.Default;
        options.Validate();

        if (options.MaxSourcePixels > 0)
        {
            var info = Image.Identify(imageBytes.Span);
            GuardPixels(info.Width, info.Height, options.MaxSourcePixels);
        }

        using var image = DecodeLimits.Load(imageBytes.Span, options.MaxSourcePixels, MaxPixelsOption);
        return await service.RedactAsync(image, languages, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>OCRs an image read from a stream (format auto-detected) and returns a redacted copy.</summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="imageStream">Stream positioned at the encoded image.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="options">What to redact and how.</param>
    /// <param name="cancellationToken">Cancels the OCR pass.</param>
    public static async Task<RedactionResult> RedactAsync(
        this IEasyOcrService service,
        Stream imageStream,
        IEnumerable<string> languages,
        RedactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(imageStream);

        options ??= RedactionOptions.Default;
        options.Validate();

        if (options.MaxSourcePixels > 0 && !imageStream.CanSeek)
        {
            // Non-seekable: buffer the (small) compressed bytes once so the header can be inspected
            // before the full pixel buffer is allocated.
            using var buffer = new MemoryStream();
            await imageStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return await service.RedactAsync(new ReadOnlyMemory<byte>(buffer.GetBuffer(), 0, (int)buffer.Length),
                languages, options, cancellationToken).ConfigureAwait(false);
        }

        if (options.MaxSourcePixels > 0)
        {
            long position = imageStream.Position;
            var info = await Image.IdentifyAsync(imageStream, cancellationToken).ConfigureAwait(false);
            GuardPixels(info.Width, info.Height, options.MaxSourcePixels);
            imageStream.Seek(position, SeekOrigin.Begin);
        }

        using var image = await DecodeLimits
            .LoadAsync(imageStream, options.MaxSourcePixels, MaxPixelsOption, cancellationToken).ConfigureAwait(false);
        return await service.RedactAsync(image, languages, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Redacts an image using an OCR result you already have, without running recognition again. Pure
    /// and synchronous — the same matching and painting the async overloads perform, useful when the
    /// OCR pass was done earlier (or came from <c>RecognizeRegionsAsync</c>) and for testing.
    /// </summary>
    /// <param name="ocr">The recognition result whose lines are examined.</param>
    /// <param name="image">The image the result was produced from. Neither modified nor disposed.</param>
    /// <param name="options">What to redact and how.</param>
    /// <remarks>
    /// <see cref="RedactionScope.MatchedWords"/> only works here if <paramref name="ocr"/> was produced
    /// with <see cref="WordLevelDetail.Words"/> or finer; otherwise each matching line is covered whole.
    /// </remarks>
    public static RedactionResult Redact(this OcrResult ocr, Image<Rgb24> image, RedactionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(image);

        options ??= RedactionOptions.Default;
        options.Validate();
        return RedactionEngine.Apply(image, ocr, options);
    }

    // ---- PDF entry points -----------------------------------------------------------------------

    /// <summary>
    /// Rasterizes a PDF, redacts every page, and rebuilds the document from the painted page images.
    /// See <see cref="PdfRedactionResult"/> for why the output is image-only.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="pdfBytes">The source PDF.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="options">What to redact and how.</param>
    /// <param name="pdfOptions">Rasterization settings (DPI, JPEG quality, page guards, progress).</param>
    /// <param name="cancellationToken">Cancels the whole document.</param>
    public static async Task<PdfRedactionResult> RedactPdfAsync(
        this IEasyOcrService service,
        byte[] pdfBytes,
        IEnumerable<string> languages,
        RedactionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        var pdf = pdfOptions ?? new PdfOcrOptions();
        pdf.Validate();

        var builder = new SearchablePdfBuilder();
        var pages = new List<PdfPageRedaction>();

        await service.RedactPdfPagesAsync(
            pdfBytes,
            languages,
            (page, redacted, _) =>
            {
                // OcrResult.Empty ⇒ image-only page: no invisible text layer, so the redacted words are
                // not smuggled back into the document in machine-readable form.
                builder.AddPage(redacted, OcrResult.Empty, pdf.Dpi, pdf.JpegQuality);
                pages.Add(page);
                return Task.CompletedTask;
            },
            options,
            pdf,
            cancellationToken).ConfigureAwait(false);

        return new PdfRedactionResult { Pages = pages, Pdf = builder.Build() };
    }

    /// <summary>Rasterizes a PDF file, redacts every page, and rebuilds the document in memory.</summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="pdfPath">Path of the source PDF.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="options">What to redact and how.</param>
    /// <param name="pdfOptions">Rasterization settings.</param>
    /// <param name="cancellationToken">Cancels the whole document.</param>
    public static async Task<PdfRedactionResult> RedactPdfAsync(
        this IEasyOcrService service,
        string pdfPath,
        IEnumerable<string> languages,
        RedactionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        var bytes = await File.ReadAllBytesAsync(Path.GetFullPath(pdfPath), cancellationToken).ConfigureAwait(false);
        return await service.RedactPdfAsync(bytes, languages, options, pdfOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rasterizes a PDF file, redacts every page, and writes the rebuilt document to
    /// <paramref name="outputPdfPath"/>.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="inputPdfPath">Path of the source PDF.</param>
    /// <param name="outputPdfPath">Path the redacted PDF is written to (overwritten if it exists).</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="options">What to redact and how.</param>
    /// <param name="pdfOptions">Rasterization settings.</param>
    /// <param name="cancellationToken">Cancels the whole document.</param>
    public static async Task<PdfRedactionResult> RedactPdfAsync(
        this IEasyOcrService service,
        string inputPdfPath,
        string outputPdfPath,
        IEnumerable<string> languages,
        RedactionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPdfPath);
        var result = await service.RedactPdfAsync(inputPdfPath, languages, options, pdfOptions, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.GetFullPath(outputPdfPath), result.Pdf, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// The per-page building block behind <see cref="RedactPdfAsync(IEasyOcrService, byte[], IEnumerable{string}, RedactionOptions, PdfOcrOptions, CancellationToken)"/>:
    /// renders one page at a time, redacts it, and hands the painted image to
    /// <paramref name="pageHandler"/>. Use it to write pages out individually (as PNGs, to a queue, to
    /// your own PDF writer) without ever holding the whole document in memory.
    /// </summary>
    /// <param name="service">The OCR service.</param>
    /// <param name="pdfBytes">The source PDF.</param>
    /// <param name="languages">Languages to recognize.</param>
    /// <param name="pageHandler">
    /// Receives the page's audit record and its redacted image. <b>The image is disposed as soon as the
    /// handler's task completes</b> — clone or encode it inside the handler, never store the reference.
    /// </param>
    /// <param name="options">What to redact and how.</param>
    /// <param name="pdfOptions">Rasterization settings.</param>
    /// <param name="cancellationToken">Cancels the whole document.</param>
    public static async Task RedactPdfPagesAsync(
        this IEasyOcrService service,
        byte[] pdfBytes,
        IEnumerable<string> languages,
        Func<PdfPageRedaction, Image<Rgb24>, CancellationToken, Task> pageHandler,
        RedactionOptions? options = null,
        PdfOcrOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(pageHandler);

        var redactionOptions = options ?? RedactionOptions.Default;
        redactionOptions.Validate();
        var pdf = pdfOptions ?? new PdfOcrOptions();
        pdf.Validate();

        var langs = languages as string[] ?? languages.ToArray();
        var recognition = redactionOptions.EffectiveRecognition;

        await PdfRasterizer.ForEachPageAsync(
            pdfBytes,
            pdf.Dpi,
            pdf.MaxPages,
            pdf.MaxPagePixels,
            async (index, count, image) =>
            {
                var ocr = await service.ExtractTextFromImage(image, langs, recognition, cancellationToken).ConfigureAwait(false);
                using var redaction = RedactionEngine.Apply(image, ocr, redactionOptions);

                var page = new PdfPageRedaction
                {
                    PageNumber = index + 1,
                    Ocr = ocr,
                    Redactions = redaction.Redactions,
                    SanitizedText = redaction.SanitizedText,
                    PixelWidth = image.Width,
                    PixelHeight = image.Height,
                };

                await pageHandler(page, redaction.Image, cancellationToken).ConfigureAwait(false);
                pdf.Progress?.Report(new PdfPageProgress(index + 1, count));
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The option a caller raises when an image is rejected for being too large.</summary>
    private const string MaxPixelsOption = "RedactionOptions.MaxSourcePixels";

    private static void GuardPixels(int width, int height, long maxPixels)
    {
        long pixels = (long)width * height;
        if (pixels > maxPixels)
        {
            throw new ImageTooLargeException(
                $"Image is {width}x{height} ({pixels:N0} px), exceeding the configured limit of " +
                $"{maxPixels:N0} px (RedactionOptions.MaxSourcePixels). Raise the limit or downscale the image. " +
                "This guard protects against decompression-bomb / pixel-flood denial of service.");
        }
    }
}

/// <summary>
/// The matching and painting behind <see cref="OcrRedactionExtensions"/>: it decides which quads to
/// cover and writes the pixels. Deliberately free of any OCR dependency so it can be exercised with
/// hand-built <see cref="OcrResult"/>s.
/// </summary>
internal static class RedactionEngine
{
    /// <summary>Produces a redacted copy of <paramref name="source"/> plus the audit trail.</summary>
    public static RedactionResult Apply(Image<Rgb24> source, OcrResult ocr, RedactionOptions options)
    {
        var canvas = source.Clone();
        var redactions = new List<RedactedLine>();
        var text = new StringBuilder();

        try
        {
            foreach (var line in ocr.Lines)
            {
                var matches = options.HasNoSelectors ? null : FindMatches(line, options);
                string sanitized = matches is { Count: > 0 }
                    ? Mask(line.Text, matches, options.MaskCharacter)
                    : line.Text;

                if (!string.IsNullOrEmpty(sanitized))
                {
                    if (text.Length > 0) text.AppendLine();
                    text.Append(sanitized);
                }

                if (matches is not { Count: > 0 }) continue;

                var (polygons, wholeLine) = ResolvePolygons(line, matches, options);
                foreach (var polygon in polygons)
                {
                    Paint(canvas, polygon, options);
                }

                redactions.Add(new RedactedLine
                {
                    Line = line,
                    Matches = matches,
                    RedactedPolygons = polygons,
                    WholeLineRedacted = wholeLine,
                    SanitizedText = sanitized,
                });
            }
        }
        catch
        {
            canvas.Dispose();
            throw;
        }

        return new RedactionResult
        {
            Image = canvas,
            Ocr = ocr,
            Redactions = redactions,
            SanitizedText = text.ToString(),
        };
    }

    // ---- matching -------------------------------------------------------------------------------

    private static List<RedactionMatch> FindMatches(OcrLine line, RedactionOptions options)
    {
        var matches = new List<RedactionMatch>();
        string text = line.Text;

        if (!string.IsNullOrEmpty(text))
        {
            foreach (var rule in options.Rules)
            {
                if (rule is null) continue;
                foreach (Match m in rule.Pattern.Matches(text))
                {
                    if (m.Length == 0 || !rule.Accepts(m.Value)) continue;
                    matches.Add(new RedactionMatch
                    {
                        Kind = RedactionMatchKind.Rule,
                        Rule = rule.Name,
                        Text = m.Value,
                        Index = m.Index,
                        Length = m.Length,
                    });
                }
            }

            foreach (var pattern in options.Patterns)
            {
                if (pattern is null) continue;
                foreach (Match m in pattern.Matches(text))
                {
                    if (m.Length == 0) continue;
                    matches.Add(new RedactionMatch
                    {
                        Kind = RedactionMatchKind.Pattern,
                        Rule = pattern.ToString(),
                        Text = m.Value,
                        Index = m.Index,
                        Length = m.Length,
                    });
                }
            }

            var comparison = options.CaseSensitiveKeywords ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (var keyword in options.Keywords)
            {
                if (string.IsNullOrEmpty(keyword)) continue;
                int from = 0;
                while (from <= text.Length - keyword.Length)
                {
                    int at = text.IndexOf(keyword, from, comparison);
                    if (at < 0) break;
                    matches.Add(new RedactionMatch
                    {
                        Kind = RedactionMatchKind.Keyword,
                        Rule = keyword,
                        Text = text.Substring(at, keyword.Length),
                        Index = at,
                        Length = keyword.Length,
                    });
                    from = at + keyword.Length;
                }
            }
        }

        if (options.LinePredicate is { } predicate && predicate(line))
        {
            matches.Add(new RedactionMatch
            {
                Kind = RedactionMatchKind.Predicate,
                Rule = nameof(RedactionMatchKind.Predicate),
                Text = text,
                Index = 0,
                Length = text.Length,
            });
        }

        return matches;
    }

    /// <summary>Replaces every matched span with <paramref name="mask"/>, merging overlaps.</summary>
    private static string Mask(string text, List<RedactionMatch> matches, char mask)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var chars = text.ToCharArray();
        foreach (var match in matches)
        {
            int start = Math.Max(0, match.Index);
            int end = Math.Min(chars.Length, match.Index + match.Length);
            for (int i = start; i < end; i++) chars[i] = mask;
        }
        return new string(chars);
    }

    // ---- geometry -------------------------------------------------------------------------------

    /// <summary>
    /// Picks the quads to paint: the matched words when word geometry is available and asked for,
    /// otherwise the whole line.
    /// </summary>
    private static (IReadOnlyList<IReadOnlyList<OcrPoint>> Polygons, bool WholeLine) ResolvePolygons(
        OcrLine line, List<RedactionMatch> matches, RedactionOptions options)
    {
        bool predicateMatched = false;
        foreach (var match in matches)
        {
            if (match.Kind == RedactionMatchKind.Predicate) { predicateMatched = true; break; }
        }

        if (options.Scope == RedactionScope.MatchedWords && !predicateMatched && line.Words.Count > 0
            && TryMapWordSpans(line, out var spans))
        {
            var wordPolygons = new List<IReadOnlyList<OcrPoint>>();
            for (int i = 0; i < spans.Length; i++)
            {
                if (!OverlapsAnyMatch(spans[i], matches)) continue;
                var shape = ShapeOf(line.Words[i].BoundingPolygon, line.Words[i].BoundingBox);
                if (shape is not null) wordPolygons.Add(Expand(shape, options.Padding));
            }

            if (wordPolygons.Count > 0) return (wordPolygons, false);
        }

        var lineShape = ShapeOf(line.BoundingPolygon, line.BoundingBox);
        return lineShape is null
            ? (Array.Empty<IReadOnlyList<OcrPoint>>(), true)
            : (new IReadOnlyList<OcrPoint>[] { Expand(lineShape, options.Padding) }, true);
    }

    private static bool OverlapsAnyMatch((int Start, int End) span, List<RedactionMatch> matches)
    {
        foreach (var match in matches)
        {
            int start = match.Index;
            int end = match.Index + match.Length;
            if (start < span.End && span.Start < end) return true;
        }
        return false;
    }

    /// <summary>
    /// Maps <see cref="OcrLine.Words"/> back onto character ranges of <see cref="OcrLine.Text"/> by
    /// re-splitting the text on whitespace. Returns false when the split does not reproduce the word
    /// list exactly — the caller then falls back to whole-line redaction rather than guessing at
    /// geometry it cannot trust.
    /// </summary>
    private static bool TryMapWordSpans(OcrLine line, out (int Start, int End)[] spans)
    {
        var found = new List<(int Start, int End)>(line.Words.Count);
        string text = line.Text;
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            found.Add((start, i));
        }

        if (found.Count != line.Words.Count)
        {
            spans = Array.Empty<(int, int)>();
            return false;
        }

        for (int w = 0; w < found.Count; w++)
        {
            var (start, end) = found[w];
            if (!text.AsSpan(start, end - start).SequenceEqual(line.Words[w].Text.AsSpan()))
            {
                spans = Array.Empty<(int, int)>();
                return false;
            }
        }

        spans = found.ToArray();
        return true;
    }

    /// <summary>The polygon to use for a box: its own outline, or the axis-aligned box as a rectangle.</summary>
    private static IReadOnlyList<OcrPoint>? ShapeOf(IReadOnlyList<OcrPoint> polygon, OcrBoundingBox box)
    {
        if (polygon.Count >= 3) return polygon;
        if (box.Width > 0 && box.Height > 0)
        {
            return new[]
            {
                new OcrPoint(box.MinX, box.MinY),
                new OcrPoint(box.MaxX, box.MinY),
                new OcrPoint(box.MaxX, box.MaxY),
                new OcrPoint(box.MinX, box.MaxY),
            };
        }
        return null;
    }

    /// <summary>
    /// Grows a polygon by <paramref name="padFraction"/> of its height. A quadrilateral is grown along
    /// its own axes so a rotated line stays rotated; anything else is pushed outward from its centroid.
    /// </summary>
    public static OcrPoint[] Expand(IReadOnlyList<OcrPoint> polygon, double padFraction)
    {
        var points = new OcrPoint[polygon.Count];
        for (int i = 0; i < polygon.Count; i++) points[i] = polygon[i];
        if (padFraction <= 0 || points.Length < 3) return points;

        if (points.Length == 4)
        {
            // Corner order is top-left, top-right, bottom-right, bottom-left along the text direction.
            double ux = (points[1].X - points[0].X) + (points[2].X - points[3].X);
            double uy = (points[1].Y - points[0].Y) + (points[2].Y - points[3].Y);
            double vx = (points[3].X - points[0].X) + (points[2].X - points[1].X);
            double vy = (points[3].Y - points[0].Y) + (points[2].Y - points[1].Y);

            double uLen = Math.Sqrt(ux * ux + uy * uy);
            double vLen = Math.Sqrt(vx * vx + vy * vy);
            if (uLen <= double.Epsilon || vLen <= double.Epsilon) return ExpandFromCentroid(points, padFraction);

            ux /= uLen; uy /= uLen;
            vx /= vLen; vy /= vLen;

            double height = vLen / 2.0;   // mean of the two side lengths
            double pad = padFraction * height;
            if (pad <= 0) return points;

            var expanded = new OcrPoint[4];
            expanded[0] = new OcrPoint(points[0].X - pad * (ux + vx), points[0].Y - pad * (uy + vy));
            expanded[1] = new OcrPoint(points[1].X + pad * (ux - vx), points[1].Y + pad * (uy - vy));
            expanded[2] = new OcrPoint(points[2].X + pad * (ux + vx), points[2].Y + pad * (uy + vy));
            expanded[3] = new OcrPoint(points[3].X - pad * (ux - vx), points[3].Y - pad * (uy - vy));
            return expanded;
        }

        return ExpandFromCentroid(points, padFraction);
    }

    private static OcrPoint[] ExpandFromCentroid(OcrPoint[] points, double padFraction)
    {
        var box = OcrBoundingBox.FromPoints(points);
        double pad = padFraction * box.Height;
        if (pad <= 0) return points;

        double cx = 0, cy = 0;
        foreach (var p in points) { cx += p.X; cy += p.Y; }
        cx /= points.Length;
        cy /= points.Length;

        var expanded = new OcrPoint[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            double dx = points[i].X - cx;
            double dy = points[i].Y - cy;
            double len = Math.Sqrt(dx * dx + dy * dy);
            expanded[i] = len <= double.Epsilon
                ? points[i]
                : new OcrPoint(points[i].X + pad * dx / len, points[i].Y + pad * dy / len);
        }
        return expanded;
    }

    // ---- painting -------------------------------------------------------------------------------

    private static void Paint(Image<Rgb24> image, IReadOnlyList<OcrPoint> polygon, RedactionOptions options)
    {
        switch (options.Style)
        {
            case RedactionStyle.Blur:
                BlurPolygon(image, polygon, options.BlurRadius);
                break;
            case RedactionStyle.Pixelate:
                PixelatePolygon(image, polygon, options.PixelateBlockSize);
                break;
            default:
                FillPolygon(image, polygon, options.FillColor);
                break;
        }
    }

    /// <summary>Fills the polygon with a solid colour (even-odd scanline rasterization).</summary>
    public static void FillPolygon(Image<Rgb24> image, IReadOnlyList<OcrPoint> polygon, Rgb24 color)
    {
        ForEachSpan(polygon, image.Width, image.Height, (y, x0, x1) =>
        {
            for (int x = x0; x <= x1; x++) image[x, y] = color;
        });
    }

    private static void BlurPolygon(Image<Rgb24> image, IReadOnlyList<OcrPoint> polygon, int radius)
    {
        if (!TryGetBounds(polygon, image.Width, image.Height, out int x0, out int y0, out int w, out int h)) return;

        var source = ReadRect(image, x0, y0, w, h);
        var blurred = BoxBlur(source, w, h, radius);

        ForEachSpan(polygon, image.Width, image.Height, (y, xa, xb) =>
        {
            int row = (y - y0) * w;
            for (int x = xa; x <= xb; x++)
            {
                int o = (row + (x - x0)) * 3;
                image[x, y] = new Rgb24(blurred[o], blurred[o + 1], blurred[o + 2]);
            }
        });
    }

    private static void PixelatePolygon(Image<Rgb24> image, IReadOnlyList<OcrPoint> polygon, int block)
    {
        if (!TryGetBounds(polygon, image.Width, image.Height, out int x0, out int y0, out int w, out int h)) return;

        var source = ReadRect(image, x0, y0, w, h);
        int cols = (w + block - 1) / block;
        int rows = (h + block - 1) / block;
        var sums = new long[cols * rows * 3];
        var counts = new int[cols * rows];

        for (int y = 0; y < h; y++)
        {
            int by = y / block;
            for (int x = 0; x < w; x++)
            {
                int cell = by * cols + x / block;
                int o = (y * w + x) * 3;
                sums[cell * 3] += source[o];
                sums[cell * 3 + 1] += source[o + 1];
                sums[cell * 3 + 2] += source[o + 2];
                counts[cell]++;
            }
        }

        ForEachSpan(polygon, image.Width, image.Height, (y, xa, xb) =>
        {
            int by = (y - y0) / block;
            for (int x = xa; x <= xb; x++)
            {
                int cell = by * cols + (x - x0) / block;
                int n = counts[cell];
                if (n == 0) continue;
                image[x, y] = new Rgb24(
                    (byte)(sums[cell * 3] / n),
                    (byte)(sums[cell * 3 + 1] / n),
                    (byte)(sums[cell * 3 + 2] / n));
            }
        });
    }

    private static bool TryGetBounds(IReadOnlyList<OcrPoint> polygon, int imageWidth, int imageHeight,
        out int x0, out int y0, out int width, out int height)
    {
        x0 = y0 = width = height = 0;
        if (polygon.Count < 3) return false;

        var box = OcrBoundingBox.FromPoints(polygon);
        x0 = Math.Max(0, (int)Math.Floor(box.MinX));
        y0 = Math.Max(0, (int)Math.Floor(box.MinY));
        int x1 = Math.Min(imageWidth - 1, (int)Math.Ceiling(box.MaxX));
        int y1 = Math.Min(imageHeight - 1, (int)Math.Ceiling(box.MaxY));
        if (x1 < x0 || y1 < y0) return false;

        width = x1 - x0 + 1;
        height = y1 - y0 + 1;
        return true;
    }

    private static byte[] ReadRect(Image<Rgb24> image, int x0, int y0, int w, int h)
    {
        var buffer = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 3;
            for (int x = 0; x < w; x++)
            {
                var px = image[x0 + x, y0 + y];
                int o = row + x * 3;
                buffer[o] = px.R;
                buffer[o + 1] = px.G;
                buffer[o + 2] = px.B;
            }
        }
        return buffer;
    }

    /// <summary>Separable box blur over an RGB byte buffer, clamped at the buffer's edges.</summary>
    private static byte[] BoxBlur(byte[] rgb, int w, int h, int radius)
    {
        var horizontal = new byte[rgb.Length];
        var prefix = new int[(Math.Max(w, h) + 1) * 3];

        for (int y = 0; y < h; y++)
        {
            BuildPrefix(rgb, y * w * 3, 1, w, prefix);
            for (int x = 0; x < w; x++)
            {
                int a = Math.Max(0, x - radius);
                int b = Math.Min(w - 1, x + radius);
                int n = b - a + 1;
                int o = (y * w + x) * 3;
                for (int c = 0; c < 3; c++)
                {
                    horizontal[o + c] = (byte)((prefix[(b + 1) * 3 + c] - prefix[a * 3 + c]) / n);
                }
            }
        }

        var result = new byte[rgb.Length];
        for (int x = 0; x < w; x++)
        {
            BuildPrefix(horizontal, x * 3, w, h, prefix);
            for (int y = 0; y < h; y++)
            {
                int a = Math.Max(0, y - radius);
                int b = Math.Min(h - 1, y + radius);
                int n = b - a + 1;
                int o = (y * w + x) * 3;
                for (int c = 0; c < 3; c++)
                {
                    result[o + c] = (byte)((prefix[(b + 1) * 3 + c] - prefix[a * 3 + c]) / n);
                }
            }
        }
        return result;
    }

    /// <summary>Running channel sums of <paramref name="count"/> samples taken every <paramref name="strideSamples"/> pixels.</summary>
    private static void BuildPrefix(byte[] data, int start, int strideSamples, int count, int[] prefix)
    {
        prefix[0] = prefix[1] = prefix[2] = 0;
        int index = start;
        for (int i = 0; i < count; i++)
        {
            prefix[(i + 1) * 3] = prefix[i * 3] + data[index];
            prefix[(i + 1) * 3 + 1] = prefix[i * 3 + 1] + data[index + 1];
            prefix[(i + 1) * 3 + 2] = prefix[i * 3 + 2] + data[index + 2];
            index += strideSamples * 3;
        }
    }

    /// <summary>
    /// Even-odd scanline rasterization of an arbitrary polygon: invokes <paramref name="onSpan"/> with
    /// (y, inclusive x-start, inclusive x-end) for every covered run, already clipped to the image. A
    /// pixel belongs to the polygon when its centre does, which is what keeps a rotated quad's edges
    /// from bleeding a pixel wider than the shape.
    /// </summary>
    public static void ForEachSpan(IReadOnlyList<OcrPoint> polygon, int imageWidth, int imageHeight, Action<int, int, int> onSpan)
    {
        int n = polygon.Count;
        if (n < 3 || imageWidth <= 0 || imageHeight <= 0) return;

        var box = OcrBoundingBox.FromPoints(polygon);
        int yStart = Math.Max(0, (int)Math.Floor(box.MinY));
        int yEnd = Math.Min(imageHeight - 1, (int)Math.Ceiling(box.MaxY));
        if (yStart > yEnd) return;

        var crossings = new double[n];
        for (int y = yStart; y <= yEnd; y++)
        {
            double scan = y + 0.5;
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % n];
                // Half-open rule: an edge counts when the scanline is at or above its lower endpoint and
                // strictly below its upper one, so shared vertices are counted exactly once.
                if ((a.Y <= scan && b.Y > scan) || (b.Y <= scan && a.Y > scan))
                {
                    crossings[count++] = a.X + (scan - a.Y) / (b.Y - a.Y) * (b.X - a.X);
                }
            }
            if (count < 2) continue;

            Array.Sort(crossings, 0, count);
            for (int i = 0; i + 1 < count; i += 2)
            {
                int x0 = Math.Max(0, (int)Math.Ceiling(crossings[i] - 0.5));
                int x1 = Math.Min(imageWidth - 1, (int)Math.Floor(crossings[i + 1] - 0.5));
                if (x0 <= x1) onSpan(y, x0, x1);
            }
        }
    }
}
