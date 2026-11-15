using Microsoft.Extensions.Logging;

namespace EasyOcrSharp.Pdf;

/// <summary>
/// Controls whether the invisible text layer of a searchable PDF embeds a real Unicode font instead of
/// the standard base-14 Helvetica face.
/// </summary>
public enum PdfTextLayerFontMode
{
    /// <summary>
    /// Embed a font only when the recognized text needs one, i.e. when it contains a character outside
    /// Latin-1. Latin-only documents keep the compact, self-contained Helvetica layer. This is the
    /// default and never changes the output of a document that was already fully searchable.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Never embed a font. The text layer is always Helvetica with <c>WinAnsiEncoding</c>, and every
    /// character outside Latin-1 is replaced with <c>?</c> — searchable only for Latin scripts, but with
    /// the smallest possible output and no dependency on the fonts installed on the machine.
    /// </summary>
    Never = 1,

    /// <summary>
    /// Always embed a font when one can be found, even for Latin-only text. Useful when every page of a
    /// batch must be built the same way regardless of what the OCR happened to return.
    /// </summary>
    Always = 2,
}

/// <summary>Progress for PDF processing, reported per page via <see cref="PdfOcrOptions.Progress"/>.</summary>
/// <param name="PageNumber">1-based page being processed.</param>
/// <param name="PageCount">Total pages in the document.</param>
public readonly record struct PdfPageProgress(int PageNumber, int PageCount)
{
    /// <summary>Completion fraction (0–1).</summary>
    public double Fraction => PageCount > 0 ? (double)PageNumber / PageCount : 0;
}

/// <summary>Options for rasterizing and OCR-ing PDFs.</summary>
public sealed class PdfOcrOptions
{
    /// <summary>
    /// The <see cref="Dpi"/> value that picks a rendering resolution for each page from its size.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each page renders at <c>clamp(CanvasSize / longestSideInches, 150, 400)</c> DPI, where
    /// <c>CanvasSize</c> is the CRAFT detector's input cap — <see cref="Models.DetectionOptions.CanvasSize"/>
    /// of the <see cref="Models.RecognitionOptions"/> passed to the call, 2560 by default. A US Letter page
    /// renders at 232 DPI, A4 at 218, A5 at 309, a 3 × 8 in receipt at 320, a business card at 400; A3 and
    /// larger drop toward the 150 DPI floor.
    /// </para>
    /// <para>
    /// Why the canvas: the detector shrinks any page whose longest side exceeds <c>CanvasSize</c> before it
    /// looks for text, and (at the default <see cref="Models.DetectionOptions.MagRatio"/> of 1) never enlarges
    /// a smaller one. A fixed 200 DPI therefore starves the detector on small pages — a 3 × 8 in receipt
    /// renders at 1600 px and is detected at 1600 px, leaving 960 px of canvas unused — and on large pages
    /// renders pixels the detector immediately discards. Matching the render to the canvas gives detection
    /// every pixel it can use; raising <c>CanvasSize</c> for small print on large pages raises the render
    /// resolution with it. On Letter and A4 the result is only a little above the 200 DPI default; the gain is
    /// largest on small pages.
    /// </para>
    /// <para>
    /// Recognition, unlike detection, reads its crops from the full-resolution page and scales each line to a
    /// 64 px height. The 400 DPI ceiling is roughly where 10 pt text reaches that height, so more pixels would
    /// only be scaled back down; the 150 DPI floor keeps large-format pages (A3 and up), where the canvas alone
    /// would go lower, from starving the recognizer of the pixels it does use.
    /// </para>
    /// <para>
    /// Honoured by <see cref="PdfOcrExtensions.ExtractTextFromPdfAsync(Services.IEasyOcrService, byte[], IEnumerable{string}, Models.RecognitionOptions, PdfOcrOptions, CancellationToken)"/>,
    /// <see cref="PdfOcrExtensions.ExtractTextFromPdfPagesAsync(Services.IEasyOcrService, byte[], IEnumerable{string}, Models.RecognitionOptions, PdfOcrOptions, CancellationToken)"/>
    /// and <see cref="PdfOcrExtensions.CreateSearchablePdfAsync(Services.IEasyOcrService, byte[], IEnumerable{string}, Models.RecognitionOptions, PdfOcrOptions, CancellationToken)"/>
    /// and their overloads; <see cref="PdfPageResult.Dpi"/> reports the resolution each page got. The
    /// redaction methods, which report no per-page resolution, render at the default 200 DPI when it is set.
    /// </para>
    /// </remarks>
    public const int AutoDpi = 0;

