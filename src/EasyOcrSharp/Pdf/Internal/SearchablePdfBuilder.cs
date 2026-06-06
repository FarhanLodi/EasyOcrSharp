using System.Globalization;
using System.Text;
using EasyOcrSharp.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace EasyOcrSharp.Pdf.Internal;

/// <summary>
/// Builds a searchable PDF by hand: each page is the rendered image (embedded as a JPEG XObject) with
/// an invisible OCR text layer (text render mode 3) positioned over it. Uses the standard base-14
/// Helvetica font, so no font files or font resolver are required and the output is fully self-contained.
/// Pure (no native dependency) and therefore unit-testable on its own.
/// </summary>
internal sealed class SearchablePdfBuilder
{
    private sealed record Page(byte[] Jpeg, int PixelWidth, int PixelHeight, double WidthPt, double HeightPt, string Content);

    private readonly List<Page> _pages = new();

    /// <summary>Adds one page: its rendered image and the OCR result whose text becomes the hidden layer.</summary>
    public void AddPage(Image<Rgb24> image, OcrResult ocr, int dpi, int jpegQuality)
    {
        double scale = 72.0 / dpi;                 // points per pixel (PDF user space is 72 dpi)
        double widthPt = image.Width * scale;
        double heightPt = image.Height * scale;

        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = jpegQuality });

        var content = BuildContent(ocr, widthPt, heightPt, scale);
        _pages.Add(new Page(ms.ToArray(), image.Width, image.Height, widthPt, heightPt, content));
    }

    /// <summary>Serializes the accumulated pages to a complete PDF document.</summary>
    public byte[] Build()
    {
        // Object numbering: 1=Catalog, 2=Pages, 3=Font, then per page (content, image, page).
        const int firstPageObj = 4;
        int objCount = 3 + _pages.Count * 3;

        var stream = new MemoryStream();
        var offsets = new long[objCount + 1]; // 1-based; index 0 unused

        WriteAscii(stream, "%PDF-1.7\n");
        // Binary marker so tools treat the file as binary.
        stream.WriteByte((byte)'%');
        stream.Write(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 });
        stream.WriteByte((byte)'\n');

        int ContentObjNum(int p) => firstPageObj + p * 3;
        int ImageObjNum(int p) => firstPageObj + p * 3 + 1;
        int PageObjNum(int p) => firstPageObj + p * 3 + 2;

        // 1: Catalog
        offsets[1] = stream.Position;
        WriteAscii(stream, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        // 2: Pages
        offsets[2] = stream.Position;
        var kids = new StringBuilder();
        for (int p = 0; p < _pages.Count; p++)
        {
            if (p > 0) kids.Append(' ');
            kids.Append(PageObjNum(p)).Append(" 0 R");
        }
        WriteAscii(stream, $"2 0 obj\n<< /Type /Pages /Count {_pages.Count} /Kids [{kids}] >>\nendobj\n");

        // 3: Font (base-14 Helvetica, WinAnsi so Latin text copies/searches correctly)
        offsets[3] = stream.Position;
        WriteAscii(stream, "3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");

        for (int p = 0; p < _pages.Count; p++)
        {
            var page = _pages[p];

            // Content stream
            offsets[ContentObjNum(p)] = stream.Position;
            var contentBytes = Encoding.Latin1.GetBytes(page.Content);
            WriteAscii(stream, $"{ContentObjNum(p)} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
            stream.Write(contentBytes);
            WriteAscii(stream, "\nendstream\nendobj\n");

            // Image (JPEG / DCTDecode)
            offsets[ImageObjNum(p)] = stream.Position;
            WriteAscii(stream,
                $"{ImageObjNum(p)} 0 obj\n<< /Type /XObject /Subtype /Image /Width {page.PixelWidth} /Height {page.PixelHeight} " +
                $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {page.Jpeg.Length} >>\nstream\n");
            stream.Write(page.Jpeg);
            WriteAscii(stream, "\nendstream\nendobj\n");

            // Page
            offsets[PageObjNum(p)] = stream.Position;
            WriteAscii(stream,
                $"{PageObjNum(p)} 0 obj\n<< /Type /Page /Parent 2 0 R " +
                $"/MediaBox [0 0 {Num(page.WidthPt)} {Num(page.HeightPt)}] " +
                $"/Resources << /XObject << /Im0 {ImageObjNum(p)} 0 R >> /Font << /F1 3 0 R >> >> " +
                $"/Contents {ContentObjNum(p)} 0 R >>\nendobj\n");
        }

        // xref
        long xrefPos = stream.Position;
        WriteAscii(stream, $"xref\n0 {objCount + 1}\n");
        WriteAscii(stream, "0000000000 65535 f \n");
        for (int i = 1; i <= objCount; i++)
        {
            WriteAscii(stream, $"{offsets[i]:D10} 00000 n \n");
        }

        WriteAscii(stream, $"trailer\n<< /Size {objCount + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
        return stream.ToArray();
    }

    /// <summary>
    /// Builds the page content stream: draw the image full-bleed, then emit invisible (Tr 3) text for
    /// each recognized line, positioned to match its on-page location.
    /// </summary>
    private static string BuildContent(OcrResult ocr, double widthPt, double heightPt, double scale)
    {
        var sb = new StringBuilder();

        // Draw the page image to fill the MediaBox.
        sb.Append("q\n").Append(Num(widthPt)).Append(" 0 0 ").Append(Num(heightPt)).Append(" 0 0 cm\n/Im0 Do\nQ\n");

        // Invisible OCR text layer.
        sb.Append("BT\n3 Tr\n");
        foreach (var line in ocr.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            var b = line.BoundingBox;
            double size = Math.Max(1.0, (b.MaxY - b.MinY) * scale);
            double x = b.MinX * scale;
            double yBaseline = heightPt - b.MaxY * scale; // PDF origin is bottom-left

            sb.Append("/F1 ").Append(Num(size)).Append(" Tf\n");
            sb.Append("1 0 0 1 ").Append(Num(x)).Append(' ').Append(Num(yBaseline)).Append(" Tm\n");
            sb.Append('(').Append(EscapePdfText(line.Text)).Append(") Tj\n");
        }
        sb.Append("ET\n");
        return sb.ToString();
    }

    private static string EscapePdfText(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (char ch in text)
        {
            // WinAnsi/Latin1 only; replace anything outside with '?' so the (invisible) layer stays valid.
            char c = ch > 0xFF ? '?' : ch;
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '(': sb.Append("\\("); break;
                case ')': sb.Append("\\)"); break;
                case '\r': sb.Append(' '); break;
                case '\n': sb.Append(' '); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static void WriteAscii(Stream stream, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        stream.Write(bytes);
    }
}
