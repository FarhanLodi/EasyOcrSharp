using System.Globalization;
using System.Text;
using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure.Export;
using EasyImageSharp;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Structure;

/// <summary>
/// Renders a full parsed <see cref="StructureResult"/> document to a single, self-contained, semantic
/// HTML5 document — the C# parity for Python PP-StructureV3's <c>save_to_html()</c>. The exporter walks
/// <see cref="StructureResult.Blocks"/> in reading order and maps each block kind to the most natural
/// HTML element (headings, paragraphs, tables, MathJax-wrapped formulas, figure placeholders), escaping
/// all recovered text while emitting pre-built table markup verbatim. Both string-returning
/// <c>ToHtml</c> and file-writing <c>SaveAsHtml</c> overloads are provided, each with an image-aware variant
/// that embeds the actual figure/chart/seal pixels. The exporter is pure (apart from <c>SaveAsHtml</c>'s
/// single write) and Native-AOT friendly.
/// </summary>
internal static class StructureHtmlExporter
{
    // Derived from the assembly version so the generated "generator" meta tag never drifts from the package version.
    private static readonly string Generator =
        "EasyOcrSharp " + (typeof(StructureHtmlExporter).Assembly.GetName().Version?.ToString(3) ?? "");

    /// <summary>
    /// Renders the analyzed document as a single, well-formed HTML5 document. The structure is
    /// <c>&lt;!DOCTYPE html&gt;&lt;html&gt;&lt;head&gt;…&lt;/head&gt;&lt;body&gt;…&lt;/body&gt;&lt;/html&gt;</c>
    /// with a UTF-8 <c>&lt;meta charset&gt;</c>. Blocks are walked in reading order
    /// (<see cref="StructureBlock.Order"/>) and rendered by kind:
    /// <list type="bullet">
    ///   <item>document title → <c>&lt;h1&gt;</c>, section/paragraph titles → <c>&lt;h2&gt;</c>, abstract → <c>&lt;h2&gt;</c>.</item>
    ///   <item>body text / lists / captions / references → <c>&lt;p&gt;</c> (text is HTML-escaped).</item>
    ///   <item>tables → the block's recovered <c>&lt;table&gt;</c> HTML, emitted verbatim when well-formed (otherwise escaped as a fallback).</item>
    ///   <item>formulas → a MathJax display block <c>\[ … \]</c> with the raw LaTeX preserved in a <c>data-latex</c> attribute.</item>
    ///   <item>figures / charts / seals → a <c>&lt;figure&gt;</c> placeholder carrying the region label and bbox in <c>data-*</c> attributes.</item>
    /// </list>
    /// The MathJax CDN <c>&lt;script&gt;</c> is included in the head (behind a comment) so formulas render when
    /// the document is opened online; it is optional and the document is valid without it.
    /// </summary>
    /// <param name="result">The analyzed document to render. Must not be <c>null</c>.</param>
    /// <param name="title">Optional document title used for the <c>&lt;title&gt;</c> element; defaults to the first title block's text, or "Document".</param>
    /// <returns>The complete HTML document as a UTF-8-encodable string.</returns>
    public static string ToHtml(this StructureResult result, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return RenderDocument(result, sourceImage: null, title);
    }