    /// <summary>The resolution used when <see cref="Dpi"/> is left alone.</summary>
    internal const int DefaultDpi = 200;

    /// <summary>
    /// Rendering resolution. Higher = better OCR accuracy but slower and larger searchable PDFs.
    /// 200–300 is a good range for scanned documents. Default 200.
    /// <para>
    /// Set to <see cref="AutoDpi"/> (0) to choose each page's resolution from its size so the text detector
    /// gets exactly as many pixels as it can use — see <see cref="AutoDpi"/> for the formula. Otherwise the
    /// value must be between 36 and 1200.
    /// </para>
    /// </summary>
    public int Dpi { get; set; } = DefaultDpi;

    /// <summary>
    /// Whether to read a born-digital page's embedded text layer instead of OCR-ing it. Default
    /// <see cref="PdfTextLayerMode.Ignore"/>: every page is OCR'd, exactly as before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With <see cref="PdfTextLayerMode.PreferEmbedded"/> or <see cref="PdfTextLayerMode.Auto"/>, a page whose
    /// embedded text passes the quality gate (see <see cref="PdfTextLayerMode"/>) is read directly from
    /// PDFium's character boxes: exact text, a confidence of 1.0, and lines and words rebuilt in the same
    /// rendered-page pixel space the OCR path reports at <see cref="PdfPageResult.Dpi"/>, so downstream code
    /// cannot tell the coordinates apart. Such a page reports
    /// <see cref="PdfPageResult.Source"/> = <see cref="PdfPageSource.EmbeddedText"/>. Text extraction skips
    /// rendering it altogether, which is where the speed comes from; a searchable PDF still renders it for
    /// the page image and writes the embedded text as the page's single invisible layer.
    /// </para>
    /// <para>
    /// The caller's <see cref="Models.RecognitionOptions"/> still shape the result:
    /// <see cref="Models.RecognitionOptions.Grouping"/> (word, line or paragraph),
    /// <see cref="Models.RecognitionOptions.WordLevelDetail"/>, <see cref="Models.RecognitionOptions.Region"/>
    /// and <see cref="Models.RecognitionOptions.ReadingDirection"/> apply to embedded pages too. Options that
    /// only steer the models — detection thresholds, decoder, allow/block lists, preprocessing — have nothing
    /// to act on and are ignored there. Lines keep the document's content order, which for born-digital PDFs
    /// is the author's reading order.
    /// </para>
    /// </remarks>
    public PdfTextLayerMode TextLayer { get; set; } = PdfTextLayerMode.Ignore;

    /// <summary>
    /// JPEG quality (1–100) for the page images embedded in a <i>searchable</i> PDF. Lower = smaller
    /// file. Default 75. Ignored when only extracting text.
    /// </summary>
    public int JpegQuality { get; set; } = 75;

    /// <summary>
    /// Maximum number of pages to accept. A PDF with more pages is rejected before any page is rendered —
    /// a guard against a malicious document forcing unbounded CPU/time. Default 5000. Set to 0 for no limit.
    /// </summary>
    public int MaxPages { get; set; } = 5000;

    /// <summary>
    /// Maximum rendered megapixels per page (width × height at the chosen <see cref="Dpi"/>). A page that
    /// would exceed this is rejected before its bitmap is allocated — a guard against a large page box at
    /// high DPI exhausting memory. Default 200 (≈ an A3 page at 600 DPI). Set to 0 for no limit.
    /// </summary>
    public int MaxPageMegapixels { get; set; } = 200;

    /// <summary>Optional per-page progress callback.</summary>
    public IProgress<PdfPageProgress>? Progress { get; set; }

