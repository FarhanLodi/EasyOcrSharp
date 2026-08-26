using System.Globalization;
using System.IO.Compression;
using System.Text;
using EasyImageSharp;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Structure.Export;

/// <summary>
/// Exports a <see cref="StructureResult"/> to a Microsoft Word <c>.docx</c> (WordprocessingML) package,
/// reaching parity with Python PaddleOCR's PP-StructureV3 <c>save_to_word()</c>. The exporter walks the
/// analyzed <see cref="StructureResult.Blocks"/> in reading order and maps each block kind onto a Word
/// construct:
/// <list type="bullet">
///   <item>doc/section titles → heading-styled paragraphs (<c>Heading1</c>/<c>Heading2</c>);</item>
///   <item>body text / lists / captions → normal paragraphs;</item>
///   <item>tables → native Word <c>&lt;w:tbl&gt;</c> recovered from the block's HTML grid, honoring
///         <c>colspan</c> (via <c>&lt;w:gridSpan&gt;</c>) and <c>rowspan</c> (via <c>&lt;w:vMerge&gt;</c>);</item>
///   <item>formulas → a native Word equation (Office MathML / OMML) when a <c>sourceImage</c>-bearing
///         overload is used and the LaTeX converts cleanly; otherwise the recovered LaTeX as <c>$$…$$</c> text;</item>
///   <item>figures/charts/seals → an inline image (the cropped region pixels) when a <c>sourceImage</c> is
///         supplied; otherwise a placeholder paragraph noting the figure region.</item>
/// </list>
/// The package is built by hand (a <c>.docx</c> is a ZIP of XML parts) using only the BCL plus ImageSharp
/// (already a core dependency, used for the PNG crop encode), so the library stays Native-AOT safe. All
/// emitted text is XML-escaped.
/// <para>
/// Image-aware overloads (<see cref="ToDocx(StructureResult, Image{Rgb24})"/> and the matching
/// <c>WriteDocx</c>/<c>SaveAsDocx</c>) embed the actual figure/chart/seal pixels and render formulas as OMML.
/// The caller MUST pass the SAME image that was analyzed: block bounds are interpreted in that image's pixel
/// space. The original no-image overloads keep their exact placeholder/text behavior.
/// </para>
/// </summary>
internal static class StructureDocxExporter
{
    // The minimal WordprocessingML namespaces. Only the main "w" namespace and the relationship namespace
    // are required for the text/table parts; the drawing/picture/math namespaces below are declared on the
    // <w:document> root so the inline-image and OMML fragments can reference them without re-declaring.
    private const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    // DrawingML namespaces used by the inline-image (<w:drawing>) fragment.
    private const string WpNs = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private const string ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string PicNs = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    // EMUs (English Metric Units) per pixel at 96 DPI: 914400 EMU/inch ÷ 96 px/inch = 9525.
    private const long EmuPerPixel = 9525L;

    // Cap an embedded image's rendered width to 6 inches so a large crop doesn't overrun the page; the
    // height is scaled proportionally. 6 in × 914400 EMU/in = 5486400 EMU.
    private const long MaxImageWidthEmu = 5486400L;

    // UTF-8 without a BOM: OOXML parts must not carry a byte-order mark.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Renders the structure result to an in-memory WordprocessingML <c>.docx</c> package. Figures/charts/seals
    /// are written as textual placeholders and formulas as <c>$$…$$</c> text (no pixels are embedded).
    /// </summary>
    /// <param name="result">The analyzed document to export. Must not be <c>null</c>.</param>
    /// <returns>The bytes of a valid <c>.docx</c> ZIP package.</returns>
    public static byte[] ToDocx(this StructureResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        using var buffer = new MemoryStream();
        WriteDocx(result, buffer, leaveOpen: true, sourceImage: null);
        return buffer.ToArray();
    }