    /// <summary>
    /// Renders the analyzed document as a single, self-contained HTML5 document, embedding the actual pixels of
    /// each figure/chart/seal region as an inline <c>data:image/png;base64,…</c> <c>&lt;img&gt;</c> (instead of
    /// the bbox placeholder). All other block kinds render exactly as in <see cref="ToHtml(StructureResult, string?)"/>.
    /// </summary>
    /// <param name="result">The analyzed document to render. Must not be <c>null</c>.</param>
    /// <param name="sourceImage">
    /// The SAME image that was analyzed to produce <paramref name="result"/>. Block <c>Bounds</c> are
    /// interpreted in this image's pixel space; passing a different image yields wrong crops. Must not be <c>null</c>.
    /// </param>
    /// <param name="title">Optional document title; see <see cref="ToHtml(StructureResult, string?)"/>.</param>
    /// <returns>The complete HTML document as a UTF-8-encodable string, with embedded figure images.</returns>
    public static string ToHtml(this StructureResult result, Image<Rgb24> sourceImage, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sourceImage);
        return RenderDocument(result, sourceImage, title);
    }

    private static string RenderDocument(StructureResult result, Image<Rgb24>? sourceImage, string? title)
    {
        sourceImage = ImageMatchingResult(result, sourceImage);
        var ordered = result.Blocks.OrderBy(b => b.Order).ToList();
        string docTitle = title ?? FirstTitle(ordered) ?? "Document";

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("  <meta name=\"generator\" content=\"").Append(Esc(Generator)).AppendLine("\">");
        sb.Append("  <title>").Append(Esc(docTitle)).AppendLine("</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body { font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif; line-height: 1.5; margin: 2rem auto; max-width: 50rem; padding: 0 1rem; }");
        sb.AppendLine("    table { border-collapse: collapse; margin: 1rem 0; }");
        sb.AppendLine("    table, th, td { border: 1px solid #bbb; }");
        sb.AppendLine("    th, td { padding: 0.3rem 0.6rem; text-align: left; }");
        sb.AppendLine("    figure { border: 1px dashed #bbb; padding: 1rem; color: #666; text-align: center; }");
        sb.AppendLine("    .formula { overflow-x: auto; }");
        sb.AppendLine("  </style>");
        // MathJax is optional: it renders the \( … \) / \[ … \] formula markup when the document is opened
        // online. The document remains valid HTML (formulas show as raw LaTeX) when offline or if removed.
        sb.AppendLine("  <!-- MathJax (optional): renders LaTeX formulas when online. Remove if not needed. -->");
        sb.AppendLine("  <script>window.MathJax = { tex: { inlineMath: [['\\\\(', '\\\\)']], displayMath: [['\\\\[', '\\\\]']] } };</script>");
        sb.AppendLine("  <script async src=\"https://cdn.jsdelivr.net/npm/mathjax@3/es5/tex-mml-chtml.js\"></script>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        foreach (var block in ordered)
        {
            string chunk = RenderHtml(block, sourceImage);
            if (chunk.Length == 0) continue;
            sb.Append("  ").AppendLine(chunk);
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    /// <summary>
    /// Renders the document via <see cref="ToHtml(StructureResult, string?)"/> and writes it to
    /// <paramref name="path"/> as UTF-8 (without a byte-order mark, so the file is a clean, portable HTML document).
    /// </summary>
    /// <param name="result">The analyzed document to render. Must not be <c>null</c>.</param>
    /// <param name="path">Destination file path. Must not be <c>null</c> or empty.</param>
    /// <param name="title">Optional document title; see <see cref="ToHtml(StructureResult, string?)"/>.</param>
    public static void SaveAsHtml(this StructureResult result, string path, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.WriteAllText(path, result.ToHtml(title), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Renders the document via <see cref="ToHtml(StructureResult, Image{Rgb24}, string?)"/> (embedding figure
    /// pixels) and writes it to <paramref name="path"/> as UTF-8 (without a byte-order mark).
    /// </summary>
    /// <param name="result">The analyzed document to render. Must not be <c>null</c>.</param>
    /// <param name="sourceImage">The SAME image that was analyzed; block bounds are in its pixel space. Must not be <c>null</c>.</param>
    /// <param name="path">Destination file path. Must not be <c>null</c> or empty.</param>
    /// <param name="title">Optional document title; see <see cref="ToHtml(StructureResult, string?)"/>.</param>
    public static void SaveAsHtml(this StructureResult result, Image<Rgb24> sourceImage, string path, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sourceImage);
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.WriteAllText(path, result.ToHtml(sourceImage, title), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Renders a single block to its HTML fragment (no surrounding whitespace); empty when nothing to emit.
    /// </summary>
    private static string RenderHtml(StructureBlock block, Image<Rgb24>? sourceImage)
    {
        switch (block.Type)
        {
            case StructureBlockType.DocTitle:
                return Heading(block.Text, level: 1);

            case StructureBlockType.Title:
            case StructureBlockType.Abstract:
                return Heading(block.Text, level: 2);

            case StructureBlockType.Table:
                return RenderTable(block);

            case StructureBlockType.Formula:
                return RenderFormula(block);

            case StructureBlockType.Figure:
            case StructureBlockType.Chart:
            case StructureBlockType.Seal:
                return RenderFigure(block, sourceImage);

            default:
                // Text, Paragraph, List, captions, headers/footers, references, etc.: plain paragraph text.
                var text = block.Text?.Trim();
                return string.IsNullOrEmpty(text) ? string.Empty : $"<p>{Esc(text)}</p>";
        }
    }

    /// <summary>
    /// Formats an <c>&lt;hN&gt;</c> heading; empty when the text is blank.
    /// </summary>
    private static string Heading(string? text, int level)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return $"<h{level}>{Esc(text.Trim())}</h{level}>";
    }

    /// <summary>
    /// Emits the table block's recovered HTML verbatim when it is a well-formed, table-rooted fragment;
    /// otherwise falls back to the recovered text (escaped) or, failing that, an empty-table placeholder.
    /// The pre-built markup is NOT escaped — it is already markup — but it is validated first.
    /// </summary>
    private static string RenderTable(StructureBlock block)
    {
        // The recognizer emits PaddleOCR's <html><body><table>…</table></body></html> envelope, which is what
        // StructureBlock.TableHtml carries. Only the <table> fragment may be spliced into the page we build,
        // so unwrap first — validating the envelope as a table fragment would reject every real table.
        var html = ExtractTableFragment(block.TableHtml);
        if (!string.IsNullOrEmpty(html) && IsWellFormedTable(html))
        {
            return html;
        }

        // Not a usable <table> fragment: degrade gracefully rather than inject broken markup.
        var text = block.Text?.Trim();
        return string.IsNullOrEmpty(text)
            ? "<table></table>"
            : $"<p>{Esc(text)}</p>";
    }

    /// <summary>
    /// Renders a formula as a MathJax display block (<c>\[ … \]</c>), keeping the raw LaTeX in a
    /// <c>data-latex</c> attribute so the source survives even when MathJax is unavailable. Empty when no LaTeX.
    /// </summary>
    private static string RenderFormula(StructureBlock block)
    {
        var latex = block.Latex?.Trim();
        if (string.IsNullOrEmpty(latex)) return string.Empty;
        // The visible text uses \[ … \] (MathJax display math). The data-latex attribute keeps the raw source.
        return $"<div class=\"formula\" data-latex=\"{Esc(latex)}\">\\[ {Esc(latex)} \\]</div>";
    }

    /// <summary>
    /// Renders a figure / chart / seal. When a <paramref name="sourceImage"/> is supplied and the region crops
    /// to a usable PNG, embeds the actual pixels as a base64 <c>data:image/png</c> <c>&lt;img&gt;</c>; otherwise
    /// (no image, or a degenerate crop) falls back to the <c>&lt;figure&gt;</c> placeholder carrying the region
    /// label and source-image bbox in <c>data-*</c> attributes. Any recovered caption/text becomes a
    /// <c>&lt;figcaption&gt;</c>.
    /// </summary>
    private static string RenderFigure(StructureBlock block, Image<Rgb24>? sourceImage)
    {
        var label = block.Type.ToString();
        var caption = block.Text?.Trim();

        if (sourceImage is not null)
        {
            var png = TryCropToPng(block, sourceImage);
            if (png is not null)
            {
                var sb = new StringBuilder();
                sb.Append("<figure>");
                sb.Append("<img src=\"data:image/png;base64,").Append(Convert.ToBase64String(png))
                  .Append("\" alt=\"").Append(Esc(label)).Append("\"/>");
                if (!string.IsNullOrEmpty(caption))
                {
                    sb.Append("<figcaption>").Append(Esc(caption)).Append("</figcaption>");
                }
                sb.Append("</figure>");
                return sb.ToString();
            }
            // Degenerate crop: fall through to the placeholder so the figure region is still recorded.
        }

        var b = block.Bounds;
        var ph = new StringBuilder();
        ph.Append("<figure data-type=\"").Append(Esc(label)).Append("\" data-bbox=\"")
          .Append(Num(b.MinX)).Append(' ').Append(Num(b.MinY)).Append(' ')
          .Append(Num(b.MaxX)).Append(' ').Append(Num(b.MaxY)).Append("\">");
        ph.Append("<figcaption>").Append(Esc(string.IsNullOrEmpty(caption) ? label : caption)).Append("</figcaption>");
        ph.Append("</figure>");
        return ph.ToString();
    }

    /// <summary>
    /// Returns <paramref name="sourceImage"/> only when its dimensions match the image the result was
    /// actually produced from, and <c>null</c> otherwise.
    /// <para>
    /// Block bounds are expressed in the analyzed page's coordinate space. With
    /// <c>DocumentOrientation</c> or <c>DocumentUnwarp</c> enabled, that page is the rotated/dewarped copy —
    /// which the engine disposes — while the caller still holds the original. Cropping the original with
    /// those bounds embedded an unrelated part of the page as the figure, silently and with no error. A
    /// dimension check catches the rotation and unwarp cases; on a mismatch the exporters fall back to the
    /// textual placeholder, which is honest rather than wrong.
    /// </para>
    /// </summary>
    private static Image<Rgb24>? ImageMatchingResult(StructureResult result, Image<Rgb24>? sourceImage)
    {
        if (sourceImage is null) return null;
        if (result.SourceWidth <= 0 || result.SourceHeight <= 0) return sourceImage;
        return sourceImage.Width == result.SourceWidth && sourceImage.Height == result.SourceHeight
            ? sourceImage
            : null;
    }

    /// <summary>
    /// Crops <paramref name="block"/>'s <c>Bounds</c> (source-image pixels) out of <paramref name="image"/> and
    /// PNG-encodes the region. Bounds are clamped to the image; returns <c>null</c> when the clamped region is
    /// degenerate (width or height &lt; 2 px).
    /// </summary>
    private static byte[]? TryCropToPng(StructureBlock block, Image<Rgb24> image)
    {
        var b = block.Bounds;
        int x = Math.Clamp((int)Math.Floor(b.MinX), 0, image.Width - 1);
        int y = Math.Clamp((int)Math.Floor(b.MinY), 0, image.Height - 1);
        int right = Math.Clamp((int)Math.Ceiling(b.MaxX), 0, image.Width);
        int bottom = Math.Clamp((int)Math.Ceiling(b.MaxY), 0, image.Height);
        int w = right - x;
        int h = bottom - y;

        if (w < 2 || h < 2) return null;

        using var crop = image.Clone(ctx => ctx.Crop(new Rectangle(x, y, w, h)));
        using var stream = new MemoryStream();
        crop.Save(stream, new PngEncoder());
        return stream.ToArray();
    }

    // ---- helpers ----

    /// <summary>
    /// Returns the text of the first heading-like block, used to seed the document <c>&lt;title&gt;</c>.
    /// </summary>
    private static string? FirstTitle(IEnumerable<StructureBlock> ordered)
    {
        foreach (var b in ordered)
        {
            if ((b.Type is StructureBlockType.DocTitle or StructureBlockType.Title)
                && !string.IsNullOrWhiteSpace(b.Text))
            {
                return b.Text!.Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the <c>&lt;table&gt;…&lt;/table&gt;</c> span of <paramref name="html"/>, trimmed, or <c>null</c>
    /// when there is no such span. Accepts both a bare fragment and the recognizer's
    /// <c>&lt;html&gt;&lt;body&gt;</c>-wrapped form.
    /// </summary>
    private static string? ExtractTableFragment(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        int start = html.IndexOf("<table", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        int end = html.LastIndexOf("</table>", StringComparison.OrdinalIgnoreCase);
        if (end < start) return null;

        return html.Substring(start, end + "</table>".Length - start).Trim();
    }

    /// <summary>
    /// Cheap structural validation that <paramref name="html"/> is a single <c>&lt;table&gt;</c>-rooted
    /// fragment with balanced <c>&lt;table&gt;</c>/<c>&lt;/table&gt;</c> tags. We avoid a full HTML parse
    /// (no dependency) but reject markup that would obviously break the surrounding document.
    /// </summary>
    private static bool IsWellFormedTable(string html)
    {
        // Must start at a <table ...> tag (allow leading whitespace, already trimmed by the caller).
        if (!html.StartsWith("<table", StringComparison.OrdinalIgnoreCase)) return false;
        if (!html.EndsWith("</table>", StringComparison.OrdinalIgnoreCase)) return false;

        int open = CountOccurrences(html, "<table");
        int close = CountOccurrences(html, "</table>");
        if (open == 0 || open != close) return false;

        // Angle brackets must balance overall (a coarse guard against truncated/garbled fragments).
        int lt = CountOccurrences(html, "<");
        int gt = CountOccurrences(html, ">");
        return lt == gt;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    private static string Num(double v) => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// HTML-escapes text content / attribute values (the five XML-significant characters).
    /// </summary>
    /// <summary>
    /// Escapes the five XML entities, after dropping the characters XML 1.0 forbids outright. The exported
    /// page declares XHTML and is routinely re-parsed by XML tooling, so an unescapable C0 control reaching
    /// it (from a caller-built result, or a numeric entity in table HTML) makes the whole document
    /// unparseable. Shares one sanitizer with the DOCX/XLSX writers so all three agree.
    /// </summary>
    private static string Esc(string s) => StructureDocxExporter.SanitizeXmlText(s)
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&#39;");
}
