using System.Diagnostics;
using Docnet.Core;
using Docnet.Core.Exceptions;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Pdf.Internal;

/// <summary>
/// Rasterizes PDF pages to images via PDFium (Docnet.Core), and optionally reads their embedded text layer.
/// Pages are rendered and handed off one at a time so peak memory stays at roughly a single page regardless
/// of document length.
/// </summary>
internal static class PdfRasterizer
{
    /// <summary>
    /// Serializes every call into PDFium. Docnet exposes it through the process-wide
    /// <see cref="DocLib.Instance"/> singleton, and PDFium itself is <b>not</b> thread-safe: two
    /// threads rasterizing at once can return a corrupted page or tear down the native library. Two
    /// concurrent OCR calls on different PDFs is an entirely ordinary thing for a caller to do (a
    /// batch, or a web request per document), so the guard belongs here rather than in the caller.
    /// </summary>
    /// <remarks>
    /// The gate is held only across the native calls. The OCR handler — by far the expensive part —
    /// runs outside it, so documents still overlap and throughput is barely affected.
    /// </remarks>
    private static readonly Lock PdfiumGate = new();

    /// <summary>
    /// Pixels per point of the reader that only measures page sizes for auto DPI. Docnet truncates page sizes to
    /// whole pixels, so a large factor keeps 0.01 pt precision; the probe never renders, so its scale costs nothing.
    /// </summary>
    private const double ProbeScale = 100.0;

    /// <summary>
    /// Lower clamp of auto DPI — large-format pages, where the canvas alone would go lower, still give the
    /// recognizer (which reads full-resolution crops) enough pixels for small print.
    /// </summary>
    internal const int AutoDpiMin = 150;

    /// <summary>Upper clamp of auto DPI — roughly where 10 pt text reaches the recognizer's 64 px line height.</summary>
    internal const int AutoDpiMax = 400;

