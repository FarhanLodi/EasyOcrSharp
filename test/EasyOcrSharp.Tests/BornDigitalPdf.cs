using System.Globalization;
using System.Text;
using EasyImageSharp;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Writes small <i>born-digital</i> PDFs for tests: pages carrying real, visible text objects (base-14
/// Helvetica, <c>WinAnsiEncoding</c>), optionally over a full-page raster image, so the embedded-text path
/// can be checked against text whose content and position are known exactly. The library's own
/// <c>SearchablePdfBuilder</c> only writes invisible OCR layers, which is not what a word processor produces.
/// </summary>
internal sealed class BornDigitalPdf
{
    /// <summary>US Letter, in points.</summary>
    public const double LetterWidth = 612, LetterHeight = 792;

    /// <summary>ISO A4, in points.</summary>
    public const double A4Width = 595.28, A4Height = 841.89;

    /// <summary>One run of visible text.</summary>
    /// <param name="Text">WinAnsi text; one <c>Tj</c>.</param>
    /// <param name="X">Left edge of the run in points from the page's left edge.</param>
    /// <param name="Baseline">Baseline in points from the page's <b>bottom</b> edge (PDF user space).</param>
    /// <param name="Size">Font size in points.</param>
    /// <param name="MatrixScaled">
    /// Draw at <c>/F1 1 Tf</c> with the size in the text matrix instead — what many generators emit, and why
    /// PDFium can report a 1 pt font for 11 pt text.
    /// </param>
    public sealed record TextRun(string Text, double X, double Baseline, double Size = 11, bool MatrixScaled = false);

    private sealed record Page(double WidthPt, double HeightPt, IReadOnlyList<TextRun> Runs, byte[]? Jpeg, int ImageWidth, int ImageHeight);

    private readonly List<Page> _pages = new();

    /// <summary>
    /// Give the font a <c>/ToUnicode</c> map that sends every code to the private-use area — the classic broken
    /// map that renders perfectly but extracts as garbage.
    /// </summary>
    public bool BrokenToUnicode { get; init; }

    /// <summary>Adds a page with the given text runs, optionally drawn over a full-page image (a "scan").</summary>
    public BornDigitalPdf AddPage(double widthPt, double heightPt, IEnumerable<TextRun> runs, Image<Rgb24>? background = null)
    {
        byte[]? jpeg = null;
        if (background is not null)
        {
            using var ms = new MemoryStream();
            background.Save(ms, new JpegEncoder { Quality = 90 });
            jpeg = ms.ToArray();
        }

        _pages.Add(new Page(widthPt, heightPt, runs.ToList(), jpeg, background?.Width ?? 0, background?.Height ?? 0));
        return this;
    }

    /// <summary>Adds a Letter page with one run per line, 11 pt, starting 72 pt from the top and left, 18 pt apart.</summary>
    public BornDigitalPdf AddTextPage(IReadOnlyList<string> lines, double widthPt = LetterWidth, double heightPt = LetterHeight, bool matrixScaled = false)
        => AddPage(widthPt, heightPt, LinesAt(lines, heightPt, matrixScaled));

    /// <summary>The runs <see cref="AddTextPage"/> lays out — exposed so tests can predict positions.</summary>
    public static IEnumerable<TextRun> LinesAt(IReadOnlyList<string> lines, double heightPt = LetterHeight, bool matrixScaled = false)
        => lines.Select((text, i) => new TextRun(text, 72, heightPt - 72 - i * 18, 11, matrixScaled));

    /// <summary>Serializes the document.</summary>
    public byte[] Build()
    {
        // 1 Catalog, 2 Pages, 3 Font, 4 ToUnicode (always allocated; only referenced when broken), then per page:
        // content, page, image.
        const int firstPageObj = 5;
        int objCount = 4 + _pages.Count * 3;
        var offsets = new long[objCount + 1];
        var stream = new MemoryStream();

        Write(stream, "%PDF-1.7\n%âãÏÓ\n");

        string kids = string.Join(" ", Enumerable.Range(0, _pages.Count).Select(p => $"{firstPageObj + p * 3 + 1} 0 R"));
        Object(stream, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(stream, offsets, 2, $"<< /Type /Pages /Count {_pages.Count} /Kids [{kids}] >>");
        Object(stream, offsets, 3,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding" +
            (BrokenToUnicode ? " /ToUnicode 4 0 R" : string.Empty) + " >>");
        StreamObject(stream, offsets, 4, string.Empty, Encoding.ASCII.GetBytes(PrivateUseCMap));

        for (int p = 0; p < _pages.Count; p++)
        {
            var page = _pages[p];
            int content = firstPageObj + p * 3, pageObj = content + 1, image = content + 2;

            var sb = new StringBuilder();
            if (page.Jpeg is not null)
                sb.Append($"q {Num(page.WidthPt)} 0 0 {Num(page.HeightPt)} 0 0 cm /Im0 Do Q\n");
            foreach (var run in page.Runs)
            {
                sb.Append("BT\n");
                if (run.MatrixScaled)
                    sb.Append($"/F1 1 Tf {Num(run.Size)} 0 0 {Num(run.Size)} {Num(run.X)} {Num(run.Baseline)} Tm\n");
                else
                    sb.Append($"/F1 {Num(run.Size)} Tf 1 0 0 1 {Num(run.X)} {Num(run.Baseline)} Tm\n");
                sb.Append('(').Append(Escape(run.Text)).Append(") Tj\nET\n");
            }
            StreamObject(stream, offsets, content, string.Empty, Encoding.Latin1.GetBytes(sb.ToString()));

            string xobject = page.Jpeg is null ? string.Empty : $" /XObject << /Im0 {image} 0 R >>";
            Object(stream, offsets, pageObj,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Num(page.WidthPt)} {Num(page.HeightPt)}] " +
                $"/Resources << /Font << /F1 3 0 R >>{xobject} >> /Contents {content} 0 R >>");

            if (page.Jpeg is not null)
            {
                StreamObject(stream, offsets, image,
                    $"/Type /XObject /Subtype /Image /Width {page.ImageWidth} /Height {page.ImageHeight} " +
                    "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode ",
                    page.Jpeg);
            }
            else
            {
                // Keep the numbering dense: an unused, harmless object.
                Object(stream, offsets, image, "null");
            }
        }

        long xref = stream.Position;
        var tail = new StringBuilder();
        tail.Append($"xref\n0 {objCount + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= objCount; i++)
            tail.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        tail.Append($"trailer\n<< /Size {objCount + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(stream, tail.ToString());
        return stream.ToArray();
    }

    private const string PrivateUseCMap =
        "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
        "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
        "/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n" +
        "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
        "1 beginbfrange\n<20> <7E> <E000>\nendbfrange\n" +
        "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n";

    private static void Object(MemoryStream stream, long[] offsets, int number, string body)
    {
        offsets[number] = stream.Position;
        Write(stream, $"{number} 0 obj\n{body}\nendobj\n");
    }

    private static void StreamObject(MemoryStream stream, long[] offsets, int number, string dictionary, byte[] data)
    {
        offsets[number] = stream.Position;
        Write(stream, $"{number} 0 obj\n<< {dictionary}/Length {data.Length} >>\nstream\n");
        stream.Write(data);
        Write(stream, "\nendstream\nendobj\n");
    }

    private static void Write(MemoryStream stream, string text) => stream.Write(Encoding.Latin1.GetBytes(text));

    private static string Escape(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal)
               .Replace("(", "\\(", StringComparison.Ordinal)
               .Replace(")", "\\)", StringComparison.Ordinal);

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