    /// <summary>
    /// Renders the structure result to an in-memory WordprocessingML <c>.docx</c> package, embedding the actual
    /// pixels of each figure/chart/seal region as an inline image and rendering recovered formula LaTeX as a
    /// native Word equation (OMML).
    /// </summary>
    /// <param name="result">The analyzed document to export. Must not be <c>null</c>.</param>
    /// <param name="sourceImage">
    /// The SAME image that was analyzed to produce <paramref name="result"/>. Block <c>Bounds</c> are
    /// interpreted in this image's pixel space; passing a different image yields wrong crops. Must not be <c>null</c>.
    /// </param>
    /// <returns>The bytes of a valid <c>.docx</c> ZIP package with embedded media.</returns>
    public static byte[] ToDocx(this StructureResult result, Image<Rgb24> sourceImage)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sourceImage);

        using var buffer = new MemoryStream();
        WriteDocx(result, buffer, leaveOpen: true, sourceImage);
        return buffer.ToArray();
    }

    /// <summary>
    /// Writes the structure result as a WordprocessingML <c>.docx</c> package into <paramref name="destination"/>.
    /// The stream is left open so callers retain ownership.
    /// </summary>
    /// <param name="result">The analyzed document to export. Must not be <c>null</c>.</param>
    /// <param name="destination">The writable, seekable stream to receive the package. Must not be <c>null</c>.</param>
    public static void WriteDocx(this StructureResult result, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(destination);
        WriteDocx(result, destination, leaveOpen: true, sourceImage: null);
    }

    /// <summary>
    /// Writes the structure result as a WordprocessingML <c>.docx</c> package into <paramref name="destination"/>,
    /// embedding figure/chart/seal pixels and rendering formulas as OMML. The stream is left open.
    /// </summary>
    /// <param name="result">The analyzed document to export. Must not be <c>null</c>.</param>
    /// <param name="destination">The writable, seekable stream to receive the package. Must not be <c>null</c>.</param>
    /// <param name="sourceImage">The SAME image that was analyzed; block bounds are in its pixel space. Must not be <c>null</c>.</param>
    public static void WriteDocx(this StructureResult result, Stream destination, Image<Rgb24> sourceImage)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(sourceImage);
        WriteDocx(result, destination, leaveOpen: true, sourceImage);
    }

    /// <summary>
    /// Renders the structure result and saves it to <paramref name="path"/> as a <c>.docx</c> file.
    /// </summary>
    /// <param name="result">The analyzed document to export. Must not be <c>null</c>.</param>
    /// <param name="path">The destination file path. Must not be <c>null</c> or empty.</param>
    public static void SaveAsDocx(this StructureResult result, string path)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var file = File.Create(path);
        WriteDocx(result, file, leaveOpen: false, sourceImage: null);
    }

    /// <summary>
    /// Renders the structure result and saves it to <paramref name="path"/> as a <c>.docx</c> file, embedding
    /// figure/chart/seal pixels and rendering formulas as OMML.
    /// </summary>
    /// <param name="result">The analyzed document to export. Must not be <c>null</c>.</param>
    /// <param name="path">The destination file path. Must not be <c>null</c> or empty.</param>
    /// <param name="sourceImage">The SAME image that was analyzed; block bounds are in its pixel space. Must not be <c>null</c>.</param>
    public static void SaveAsDocx(this StructureResult result, string path, Image<Rgb24> sourceImage)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(sourceImage);

        using var file = File.Create(path);
        WriteDocx(result, file, leaveOpen: false, sourceImage);
    }

    private static void WriteDocx(StructureResult result, Stream destination, bool leaveOpen, Image<Rgb24>? sourceImage)
    {
        // Pass 1: build the document body. When a source image is supplied, this also crops each
        // figure/chart/seal region to a PNG and records a media part for it; the returned media list drives
        // the media parts, relationships and the png content-type declaration below.
        var media = new List<MediaPart>();
        string documentXml = DocumentXml(result, sourceImage, media);

        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen);

        WriteEntry(zip, "[Content_Types].xml", ContentTypesXml(includePng: media.Count > 0));
        WriteEntry(zip, "_rels/.rels", RootRelsXml());
        WriteEntry(zip, "word/_rels/document.xml.rels", DocumentRelsXml(media));
        WriteEntry(zip, "word/document.xml", documentXml);

        // Binary media parts (PNG crops). These are stored after the XML parts; order in the ZIP is irrelevant.
        foreach (var part in media)
        {
            WriteBinaryEntry(zip, "word/media/" + part.FileName, part.Png);
        }
    }

    /// <summary>
    /// A cropped figure/chart/seal region staged for embedding: its PNG bytes, the relationship id
    /// (<c>rId{N}</c>) the drawing run references, the media file name, and the rendered extent in EMUs.
    /// </summary>
    private sealed record MediaPart(string RelId, string FileName, byte[] Png, long ExtentWidthEmu, long ExtentHeightEmu);

    // ---- top-level OOXML parts -------------------------------------------------------------------------

    private static string ContentTypesXml(bool includePng) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        // Only declared when at least one PNG media part is embedded, so the no-image package is byte-for-byte
        // unchanged from before.
        (includePng ? "<Default Extension=\"png\" ContentType=\"image/png\"/>" : "") +
        "<Override PartName=\"/word/document.xml\" " +
        "ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
        "</Types>";

    private static string RootRelsXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" " +
        "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" " +
        "Target=\"word/document.xml\"/>" +
        "</Relationships>";

    // No relationships are required by the document body itself (no styles/numbering parts are emitted),
    // but Word expects the document part's .rels to exist. An empty relationship set is valid. Each embedded
    // image adds one relationship: rId{N} -> media/image{N}.png (image relationship type).
    private static string DocumentRelsXml(IReadOnlyList<MediaPart> media)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        foreach (var part in media)
        {
            sb.Append("<Relationship Id=\"").Append(part.RelId).Append("\" ")
              .Append("Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" ")
              .Append("Target=\"media/").Append(part.FileName).Append("\"/>");
        }
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    // ---- document body ---------------------------------------------------------------------------------

    // Builds word/document.xml. When sourceImage is non-null, figure/chart/seal regions are cropped to PNGs,
    // appended to <paramref name="media"/> (which the caller turns into media parts + relationships), and
    // emitted as inline-image drawings; formulas are rendered as OMML when the LaTeX converts. When null, the
    // original placeholder/text behavior is used and <paramref name="media"/> stays empty.
    private static string DocumentXml(StructureResult result, Image<Rgb24>? sourceImage, List<MediaPart> media)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        // Declare every namespace the body may use at the root so inline-image and OMML fragments stay terse.
        sb.Append("<w:document")
          .Append(" xmlns:w=\"").Append(WNs).Append('"')
          .Append(" xmlns:r=\"").Append(RNs).Append('"')
          .Append(" xmlns:wp=\"").Append(WpNs).Append('"')
          .Append(" xmlns:a=\"").Append(ANs).Append('"')
          .Append(" xmlns:pic=\"").Append(PicNs).Append('"')
          .Append('>');
        sb.Append("<w:body>");

        foreach (var block in OrderedBlocks(result))
        {
            AppendBlock(sb, block, sourceImage, media);
        }

        // A trailing section-properties element is conventional and keeps Word happy about page setup.
        sb.Append("<w:sectPr/>");
        sb.Append("</w:body>");
        sb.Append("</w:document>");
        return sb.ToString();
    }

    private static void AppendBlock(StringBuilder sb, StructureBlock block, Image<Rgb24>? sourceImage, List<MediaPart> media)
    {
        switch (block.Type)
        {
            case StructureBlockType.DocTitle:
                AppendParagraph(sb, block.Text, styleId: "Title");
                break;

            case StructureBlockType.Title:
                AppendParagraph(sb, block.Text, styleId: "Heading1");
                break;

            case StructureBlockType.Abstract:
                AppendParagraph(sb, block.Text, styleId: "Heading2");
                break;

            case StructureBlockType.Table:
                AppendTable(sb, block);
                break;

            case StructureBlockType.Formula:
                AppendFormula(sb, block, sourceImage is not null);
                break;

            case StructureBlockType.Figure:
            case StructureBlockType.Chart:
            case StructureBlockType.Seal:
                AppendFigure(sb, block, sourceImage, media);
                break;

            default:
                // Text, Paragraph, List, captions, headers/footers, references, footnotes, etc.
                AppendParagraph(sb, block.Text, styleId: null);
                break;
        }
    }

    /// <summary>
    /// Emits a formula block. When an image is supplied (image-aware overload) and the LaTeX converts to OMML,
    /// renders a native Word equation; otherwise falls back to the recovered LaTeX as <c>$$…$$</c> text.
    /// </summary>
    private static void AppendFormula(StringBuilder sb, StructureBlock block, bool allowOmml)
    {
        if (string.IsNullOrWhiteSpace(block.Latex)) return;

        if (allowOmml)
        {
            // LatexToOmml.Convert returns a self-contained <m:oMath xmlns:m="…">…</m:oMath> (or string.Empty)
            // and never throws. As a child of <w:p> it renders as a native Word equation.
            var omml = LatexToOmml.Convert(block.Latex);
            if (!string.IsNullOrEmpty(omml))
            {
                sb.Append("<w:p>").Append(omml).Append("</w:p>");
                return;
            }
        }

        // No image, or the LaTeX did not convert: keep the faithful, honest $$…$$ text rendering.
        AppendParagraph(sb, "$$ " + block.Latex!.Trim() + " $$", styleId: null);
    }

    /// <summary>
    /// Emits a figure/chart/seal block. When an image is supplied and the region crops to a usable PNG, embeds
    /// the pixels as an inline image (staging a media part for the caller); otherwise emits the textual placeholder.
    /// </summary>
    private static void AppendFigure(StringBuilder sb, StructureBlock block, Image<Rgb24>? sourceImage, List<MediaPart> media)
    {
        if (sourceImage is not null)
        {
            var part = TryCropToMediaPart(block, sourceImage, media.Count);
            if (part is not null)
            {
                media.Add(part);
                AppendInlineImage(sb, part);
                // Keep any recovered caption/text under the image, mirroring the placeholder's behavior.
                var caption = block.Text?.Trim();
                if (!string.IsNullOrEmpty(caption))
                {
                    AppendParagraph(sb, caption, styleId: null);
                }
                return;
            }
            // Crop was degenerate (region too small / off-image): fall through to the placeholder so the
            // figure region is still recorded rather than silently dropped.
        }

        AppendFigurePlaceholder(sb, block);
    }

    /// <summary>
    /// Emits a single <c>&lt;w:p&gt;</c> with one run of <paramref name="text"/>; skips blank text.
    /// </summary>
    private static void AppendParagraph(StringBuilder sb, string? text, string? styleId)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        sb.Append("<w:p>");
        if (styleId is not null)
        {
            sb.Append("<w:pPr><w:pStyle w:val=\"").Append(styleId).Append("\"/></w:pPr>");
        }
        AppendRun(sb, text.Trim());
        sb.Append("</w:p>");
    }

    private static void AppendFigurePlaceholder(StringBuilder sb, StructureBlock block)
    {
        var caption = block.Text?.Trim();
        var label = "[" + block.Type + " region]";
        var text = string.IsNullOrEmpty(caption) ? label : label + " " + caption;
        AppendParagraph(sb, text, styleId: null);
    }

    // ---- inline-image embedding ------------------------------------------------------------------------

    /// <summary>
    /// Crops <paramref name="block"/>'s <c>Bounds</c> (source-image pixels) out of <paramref name="image"/>,
    /// PNG-encodes the region and stages it as a <see cref="MediaPart"/> with a fresh relationship id derived
    /// from <paramref name="mediaIndex"/> (the count of already-staged parts). Returns <c>null</c> when the
    /// clamped region is degenerate (width or height &lt; 2 px) so the caller can fall back to a placeholder.
    /// </summary>
    private static MediaPart? TryCropToMediaPart(StructureBlock block, Image<Rgb24> image, int mediaIndex)
    {
        var b = block.Bounds;
        int x = Math.Clamp((int)Math.Floor(b.MinX), 0, image.Width - 1);
        int y = Math.Clamp((int)Math.Floor(b.MinY), 0, image.Height - 1);
        int right = Math.Clamp((int)Math.Ceiling(b.MaxX), 0, image.Width);
        int bottom = Math.Clamp((int)Math.Ceiling(b.MaxY), 0, image.Height);
        int w = right - x;
        int h = bottom - y;

        if (w < 2 || h < 2) return null;

        byte[] png;
        using (var crop = image.Clone(ctx => ctx.Crop(new Rectangle(x, y, w, h))))
        using (var stream = new MemoryStream())
        {
            crop.Save(stream, new PngEncoder());
            png = stream.ToArray();
        }

        // The crop dimensions (w, h) are the image's natural pixel size; compute the rendered extent in EMUs,
        // capping width to a sane page width and scaling height proportionally.
        long widthEmu = w * EmuPerPixel;
        long heightEmu = h * EmuPerPixel;
        if (widthEmu > MaxImageWidthEmu)
        {
            heightEmu = (long)Math.Round(heightEmu * (MaxImageWidthEmu / (double)widthEmu));
            widthEmu = MaxImageWidthEmu;
            if (heightEmu < 1) heightEmu = 1;
        }

        // The exporter emits no other document.xml relationships, so a 1-based sequence is collision-free.
        int n = mediaIndex + 1;
        return new MediaPart($"rId{n}", $"image{n}.png", png, widthEmu, heightEmu);
    }

    /// <summary>
    /// Emits an inline-image paragraph: <c>&lt;w:drawing&gt;&lt;wp:inline&gt;…&lt;a:blip r:embed="rId{N}"/&gt;…</c>.
    /// All referenced namespaces (<c>wp</c>, <c>a</c>, <c>pic</c>, <c>r</c>) are declared on the <c>&lt;w:document&gt;</c>
    /// root, so this fragment only needs the relationship id and the rendered extent.
    /// </summary>
    private static void AppendInlineImage(StringBuilder sb, MediaPart part)
    {
        string cx = part.ExtentWidthEmu.ToString(CultureInfo.InvariantCulture);
        string cy = part.ExtentHeightEmu.ToString(CultureInfo.InvariantCulture);
        // A unique non-zero drawing-object id. The relationship id is "rId{N}"; reuse N for the docPr id.
        string id = part.RelId.Substring("rId".Length);

        sb.Append("<w:p><w:r><w:drawing>");
        sb.Append("<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">");
        sb.Append("<wp:extent cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\"/>");
        sb.Append("<wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/>");
        sb.Append("<wp:docPr id=\"").Append(id).Append("\" name=\"Picture ").Append(id).Append("\"/>");
        sb.Append("<wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>");
        sb.Append("<a:graphic>");
        sb.Append("<a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">");
        sb.Append("<pic:pic>");
        sb.Append("<pic:nvPicPr>");
        sb.Append("<pic:cNvPr id=\"").Append(id).Append("\" name=\"image").Append(id).Append(".png\"/>");
        sb.Append("<pic:cNvPicPr/>");
        sb.Append("</pic:nvPicPr>");
        sb.Append("<pic:blipFill>");
        sb.Append("<a:blip r:embed=\"").Append(part.RelId).Append("\"/>");
        sb.Append("<a:stretch><a:fillRect/></a:stretch>");
        sb.Append("</pic:blipFill>");
        sb.Append("<pic:spPr>");
        sb.Append("<a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\"/></a:xfrm>");
        sb.Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>");
        sb.Append("</pic:spPr>");
        sb.Append("</pic:pic>");
        sb.Append("</a:graphicData>");
        sb.Append("</a:graphic>");
        sb.Append("</wp:inline>");
        sb.Append("</w:drawing></w:r></w:p>");
    }

    /// <summary>
    /// Emits a run that preserves significant whitespace (<c>xml:space="preserve"</c>) so leading/trailing
    /// spaces inside cells/paragraphs survive the round-trip.
    /// </summary>
    private static void AppendRun(StringBuilder sb, string text)
    {
        sb.Append("<w:r><w:t xml:space=\"preserve\">").Append(Xml(text)).Append("</w:t></w:r>");
    }

    // ---- tables ----------------------------------------------------------------------------------------

    private static void AppendTable(StringBuilder sb, StructureBlock block)
    {
        var grid = OoxmlHtmlTable.Parse(block.TableHtml);

        // No parseable grid: degrade to a single-cell table carrying whatever text we have, so the table
        // region is never lost (this is the most faithful option when only free text is available).
        if (grid is null || grid.RowCount == 0 || grid.ColumnCount == 0)
        {
            var fallback = !string.IsNullOrWhiteSpace(block.Text)
                ? block.Text!.Trim()
                : (block.TableHtml?.Trim() ?? string.Empty);
            if (fallback.Length == 0) return;

            sb.Append("<w:tbl>");
            AppendTableProperties(sb);
            sb.Append("<w:tblGrid><w:gridCol/></w:tblGrid>");
            sb.Append("<w:tr>");
            AppendTableCell(sb, fallback, gridSpan: 1, vMerge: null);
            sb.Append("</w:tr>");
            sb.Append("</w:tbl>");
            // A trailing empty paragraph after a table is required so Word does not merge it with following
            // content; emit one to be safe.
            sb.Append("<w:p/>");
            return;
        }

        sb.Append("<w:tbl>");
        AppendTableProperties(sb);

        // Declare the logical column grid.
        sb.Append("<w:tblGrid>");
        for (int c = 0; c < grid.ColumnCount; c++)
        {
            sb.Append("<w:gridCol/>");
        }
        sb.Append("</w:tblGrid>");

        for (int r = 0; r < grid.RowCount; r++)
        {
            sb.Append("<w:tr>");
            for (int c = 0; c < grid.ColumnCount; c++)
            {
                var cell = grid.Cells[r, c];
                if (cell is null)
                {
                    // A slot covered by a colspan of the cell to its left: nothing to emit (it was folded
                    // into that cell's gridSpan).
                    continue;
                }

                if (cell.IsRowSpanContinuation)
                {
                    // A slot covered by a rowspan from above: emit a vertically-merged continuation cell.
                    AppendTableCell(sb, text: string.Empty, gridSpan: cell.ColSpan, vMerge: "continue");
                    continue;
                }

                var vMerge = cell.RowSpan > 1 ? "restart" : null;
                AppendTableCell(sb, cell.Text, gridSpan: cell.ColSpan, vMerge: vMerge);
            }
            sb.Append("</w:tr>");
        }

        sb.Append("</w:tbl>");
        sb.Append("<w:p/>");
    }

    private static void AppendTableProperties(StringBuilder sb)
    {
        // A single-line border on every edge so the table is visibly gridded, matching the recovered HTML.
        sb.Append("<w:tblPr>");
        sb.Append("<w:tblW w:w=\"0\" w:type=\"auto\"/>");
        sb.Append("<w:tblBorders>");
        foreach (var edge in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
        {
            sb.Append("<w:").Append(edge).Append(" w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"auto\"/>");
        }
        sb.Append("</w:tblBorders>");
        sb.Append("</w:tblPr>");
    }

    private static void AppendTableCell(StringBuilder sb, string text, int gridSpan, string? vMerge)
    {
        sb.Append("<w:tc>");
        sb.Append("<w:tcPr>");
        if (gridSpan > 1)
        {
            sb.Append("<w:gridSpan w:val=\"").Append(gridSpan).Append("\"/>");
        }
        if (vMerge is not null)
        {
            // "restart" begins a vertical merge; "continue" extends it downward.
            sb.Append("<w:vMerge w:val=\"").Append(vMerge).Append("\"/>");
        }
        sb.Append("</w:tcPr>");

        // A table cell must contain at least one paragraph to be valid, even when empty.
        if (string.IsNullOrEmpty(text))
        {
            sb.Append("<w:p/>");
        }
        else
        {
            sb.Append("<w:p>");
            AppendRun(sb, text);
            sb.Append("</w:p>");
        }
        sb.Append("</w:tc>");
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static IEnumerable<StructureBlock> OrderedBlocks(StructureResult result)
        => result.Blocks.OrderBy(b => b.Order);

    /// <summary>
    /// Escapes XML's five predefined entities so arbitrary OCR text is safe in element content.
    /// </summary>
    internal static string Xml(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");

    /// <summary>
    /// Adds a ZIP entry with the given (forward-slash, no leading slash) name and UTF-8 (no BOM) text.
    /// </summary>
    internal static void WriteEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.Write(content);
    }

    /// <summary>
    /// Adds a ZIP entry with raw binary content (e.g. an already-compressed PNG media part). PNG bytes are
    /// stored with no further compression — re-deflating compressed image data wastes CPU for no size win.
    /// </summary>
    private static void WriteBinaryEntry(ZipArchive zip, string entryName, byte[] content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }
}
