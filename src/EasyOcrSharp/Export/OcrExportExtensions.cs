using System.Globalization;
using System.Text;
using System.Text.Json;
using EasyOcrSharp.Models;

using EasyOcrSharp.Structure.Export;

namespace EasyOcrSharp.Export;

/// <summary>
/// Converts an <see cref="OcrResult"/> to the document-interchange formats document pipelines and
/// archival systems expect: JSON, hOCR, ALTO XML, and Tesseract-style TSV. All exporters are pure
/// (no I/O) and AOT-friendly.
/// </summary>
public static class OcrExportExtensions
{
    // Derived from the assembly version so the exporter "producer" tag never drifts from the package version.
    private static readonly string Producer =
        "EasyOcrSharp " + (typeof(OcrExportExtensions).Assembly.GetName().Version?.ToString(3) ?? "");

    private static readonly EasyOcrJsonContext CompactJson = new(EasyOcrJson.Options(indented: false));
    private static readonly EasyOcrJsonContext IndentedJson = new(EasyOcrJson.Options(indented: true));

    /// <summary>
    /// Serializes the result to JSON using the source-generated (AOT-safe) context. Non-ASCII text
    /// (Cyrillic, CJK, Arabic, …) is written verbatim rather than as <c>\uXXXX</c> escapes — see
    /// <see cref="EasyOcrJson.Encoder"/>.
    /// </summary>
    public static string ToJson(this OcrResult result, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        return JsonSerializer.Serialize(result, (indented ? IndentedJson : CompactJson).OcrResult);
    }

    /// <summary>
    /// Serializes the result with caller-supplied <paramref name="options"/> (naming policy, indentation,
    /// <see cref="System.Text.Json.JsonSerializerOptions.Encoder"/>, …). Serialization stays
    /// reflection-free: the options are copied onto an <see cref="EasyOcrJsonContext"/>, so the
    /// source-generated metadata is still what resolves the types.
    /// </summary>
    /// <remarks>
    /// A context is built per call. When serializing in a loop, cache one instead:
    /// <c>var ctx = new EasyOcrJsonContext(options); JsonSerializer.Serialize(result, ctx.OcrResult);</c>.
    /// </remarks>
    public static string ToJson(this OcrResult result, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        return JsonSerializer.Serialize(result, new EasyOcrJsonContext(EasyOcrJson.ForContext(options)).OcrResult);
    }