    /// <summary>
    /// Renders each page at <paramref name="dpi"/> and invokes <paramref name="handler"/> with the
    /// 0-based index, total page count, and the rendered image (disposed automatically after the
    /// handler completes — do not keep a reference to it).
    /// </summary>
    /// <remarks>
    /// The handler-based entry point the redaction pipeline uses. It reports no per-page resolution, so
    /// <see cref="PdfOcrOptions.AutoDpi"/> renders at <see cref="PdfOcrOptions.DefaultDpi"/> here — the value
    /// the caller will lay the page out with — rather than at a resolution it cannot see.
    /// </remarks>
    /// <exception cref="EasyOcrSharpException">
    /// The input is empty, or the PDF cannot be opened/rendered (corrupt, not a PDF, or
    /// password-protected/encrypted). Exceptions thrown by <paramref name="handler"/> itself
    /// (e.g. OCR failures) are propagated unchanged.
    /// </exception>
    public static async Task ForEachPageAsync(
        byte[] pdfBytes,
        int dpi,
        int maxPages,
        long maxPagePixels,
        Func<int, int, Image<Rgb24>, Task> handler,
        CancellationToken cancellationToken)
    {
        var settings = new PdfReadSettings(
            dpi == PdfOcrOptions.AutoDpi ? PdfOcrOptions.DefaultDpi : dpi,
            DetectionOptions.Default.CanvasSize,
            maxPages,
            maxPagePixels,
            PdfTextLayerMode.Ignore,
            ImagesRequired: true);

        foreach (var page in ReadPages(pdfBytes, settings, cancellationToken))
        {
            using (page)
            {
                await handler(page.PageNumber - 1, page.PageCount, page.Image!).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Yields every page in document order, rendered at <see cref="PdfReadSettings.Dpi"/> — or at the page's own
    /// <see cref="ResolveAutoDpi"/> resolution when that is <see cref="PdfOcrOptions.AutoDpi"/>. When
    /// <see cref="PdfReadSettings.TextLayer"/> accepts a page's embedded text, the page carries
    /// <see cref="RenderedPage.EmbeddedLines"/> and is rendered only if
    /// <see cref="PdfReadSettings.ImagesRequired"/>. The consumer must dispose every yielded page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lazy and pull-based: a page is read when the consumer asks for it, so peak memory stays at roughly one page
    /// whatever the document's length, and a consumer that stops early stops the rendering. Disposing the
    /// enumerator closes the document. Every PDFium call is made under <see cref="PdfiumGate"/>; nothing is held
    /// across a <c>yield</c>, so the consumer's OCR always runs outside it.
    /// </para>
    /// <para>
    /// A Docnet reader has one fixed scale, so auto DPI keeps two: a probe that only measures page sizes, and a
    /// renderer reopened whenever a page needs a different resolution from the one before — once per document
    /// whose pages share a size.
    /// </para>
    /// </remarks>
    /// <exception cref="PdfProcessingException">
    /// The input is empty, the PDF cannot be opened or a page cannot be rendered (corrupt, not a PDF, or
    /// password-protected/encrypted), or a page-count or page-size guard is exceeded.
    /// </exception>
    public static IEnumerable<RenderedPage> ReadPages(byte[] pdfBytes, PdfReadSettings settings, CancellationToken cancellationToken)
    {
        if (pdfBytes is null || pdfBytes.Length == 0)
            throw new PdfProcessingException("The PDF input is empty. Provide the bytes of a valid PDF document.");

        bool autoDpi = settings.Dpi == PdfOcrOptions.AutoDpi;

        // PDF user space is 72 dpi; a fixed resolution scales every page by dpi / 72. Under auto DPI the first
        // reader only measures pages, so its scale is chosen for precision (Docnet truncates sizes to whole
        // pixels) — it never renders.
        IDocReader primary = Open(pdfBytes, autoDpi ? ProbeScale : settings.Dpi / 72.0);
        IDocReader? renderer = autoDpi ? null : primary;
        int rendererDpi = autoDpi ? 0 : settings.Dpi;

        try
        {
            int count = CountPages(primary, settings.MaxPages);

            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int dpi = settings.Dpi;
                if (autoDpi)
                {
                    var (widthPt, heightPt) = ReadPageSizePoints(primary, i);
                    dpi = ResolveAutoDpi(widthPt, heightPt, settings.CanvasSize);
                    if (renderer is null || rendererDpi != dpi)
                    {
                        var previous = renderer;
                        renderer = null;
                        if (previous is not null) Close(previous);
                        renderer = Open(pdfBytes, dpi / 72.0);
                        rendererDpi = dpi;
                    }
                }

                yield return ReadPage(renderer!, i, count, dpi, settings);
            }
        }
        finally
        {
            // Tearing the documents down is a native call too, so it takes the gate like the rest.
            if (renderer is not null && !ReferenceEquals(renderer, primary)) Close(renderer);
            Close(primary);
        }
    }

    /// <summary>
    /// The per-page auto DPI: <c>clamp(floor(canvasSize / longestSideInches), 150, 400)</c> — the resolution at
    /// which the page's longest side fills, without exceeding, the CRAFT detector's input canvas. See
    /// <see cref="PdfOcrOptions.AutoDpi"/> for why the canvas and why those clamps.
    /// </summary>
    /// <param name="widthPt">Page width in PDF points (1/72 in), after PDFium applied <c>/Rotate</c>.</param>
    /// <param name="heightPt">Page height in PDF points.</param>
    /// <param name="canvasSize">
    /// <see cref="DetectionOptions.CanvasSize"/> of the call; floored at 32 exactly as the detector floors it.
    /// </param>
    internal static int ResolveAutoDpi(double widthPt, double heightPt, int canvasSize)
    {
        double longestInches = Math.Max(widthPt, heightPt) / 72.0;
        if (!(longestInches > 0)) return AutoDpiMax;
        double dpi = Math.Floor(Math.Max(32, canvasSize) / longestInches);
        return (int)Math.Clamp(dpi, AutoDpiMin, AutoDpiMax);
    }

    private static IDocReader Open(byte[] pdfBytes, double scale)
    {
        // DocLib.Instance is a process-wide singleton — never dispose it here. Opening can fail on a
        // corrupt, truncated, non-PDF, or encrypted document; surface those as a typed, clear error
        // instead of leaking a PDFium/Docnet exception to the caller.
        try
        {
            lock (PdfiumGate)
            {
                return DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(scale));
            }
        }
        catch (Exception ex) when (ex is DocnetException or ArgumentException)
        {
            throw new PdfProcessingException(
                "The PDF could not be opened. It may be corrupt, not a PDF, or password-protected/encrypted.", ex);
        }
    }

    private static void Close(IDocReader reader)
    {
        lock (PdfiumGate)
        {
            reader.Dispose();
        }
    }

    private static int CountPages(IDocReader reader, int maxPages)
    {
        int count;
        try
        {
            lock (PdfiumGate)
            {
                count = reader.GetPageCount();
            }
        }
        catch (DocnetException ex)
        {
            throw new PdfProcessingException("The PDF page count could not be read; the document may be corrupt.", ex);
        }

        // PDFium opens documents whose page tree is empty and reports 0. Letting that through produced a
        // "successful" searchable PDF containing << /Type /Pages /Count 0 /Kids [] >>, which Acrobat
        // rejects as damaged -- a silent bad artifact instead of a clear error.
        if (count <= 0)
        {
            throw new PdfProcessingException("The PDF contains no pages.");
        }

        if (maxPages > 0 && count > maxPages)
        {
            throw new PdfProcessingException(
                $"The PDF has {count} pages, exceeding the limit of {maxPages} (PdfOcrOptions.MaxPages). " +
                "Raise the limit to process it, or split the document.");
        }

        return count;
    }

    private static (double WidthPt, double HeightPt) ReadPageSizePoints(IDocReader probe, int index)
    {
        try
        {
            lock (PdfiumGate)
            {
                using var pageReader = probe.GetPageReader(index);
                return (pageReader.GetPageWidth() / ProbeScale, pageReader.GetPageHeight() / ProbeScale);
            }
        }
        catch (Exception ex) when (ex is DocnetException or ArgumentException or InvalidOperationException)
        {
            throw new PdfProcessingException($"Page {index + 1} of the PDF could not be read; the document may be corrupt.", ex);
        }
    }

    /// <summary>
    /// Reads one page: its size, its embedded text when the settings ask for it, and its bitmap unless the
    /// embedded text was accepted and no image is required.
    /// </summary>
    private static RenderedPage ReadPage(IDocReader reader, int index, int count, int dpi, PdfReadSettings settings)
    {
        // Any failure here is a document/PDFium problem (kept separate from the consumer's OCR, so genuine OCR
        // errors are never mislabeled as a PDF-rendering error).
        try
        {
            byte[]? bgra = null;
            int width, height;
            List<EmbeddedLine>? embeddedLines = null;
            TimeSpan embeddedDuration = TimeSpan.Zero;

            // Only the native work — and the cheap grouping that decides whether the bitmap is needed at all —
            // is serialized; the BGRA->RGB conversion below is pure managed code and stays outside the gate to
            // keep the critical section short.
            lock (PdfiumGate)
            {
                using var pageReader = reader.GetPageReader(index);
                width = pageReader.GetPageWidth();
                height = pageReader.GetPageHeight();

                long maxPagePixels = settings.MaxPagePixels;
                if (maxPagePixels > 0 && (long)width * height > maxPagePixels)
                {
                    throw new PdfProcessingException(
                        $"Page {index + 1} renders to {width}x{height} ({(long)width * height:N0} px) at {dpi} DPI, " +
                        $"exceeding the per-page limit of {maxPagePixels:N0} px (PdfOcrOptions.MaxPageMegapixels). " +
                        "Lower the DPI or raise the limit.");
                }

                if (settings.TextLayer != PdfTextLayerMode.Ignore)
                {
                    long started = Stopwatch.GetTimestamp();
                    var chars = ReadCharacters(pageReader, dpi / 72.0);
                    embeddedLines = EmbeddedTextLayer.SelectLines(chars, settings.TextLayer, width, height);
                    embeddedDuration = Stopwatch.GetElapsedTime(started);
                }

                if (embeddedLines is null || settings.ImagesRequired)
                {
                    // PDFium emits BGRA with a transparent background. Take the raw buffer and let
                    // ConvertToRgb24 composite it over white: NaiveTransparencyRemover only rewrites
                    // FULLY transparent pixels, so partial alpha (anti-aliased glyph edges, watermarks,
                    // transparency groups) would survive it un-blended and then be rendered at full
                    // strength when the alpha channel was dropped.
                    bgra = pageReader.GetImage();
                }
            }

            return new RenderedPage
            {
                PageNumber = index + 1,
                PageCount = count,
                Dpi = dpi,
                PixelWidth = width,
                PixelHeight = height,
                Image = bgra is null ? null : ConvertToRgb24(bgra, width, height),
                EmbeddedLines = embeddedLines,
                EmbeddedTextDuration = embeddedDuration,
            };
        }
        catch (Exception ex) when (ex is DocnetException or ArgumentException or InvalidOperationException)
        {
            throw new PdfProcessingException($"Page {index + 1} of the PDF could not be rendered; the document may be corrupt.", ex);
        }
    }

    /// <summary>
    /// Reads the page's characters. Docnet reports boxes through <c>FPDF_PageToDevice</c> at the reader's scale,
    /// i.e. already in rendered-page pixels with the page's <c>/Rotate</c> applied; only the font size (points)
    /// needs converting. Docnet enumerates lazily over the open text page, so this must run to completion inside
    /// the gate and before the page reader is disposed.
    /// </summary>
    private static List<PdfTextChar> ReadCharacters(IPageReader pageReader, double pixelsPerPoint)
    {
        var chars = new List<PdfTextChar>();
        foreach (var c in pageReader.GetCharacters())
        {
            var box = c.Box;
            chars.Add(new PdfTextChar(
                EmbeddedTextLayer.NormalizeCharacter(c.Char),
                Math.Min(box.Left, box.Right),
                Math.Min(box.Top, box.Bottom),
                Math.Max(box.Left, box.Right),
                Math.Max(box.Top, box.Bottom),
                c.FontSize > 0 ? c.FontSize * pixelsPerPoint : 0));
        }
        return chars;
    }

    /// <summary>
    /// Converts PDFium's BGRA buffer to RGB, compositing over white.
    /// <para>
    /// <c>CloneAs&lt;Rgb24&gt;</c> alone <i>drops</i> the alpha channel instead of blending it, and
    /// <c>NaiveTransparencyRemover</c> only rewrites FULLY transparent pixels. Every partially transparent
    /// pixel therefore kept its un-blended source colour at full strength: anti-aliased glyph edges on a
    /// born-digital page hard-thresholded to solid black, and — the visible failure — a 20%-opacity watermark
    /// or transparency-group overlay rasterized at 100%, so OCR read the watermark over the real content and
    /// the searchable PDF embedded that page image.
    /// </para>
    /// <para>
    /// Composited in place over the caller's buffer, so no full-size Bgra32 image is materialised first.
    /// </para>
    /// </summary>
    private static Image<Rgb24> ConvertToRgb24(byte[] bgra, int width, int height)
    {
        long pixelCount = (long)width * height;
        if (pixelCount > Array.MaxLength)
        {
            throw new PdfProcessingException(
                $"Page renders to {width}x{height} ({pixelCount:N0} px), which exceeds the maximum addressable " +
                "pixel buffer. Lower PdfOcrOptions.Dpi.");
        }

        // PDFium hands back 4 bytes per pixel; a short buffer means the render did not produce the page we
        // were told it would, and reading past it would be an out-of-range crash deep in the loop.
        if (bgra.Length < pixelCount * 4)
        {
            throw new PdfProcessingException(
                $"The renderer returned {bgra.Length:N0} bytes for a {width}x{height} page, which needs " +
                $"{pixelCount * 4:N0}. The document may be corrupt.");
        }

        var rgb = new Rgb24[(int)pixelCount];

        for (int i = 0, p = 0; i < rgb.Length; i++, p += 4)
        {
            byte b = bgra[p], g = bgra[p + 1], r = bgra[p + 2], a = bgra[p + 3];
            if (a == 255)
            {
                rgb[i] = new Rgb24(r, g, b);
                continue;
            }

            // out = src*a + 255*(1-a), rounded. a == 0 yields white, which is what OCR wants to see.
            int inv = 255 - a;
            rgb[i] = new Rgb24(
                (byte)((r * a + 255 * inv + 127) / 255),
                (byte)((g * a + 255 * inv + 127) / 255),
                (byte)((b * a + 255 * inv + 127) / 255));
        }

        return Image.LoadPixelData<Rgb24>(rgb, width, height);
    }
}
