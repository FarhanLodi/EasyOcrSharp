using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Pdf.Internal;

/// <summary>
/// What <see cref="PdfRasterizer.ReadPages"/> needs to know about one call: the resolution (or
/// <see cref="PdfOcrOptions.AutoDpi"/>), the detector canvas auto DPI aims for, the guards, whether to read the
/// embedded text layer, and whether a page whose embedded text is accepted must still be rendered.
/// </summary>
/// <param name="Dpi">Fixed resolution, or <see cref="PdfOcrOptions.AutoDpi"/> to choose one per page.</param>
/// <param name="CanvasSize">The CRAFT detector's longest-side cap that auto DPI renders up to.</param>
/// <param name="MaxPages">Page-count guard (0 = unlimited).</param>
/// <param name="MaxPagePixels">Per-page pixel guard (0 = unlimited).</param>
/// <param name="TextLayer">Whether, and how, to accept a page's embedded text instead of OCR.</param>
/// <param name="ImagesRequired">
/// Render every page even when its embedded text is accepted — a searchable PDF needs the page image either way.
/// </param>
internal readonly record struct PdfReadSettings(
    int Dpi,
    int CanvasSize,
    int MaxPages,
    long MaxPagePixels,
    PdfTextLayerMode TextLayer,
    bool ImagesRequired)
{
    /// <summary>Settings for one public PDF call.</summary>
    public static PdfReadSettings From(PdfOcrOptions pdfOptions, RecognitionOptions? recognition, bool imagesRequired)
        => new(
            pdfOptions.Dpi,
            recognition?.Detection.CanvasSize ?? DetectionOptions.Default.CanvasSize,
            pdfOptions.MaxPages,
            pdfOptions.MaxPagePixels,
            pdfOptions.TextLayer,
            imagesRequired);
}

/// <summary>
/// One page produced by <see cref="PdfRasterizer.ReadPages"/>: its rendered image, and/or the lines read from its
/// embedded text layer. The consumer owns it and must dispose it.
/// </summary>
internal sealed class RenderedPage : IDisposable
{
    /// <summary>1-based page number.</summary>
    public required int PageNumber { get; init; }

    /// <summary>Total number of pages in the document.</summary>
    public required int PageCount { get; init; }

    /// <summary>The resolution the page was (or, when not rendered, would have been) rendered at.</summary>
    public required int Dpi { get; init; }

    /// <summary>Page width in pixels at <see cref="Dpi"/>.</summary>
    public required int PixelWidth { get; init; }

    /// <summary>Page height in pixels at <see cref="Dpi"/>.</summary>
    public required int PixelHeight { get; init; }

    /// <summary>
    /// The rendered page, or <see langword="null"/> when the embedded text layer was accepted and no image was
    /// requested.
    /// </summary>
    public Image<Rgb24>? Image { get; init; }

    /// <summary>
    /// Lines from the embedded text layer when it was accepted for this page; <see langword="null"/> means OCR it.
    /// </summary>
    public IReadOnlyList<EmbeddedLine>? EmbeddedLines { get; init; }

    /// <summary>Time spent reading the embedded characters and grouping them into lines.</summary>
    public TimeSpan EmbeddedTextDuration { get; init; }

    /// <inheritdoc />
    public void Dispose() => Image?.Dispose();
}