    /// <summary>
    /// Renders the result as <a href="https://kba.cloud/hocr-spec/1.2/">hOCR</a> — an HTML format
    /// understood by DMS tooling and convertible to searchable PDF. Pass the source image size for
    /// correct page bounds (defaults to the result's own extents when omitted).
    /// </summary>
    /// <remarks>
    /// Word boxes come from <see cref="OcrLine.Words"/> when the recognizer was asked for them (see
    /// <see cref="RecognitionOptions.WordLevelDetail"/>), in which case each word also carries its own
    /// <c>x_wconf</c>; otherwise the line box is split proportionally to word length as before. When
    /// <see cref="OcrLine.Characters"/> is populated, each word additionally gets Tesseract's
    /// <c>x_bboxes</c> / <c>x_confs</c> properties listing its glyphs.
    /// </remarks>
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
            foreach (var word in EnumerateWords(line))
            {
                wordNo++;
                sb.Append("      <span class='ocrx_word' id='word_").Append(lineNo).Append('_').Append(wordNo)
                  .Append("' title='").Append(BboxTitle(word.Box)).Append("; x_wconf ").Append(Conf100(word.Confidence));
                AppendCharacterProperties(sb, word.Characters);
                sb.Append("'>").Append(Xml(word.Text)).AppendLine("</span>");
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
    /// <remarks>
    /// <c>String</c> geometry and <c>WC</c> come from <see cref="OcrLine.Words"/> when the recognizer was
    /// asked for them (see <see cref="RecognitionOptions.WordLevelDetail"/>), otherwise from the same
    /// proportional split of the line box used before. Populated <see cref="OcrLine.Characters"/> are
    /// emitted as ALTO <c>Glyph</c> children.
    /// </remarks>
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
            foreach (var word in EnumerateWords(line))
            {
                wordNo++;
                sb.Append("            <String ID=\"string_").Append(lineNo).Append('_').Append(wordNo).Append("\" ")
                  .Append(AltoBox(word.Box)).Append(" WC=\"").Append(word.Confidence.ToString("0.###", CultureInfo.InvariantCulture))
                  .Append("\" CONTENT=\"").Append(Xml(word.Text));

                if (word.Characters.Count == 0)
                {
                    sb.AppendLine("\"/>");
                    continue;
                }

                sb.AppendLine("\">");
                int glyphNo = 0;
                foreach (var glyph in word.Characters)
                {
                    glyphNo++;
                    sb.Append("              <Glyph ID=\"glyph_").Append(lineNo).Append('_').Append(wordNo).Append('_').Append(glyphNo)
                      .Append("\" ").Append(AltoBox(glyph.BoundingBox))
                      .Append(" GC=\"").Append(glyph.Confidence.ToString("0.###", CultureInfo.InvariantCulture))
                      .Append("\" CONTENT=\"").Append(Xml(glyph.Value)).AppendLine("\"/>");
                }
                sb.AppendLine("            </String>");
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
    /// <remarks>
    /// Rows use <see cref="OcrLine.Words"/> geometry and confidence when the recognizer was asked for
    /// them (see <see cref="RecognitionOptions.WordLevelDetail"/>), otherwise the previous proportional
    /// split of the line box. The format's <c>level</c> column stops at 5 (word), so per-character detail
    /// is not representable here — use hOCR or ALTO for glyphs.
    /// </remarks>
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
            foreach (var word in EnumerateWords(line))
            {
                wordNo++;
                var wb = word.Box;
                sb.Append("5\t1\t1\t1\t").Append(lineNo).Append('\t').Append(wordNo).Append('\t')
                  .Append((int)Math.Round(wb.MinX)).Append('\t').Append((int)Math.Round(wb.MinY)).Append('\t')
                  .Append((int)Math.Round(wb.Width)).Append('\t').Append((int)Math.Round(wb.Height)).Append('\t')
                  .Append(Conf100(word.Confidence)).Append('\t').Append(word.Text.Replace('\t', ' ')).Append('\n');
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

    /// <summary>One word as the exporters need it: text, box, confidence and (optionally) its glyphs.</summary>
    private readonly record struct ExportWord(
        string Text,
        OcrBoundingBox Box,
        double Confidence,
        IReadOnlyList<OcrChar> Characters);

    /// <summary>
    /// The words of a line. Prefers the recognizer's true per-word geometry and confidence; falls back to
    /// the historical proportional split — including its line-level confidence and no glyphs — whenever
    /// <see cref="OcrLine.Words"/> is empty, so output for callers who never opted in is unchanged.
    /// </summary>
    private static IEnumerable<ExportWord> EnumerateWords(OcrLine line)
    {
        if (line.Words.Count == 0)
        {
            foreach (var (text, box) in SplitWords(line))
            {
                yield return new ExportWord(text, box, line.Confidence, Array.Empty<OcrChar>());
            }
            yield break;
        }

        var glyphs = GroupCharactersByWord(line);
        for (int i = 0; i < line.Words.Count; i++)
        {
            var word = line.Words[i];
            yield return new ExportWord(
                word.Text,
                word.BoundingBox,
                word.Confidence,
                glyphs is null ? Array.Empty<OcrChar>() : glyphs[i]);
        }
    }

    /// <summary>
    /// Partitions <see cref="OcrLine.Characters"/> into one group per word by splitting on whitespace
    /// (the separators belong to no word). Returns null unless the groups line up exactly with
    /// <see cref="OcrLine.Words"/> — both in count and in text — so a mismatch degrades to "no glyphs"
    /// rather than to wrong glyphs.
    /// </summary>
    private static IReadOnlyList<OcrChar>[]? GroupCharactersByWord(OcrLine line)
    {
        if (line.Characters.Count == 0) return null;

        var groups = new List<IReadOnlyList<OcrChar>>(line.Words.Count);
        var current = new List<OcrChar>();
        foreach (var ch in line.Characters)
        {
            if (ch.Value.Length == 1 && char.IsWhiteSpace(ch.Value[0]))
            {
                if (current.Count > 0) { groups.Add(current); current = new List<OcrChar>(); }
                continue;
            }
            current.Add(ch);
        }
        if (current.Count > 0) groups.Add(current);

        if (groups.Count != line.Words.Count) return null;
        for (int i = 0; i < groups.Count; i++)
        {
            var text = string.Concat(groups[i].Select(c => c.Value));
            if (!string.Equals(text, line.Words[i].Text, StringComparison.Ordinal)) return null;
        }
        return groups.ToArray();
    }

    /// <summary>
    /// Appends Tesseract's per-glyph hOCR properties (<c>x_bboxes</c>, <c>x_confs</c>) to a word title,
    /// or nothing at all when the line carries no character detail.
    /// </summary>
    private static void AppendCharacterProperties(StringBuilder sb, IReadOnlyList<OcrChar> characters)
    {
        if (characters.Count == 0) return;

        sb.Append("; x_bboxes");
        foreach (var ch in characters)
        {
            var b = ch.BoundingBox;
            sb.Append(' ').Append((int)Math.Round(b.MinX)).Append(' ').Append((int)Math.Round(b.MinY))
              .Append(' ').Append((int)Math.Round(b.MaxX)).Append(' ').Append((int)Math.Round(b.MaxY));
        }
        sb.Append("; x_confs");
        foreach (var ch in characters)
        {
            sb.Append(' ').Append(Conf100(ch.Confidence));
        }
    }

    /// <summary>
    /// Splits a line into whitespace-separated words, approximating each word's box by allocating the
    /// line width proportionally to character count. Used when the recognizer produced no word geometry
    /// (<see cref="RecognitionOptions.WordLevelDetail"/> left at its default).
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

    /// <summary>
    /// Escapes the five XML entities, after dropping the characters XML 1.0 forbids outright. hOCR and ALTO
    /// are consumed by XML parsers, which reject a raw C0 control outright -- and no escape sequence for one
    /// exists, so it has to be removed rather than encoded.
    /// </summary>
    private static string Xml(string s) => StructureDocxExporter.SanitizeXmlText(s)
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
