using System.Globalization;
using System.Text;
using System.Text.Json;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Export;

/// <summary>
/// Converts an <see cref="OcrResult"/> to the document-interchange formats document pipelines and
/// archival systems expect: JSON, hOCR, ALTO XML, and Tesseract-style TSV. All exporters are pure
/// (no I/O) and AOT-friendly.
/// </summary>
public static class OcrExportExtensions
{
    private const string Producer = "EasyOcrSharp 2.2.1";

    private static readonly EasyOcrJsonContext IndentedJson = new(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Serializes the result to JSON using the source-generated (AOT-safe) context.</summary>
    public static string ToJson(this OcrResult result, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        return JsonSerializer.Serialize(result,
            indented ? IndentedJson.OcrResult : EasyOcrJsonContext.Default.OcrResult);
    }

    /// <summary>
    /// Renders the result as <a href="https://kba.cloud/hocr-spec/1.2/">hOCR</a> — an HTML format
    /// understood by DMS tooling and convertible to searchable PDF. Pass the source image size for
    /// correct page bounds (defaults to the result's own extents when omitted).
    /// </summary>
    public static string ToHocr(this OcrResult result, int pageWidth = 0, int pageHeight = 0, string? imageName = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var (w, h) = ResolvePageSize(result, pageWidth, pageHeight);

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\">");
        sb.AppendLine("<html xmlns=\"http://www.w3.org/1999/xhtml\" xml:lang=\"en\" lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta http-equiv=\"Content-Type\" content=\"text/html;charset=utf-8\"/>");
        sb.Append("  <meta name='ocr-system' content='").Append(Xml(Producer)).AppendLine("'/>");
        sb.AppendLine("  <meta name='ocr-capabilities' content='ocr_page ocr_line ocrx_word'/>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.Append("  <div class='ocr_page' id='page_1' title='image \"")
          .Append(Xml(imageName ?? string.Empty)).Append("\"; bbox 0 0 ").Append(w).Append(' ').Append(h).AppendLine("'>");

        int lineNo = 0;
        foreach (var line in result.Lines)
        {
            lineNo++;
            var b = line.BoundingBox;
            sb.Append("    <span class='ocr_line' id='line_").Append(lineNo).Append("' title='")
              .Append(BboxTitle(b)).AppendLine("'>");

            int wordNo = 0;
            foreach (var (text, wb) in SplitWords(line))
            {
                wordNo++;
                sb.Append("      <span class='ocrx_word' id='word_").Append(lineNo).Append('_').Append(wordNo)
                  .Append("' title='").Append(BboxTitle(wb)).Append("; x_wconf ").Append(Conf100(line.Confidence))
                  .Append("'>").Append(Xml(text)).AppendLine("</span>");
            }
            sb.AppendLine("    </span>");
        }

        sb.AppendLine("  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    /// <summary>
    /// Renders the result as <a href="https://www.loc.gov/standards/alto/">ALTO XML v4</a>, the
    /// layout format used by libraries and digitization workflows.
    /// </summary>
    public static string ToAlto(this OcrResult result, int pageWidth = 0, int pageHeight = 0, string? imageName = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var (w, h) = ResolvePageSize(result, pageWidth, pageHeight);

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<alto xmlns=\"http://www.loc.gov/standards/alto/ns-v4#\">");
        sb.AppendLine("  <Description>");
        sb.AppendLine("    <MeasurementUnit>pixel</MeasurementUnit>");
        sb.Append("    <sourceImageInformation><fileName>").Append(Xml(imageName ?? string.Empty)).AppendLine("</fileName></sourceImageInformation>");
        sb.Append("    <OCRProcessing ID=\"ocr_1\"><ocrProcessingStep><processingSoftware><softwareName>")
          .Append(Xml(Producer)).AppendLine("</softwareName></processingSoftware></ocrProcessingStep></OCRProcessing>");
        sb.AppendLine("  </Description>");
        sb.AppendLine("  <Layout>");
        sb.Append("    <Page ID=\"page_1\" PHYSICAL_IMG_NR=\"1\" WIDTH=\"").Append(w).Append("\" HEIGHT=\"").Append(h).AppendLine("\">");
        sb.Append("      <PrintSpace HPOS=\"0\" VPOS=\"0\" WIDTH=\"").Append(w).Append("\" HEIGHT=\"").Append(h).AppendLine("\">");
        sb.AppendLine("        <TextBlock ID=\"block_1\">");

        int lineNo = 0;
        foreach (var line in result.Lines)
        {
            lineNo++;
            var b = line.BoundingBox;
            sb.Append("          <TextLine ID=\"line_").Append(lineNo).Append("\" ").Append(AltoBox(b)).AppendLine(">");
            int wordNo = 0;
            foreach (var (text, wb) in SplitWords(line))
            {
                wordNo++;
                sb.Append("            <String ID=\"string_").Append(lineNo).Append('_').Append(wordNo).Append("\" ")
                  .Append(AltoBox(wb)).Append(" WC=\"").Append(line.Confidence.ToString("0.###", CultureInfo.InvariantCulture))
                  .Append("\" CONTENT=\"").Append(Xml(text)).AppendLine("\"/>");
            }
            sb.AppendLine("          </TextLine>");
        }

        sb.AppendLine("        </TextBlock>");
        sb.AppendLine("      </PrintSpace>");
        sb.AppendLine("    </Page>");
        sb.AppendLine("  </Layout>");
        sb.AppendLine("</alto>");
        return sb.ToString();
    }

    /// <summary>
    /// Renders the result as Tesseract-style tab-separated values (one row per word), handy for
    /// spreadsheets and downstream parsing.
    /// </summary>
    public static string ToTsv(this OcrResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var sb = new StringBuilder();
        sb.AppendLine("level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext");

        int lineNo = 0;
        foreach (var line in result.Lines)
        {
            lineNo++;
            int wordNo = 0;
            foreach (var (text, wb) in SplitWords(line))
            {
                wordNo++;
                sb.Append("5\t1\t1\t1\t").Append(lineNo).Append('\t').Append(wordNo).Append('\t')
                  .Append((int)Math.Round(wb.MinX)).Append('\t').Append((int)Math.Round(wb.MinY)).Append('\t')
                  .Append((int)Math.Round(wb.Width)).Append('\t').Append((int)Math.Round(wb.Height)).Append('\t')
                  .Append(Conf100(line.Confidence)).Append('\t').Append(text.Replace('\t', ' ')).Append('\n');
            }
        }
        return sb.ToString();
    }

    // ---- helpers ----

    private static (int Width, int Height) ResolvePageSize(OcrResult result, int w, int h)
    {
        if (w > 0 && h > 0) return (w, h);
        double maxX = 0, maxY = 0;
        foreach (var line in result.Lines)
        {
            if (line.BoundingBox.MaxX > maxX) maxX = line.BoundingBox.MaxX;
            if (line.BoundingBox.MaxY > maxY) maxY = line.BoundingBox.MaxY;
        }
        return (w > 0 ? w : (int)Math.Ceiling(maxX), h > 0 ? h : (int)Math.Ceiling(maxY));
    }

    /// <summary>
    /// Splits a line into whitespace-separated words, approximating each word's box by allocating the
    /// line width proportionally to character count (we only have line-level geometry from the model).
    /// </summary>
    private static IEnumerable<(string Text, OcrBoundingBox Box)> SplitWords(OcrLine line)
    {
        var b = line.BoundingBox;
        if (string.IsNullOrWhiteSpace(line.Text))
        {
            yield break;
        }

        var words = line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1)
        {
            yield return (line.Text.Trim(), b);
            yield break;
        }

        int totalChars = words.Sum(w => w.Length);
        double x = b.MinX;
        double usable = b.Width;
        foreach (var word in words)
        {
            double frac = totalChars > 0 ? (double)word.Length / totalChars : 1.0 / words.Length;
            double width = usable * frac;
            yield return (word, new OcrBoundingBox(x, b.MinY, x + width, b.MaxY));
            x += width;
        }
    }

    private static string BboxTitle(OcrBoundingBox b)
        => $"bbox {(int)Math.Round(b.MinX)} {(int)Math.Round(b.MinY)} {(int)Math.Round(b.MaxX)} {(int)Math.Round(b.MaxY)}";

    private static string AltoBox(OcrBoundingBox b)
        => $"HPOS=\"{(int)Math.Round(b.MinX)}\" VPOS=\"{(int)Math.Round(b.MinY)}\" WIDTH=\"{(int)Math.Round(b.Width)}\" HEIGHT=\"{(int)Math.Round(b.Height)}\"";

    private static int Conf100(double confidence) => (int)Math.Round(Math.Clamp(confidence, 0, 1) * 100);

    private static string Xml(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