    /// <summary>
    /// How the invisible text layer of a <i>searchable</i> PDF is encoded. Default
    /// <see cref="PdfTextLayerFontMode.Auto"/>: Latin-1 text keeps the base-14 Helvetica layer, anything
    /// else switches to an embedded <c>/Identity-H</c> composite font so Chinese, Japanese, Korean,
    /// Arabic, Devanagari, Thai, Greek and Cyrillic text becomes selectable and searchable.
    /// Ignored when only extracting text.
    /// </summary>
    public PdfTextLayerFontMode TextLayerFont { get; set; } = PdfTextLayerFontMode.Auto;

    /// <summary>
    /// Path to the <c>.ttf</c> or <c>.ttc</c> font to embed in the text layer when one is needed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The package deliberately ships no font — a font covering CJK is tens of megabytes — so the font
    /// comes either from here or from the machine. When this is <see langword="null"/> (the default) the
    /// installed fonts are searched: on Windows <c>%WINDIR%\Fonts</c> and
    /// <c>%LOCALAPPDATA%\Microsoft\Windows\Fonts</c>; on Linux <c>/usr/share/fonts</c>,
    /// <c>/usr/local/share/fonts</c>, <c>~/.local/share/fonts</c> and <c>~/.fonts</c>; on macOS
    /// <c>/System/Library/Fonts</c>, <c>/System/Library/Fonts/Supplemental</c>, <c>/Library/Fonts</c> and
    /// <c>~/Library/Fonts</c>. Well-known faces are tried in order of increasing size — the broad
    /// Latin/Greek/Cyrillic/Arabic faces first, then Indic and Thai, then the large CJK faces — and the
    /// first one whose character map covers every character of the document wins, so the script at hand
    /// picks the font.
    /// </para>
    /// <para>
    /// Only <c>glyf</c>-flavoured TrueType is supported (<c>.ttf</c>, or the first face of a <c>.ttc</c>);
    /// a CFF-flavoured <c>.otf</c> is rejected because PDF cannot carry it in <c>/FontFile2</c>. A font
    /// set here is used as given, even if it does not cover the whole document — characters it cannot
    /// render are dropped from the (invisible) layer. Setting a path does not by itself force embedding:
    /// use <see cref="TextLayerFont"/> = <see cref="PdfTextLayerFontMode.Always"/> for that.
    /// </para>
    /// <para>
    /// If no font can be resolved, nothing throws: the standard Helvetica layer is written, the result
    /// reports <see cref="PdfTextLayerFontStatus.Unavailable"/> and <see cref="Logger"/> gets a warning.
    /// </para>
    /// </remarks>
    public string? TextLayerFontPath { get; set; }

    /// <summary>
    /// Optional logger for diagnostics that have no other home — currently the searchable-PDF text layer
    /// falling back to Helvetica because no suitable font is installed, and characters dropped because
    /// the embedded font has no glyph for them.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>Per-page pixel budget derived from <see cref="MaxPageMegapixels"/> (0 = unlimited).</summary>
    internal long MaxPagePixels => MaxPageMegapixels <= 0 ? 0 : (long)MaxPageMegapixels * 1_000_000L;

    internal void Validate()
    {
        if (Dpi != AutoDpi && Dpi is < 36 or > 1200)
            throw new ArgumentOutOfRangeException(nameof(Dpi), Dpi, "Dpi must be 0 (PdfOcrOptions.AutoDpi) or between 36 and 1200.");
        if (!Enum.IsDefined(TextLayer))
            throw new ArgumentOutOfRangeException(nameof(TextLayer), TextLayer, "TextLayer is not a defined PdfTextLayerMode value.");
        if (JpegQuality is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(JpegQuality), JpegQuality, "JpegQuality must be between 1 and 100.");
        if (MaxPages < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPages), MaxPages, "MaxPages must be 0 (unlimited) or positive.");
        if (MaxPageMegapixels < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPageMegapixels), MaxPageMegapixels, "MaxPageMegapixels must be 0 (unlimited) or positive.");
        if (!Enum.IsDefined(TextLayerFont))
            throw new ArgumentOutOfRangeException(nameof(TextLayerFont), TextLayerFont, "TextLayerFont is not a defined PdfTextLayerFontMode value.");
        if (TextLayerFontPath is not null && string.IsNullOrWhiteSpace(TextLayerFontPath))
            throw new ArgumentException("TextLayerFontPath must be null or a real path.", nameof(TextLayerFontPath));
    }
}
