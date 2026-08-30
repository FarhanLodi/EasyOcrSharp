using System.Globalization;
using System.IO.Compression;
using System.Text;
using EasyOcrSharp.Models;
using Microsoft.Extensions.Logging;
using EasyImageSharp;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.PixelFormats;

namespace EasyOcrSharp.Pdf.Internal;

/// <summary>
/// Builds a searchable PDF by hand: each page is the rendered image (embedded as a JPEG XObject) with
/// an invisible OCR text layer (text render mode 3) positioned over it.
/// </summary>
/// <remarks>
/// <para>
/// Two text-layer flavours exist, chosen automatically per document:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Standard</b> — the base-14 Helvetica font with <c>WinAnsiEncoding</c>. No font file is needed
///     and the output is fully self-contained. Used whenever every recognized character is Latin-1,
///     which keeps the output byte-for-byte what it has always been.
///   </description></item>
///   <item><description>
///     <b>Embedded</b> — a <c>/Type0</c> composite font with <c>/Identity-H</c> encoding, a
///     <c>/CIDFontType2</c> descendant and a subsetted TrueType font in <c>/FontFile2</c>, plus a
///     <c>/ToUnicode</c> CMap so copy-paste yields real characters. Used when the text contains anything
///     outside Latin-1 (Chinese, Japanese, Korean, Arabic, Devanagari, Thai, Greek, Cyrillic, …) and a
///     suitable font can be found — see <see cref="SystemFontProbe"/>. The package ships no font of its
///     own; if none is installed the builder silently falls back to the standard layer and reports that
///     through <see cref="TextLayerFontStatus"/>.
///   </description></item>
/// </list>
/// <para>Pure (no native dependency) and therefore unit-testable on its own.</para>
/// </remarks>
internal sealed class SearchablePdfBuilder
{
    private sealed record Page(
        byte[] Jpeg,
        int PixelWidth,
        int PixelHeight,
        double WidthPt,
        double HeightPt,
        double Scale,
        OcrResult Ocr);

    /// <summary>One positioned, invisible piece of text: a whole line or a single word.</summary>
    private sealed record TextRun(double X, double Baseline, double Size, double HorizontalScale, ushort[] Glyphs);

    private readonly List<Page> _pages = new();
    private readonly PdfOcrOptions _options;
    private readonly ILogger? _logger;

    /// <summary>Creates a builder using <paramref name="options"/> for text-layer font selection.</summary>
    public SearchablePdfBuilder(PdfOcrOptions? options = null)
    {
        _options = options ?? new PdfOcrOptions();
        _logger = _options.Logger;
    }

    /// <summary>Gets which text-layer font the last <see cref="Build"/> call actually used.</summary>
    public PdfTextLayerFontStatus TextLayerFontStatus { get; private set; } = PdfTextLayerFontStatus.Standard;

    /// <summary>Gets the font file embedded by the last <see cref="Build"/> call, or null when none was.</summary>
    public string? TextLayerFontPath { get; private set; }

    /// <summary>Adds one page: its rendered image and the OCR result whose text becomes the hidden layer.</summary>
    public void AddPage(Image<Rgb24> image, OcrResult ocr, int dpi, int jpegQuality)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(ocr);

        // JpegEncoder throws NotSupportedException past 65535px in either dimension. A very wide, short page
        // (60in x 1in at 1200 DPI = 72000 x 1200 = 86 Mpx) passes the rasterizer's megapixel guard and would
        // then throw an untyped exception from inside the page handler, past this subsystem's typed contract.
        const int MaxJpegDimension = 65535;
        if (image.Width > MaxJpegDimension || image.Height > MaxJpegDimension)
        {
            throw new PdfProcessingException(
                $"Page renders to {image.Width}x{image.Height}px at {dpi} DPI, and the JPEG format cannot " +
                $"encode a dimension above {MaxJpegDimension}px. Lower PdfOcrOptions.Dpi.");
        }

        double scale = 72.0 / dpi;                 // points per pixel (PDF user space is 72 dpi)
        double widthPt = image.Width * scale;
        double heightPt = image.Height * scale;

        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = jpegQuality });

        _pages.Add(new Page(ms.ToArray(), image.Width, image.Height, widthPt, heightPt, scale, ocr));
    }

    /// <summary>Serializes the accumulated pages to a complete PDF document.</summary>
    public byte[] Build()
    {
        var font = ResolveTextLayerFont();
        return font is null ? BuildStandard() : BuildWithEmbeddedFont(font);
    }

    // ---------------------------------------------------------------------------------------------
    // Font selection
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Decides whether the text layer needs a real embedded font. Pure, so the decision table is
    /// directly testable: <see cref="PdfTextLayerFontMode.Never"/> never embeds,
    /// <see cref="PdfTextLayerFontMode.Always"/> always tries, and the default
    /// <see cref="PdfTextLayerFontMode.Auto"/> tries only when the text leaves Latin-1 behind.
    /// </summary>
    internal static bool RequiresEmbeddedFont(PdfTextLayerFontMode mode, bool textIsLatin1Only) => mode switch
    {
        PdfTextLayerFontMode.Never => false,
        PdfTextLayerFontMode.Always => true,
        _ => !textIsLatin1Only,
    };

    private TrueTypeFont? ResolveTextLayerFont()
    {
        TextLayerFontStatus = PdfTextLayerFontStatus.Standard;
        TextLayerFontPath = null;

        var codePoints = CollectCodePoints(out bool latin1Only);
        if (!RequiresEmbeddedFont(_options.TextLayerFont, latin1Only) || codePoints.Count == 0)
        {
            return null;
        }

        var font = SystemFontProbe.Resolve(_options.TextLayerFontPath, codePoints);
        if (font is null)
        {
            if (!latin1Only)
            {
                TextLayerFontStatus = PdfTextLayerFontStatus.Unavailable;
                _logger?.LogWarning(
                    "No installed font could render the non-Latin-1 text of this document; the searchable PDF " +
                    "keeps the standard Helvetica text layer and those characters are replaced with '?'. " +
                    "Set PdfOcrOptions.TextLayerFontPath to a .ttf/.ttc file to make them searchable.");
            }

            return null;
        }

        TextLayerFontStatus = PdfTextLayerFontStatus.Embedded;
        TextLayerFontPath = font.FilePath;
        return font;
    }

    /// <summary>Collects every code point the text layer has to render, and whether they all fit Latin-1.</summary>
    private HashSet<int> CollectCodePoints(out bool latin1Only)
    {
        var codePoints = new HashSet<int>();
        bool allLatin1 = true;

        void Add(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            foreach (var rune in text.EnumerateRunes())
            {
                int value = rune.Value is '\r' or '\n' ? ' ' : rune.Value;
                // Representable by the declared /WinAnsiEncoding, not merely by Latin-1 — see TryMapWinAnsi.
                if (!TryMapWinAnsi(value, out _))
                {
                    allLatin1 = false;
                }

                codePoints.Add(value);
            }
        }

        foreach (var page in _pages)
        {
            foreach (var line in page.Ocr.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.Text))
                {
                    continue;
                }

                Add(line.Text);
                foreach (var word in line.Words)
                {
                    Add(word.Text);
                }
            }
        }

        latin1Only = allLatin1;
        return codePoints;
    }

    // ---------------------------------------------------------------------------------------------
    // Standard (base-14 Helvetica / WinAnsi) output — unchanged, byte for byte
    // ---------------------------------------------------------------------------------------------

    private byte[] BuildStandard()
    {
        // Object numbering: 1=Catalog, 2=Pages, 3=Font, then per page (content, image, page).
        const int firstPageObj = 4;
        int objCount = 3 + _pages.Count * 3;

        var stream = new MemoryStream();
        var offsets = new long[objCount + 1]; // 1-based; index 0 unused

        WriteHeader(stream);

        int ContentObjNum(int p) => firstPageObj + p * 3;
        int ImageObjNum(int p) => firstPageObj + p * 3 + 1;
        int PageObjNum(int p) => firstPageObj + p * 3 + 2;

        // 1: Catalog
        offsets[1] = stream.Position;
        WriteAscii(stream, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        // 2: Pages
        offsets[2] = stream.Position;
        WriteAscii(stream, $"2 0 obj\n<< /Type /Pages /Count {_pages.Count} /Kids [{BuildKids(PageObjNum)}] >>\nendobj\n");

        // 3: Font (base-14 Helvetica, WinAnsi so Latin text copies/searches correctly)
        offsets[3] = stream.Position;
        WriteAscii(stream, "3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");

        for (int p = 0; p < _pages.Count; p++)
        {
            var page = _pages[p];

            // Content stream
            offsets[ContentObjNum(p)] = stream.Position;
            var contentBytes = Encoding.Latin1.GetBytes(BuildStandardContent(page));
            WriteAscii(stream, $"{ContentObjNum(p)} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
            stream.Write(contentBytes);
            WriteAscii(stream, "\nendstream\nendobj\n");

            WriteImageObject(stream, offsets, ImageObjNum(p), page);
            WritePageObject(stream, offsets, PageObjNum(p), page, ContentObjNum(p), ImageObjNum(p), fontObj: 3);
        }

        WriteTrailer(stream, offsets, objCount);
        return stream.ToArray();
    }

    /// <summary>
    /// Builds the page content stream: draw the image full-bleed, then emit invisible (Tr 3) text for
    /// each recognized line, positioned to match its on-page location.
    /// </summary>
    private static string BuildStandardContent(Page page)
    {
        var sb = new StringBuilder();
        AppendImage(sb, page);

        // Invisible OCR text layer.
        sb.Append("BT\n3 Tr\n");
        foreach (var line in page.Ocr.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            var b = line.BoundingBox;
            double size = Math.Max(1.0, (b.MaxY - b.MinY) * page.Scale);
            double x = b.MinX * page.Scale;
            double yBaseline = page.HeightPt - b.MaxY * page.Scale; // PDF origin is bottom-left

            sb.Append("/F1 ").Append(Num(size)).Append(" Tf\n");
            sb.Append("1 0 0 1 ").Append(Num(x)).Append(' ').Append(Num(yBaseline)).Append(" Tm\n");
            sb.Append('(').Append(EscapePdfText(line.Text)).Append(") Tj\n");
        }
        sb.Append("ET\n");
        return sb.ToString();
    }

    /// <summary>
    /// WinAnsiEncoding's 0x80-0x9F block — where it departs from Latin-1. These are the typographic
    /// characters OCR produces most often on ordinary English scans (curly quotes, en/em dash, ellipsis,
    /// euro), and the standard text layer declares <c>/WinAnsiEncoding</c>, so all of them are encodable.
    /// </summary>
    private static readonly Dictionary<int, byte> WinAnsiHighRange = new()
    {
        [0x20AC] = 0x80, [0x201A] = 0x82, [0x0192] = 0x83, [0x201E] = 0x84, [0x2026] = 0x85,
        [0x2020] = 0x86, [0x2021] = 0x87, [0x02C6] = 0x88, [0x2030] = 0x89, [0x0160] = 0x8A,
        [0x2039] = 0x8B, [0x0152] = 0x8C, [0x017D] = 0x8E, [0x2018] = 0x91, [0x2019] = 0x92,
        [0x201C] = 0x93, [0x201D] = 0x94, [0x2022] = 0x95, [0x2013] = 0x96, [0x2014] = 0x97,
        [0x02DC] = 0x98, [0x2122] = 0x99, [0x0161] = 0x9A, [0x203A] = 0x9B, [0x0153] = 0x9C,
        [0x017E] = 0x9E, [0x0178] = 0x9F,
    };

    /// <summary>
    /// Maps a code point to its WinAnsiEncoding byte, reporting whether the standard (non-embedded) text
    /// layer can represent it at all. This is both the escaper's table and the font-embedding trigger.
    /// <para>
    /// Both sites previously tested <c>&gt; 0xFF</c> — i.e. Latin-1, not the WinAnsi the layer actually
    /// declares. That destroyed every character in the 0x80-0x9F block: an English invoice OCR'd as
    /// <c>don't</c> (U+2019) or <c>€1,234</c> became <c>don?t</c> / <c>?1,234</c> under
    /// <see cref="PdfTextLayerFontMode.Never"/> and was unsearchable. Under the default
    /// <see cref="PdfTextLayerFontMode.Auto"/> it was arguably worse: one curly apostrophe flipped the
    /// document onto the embedded-font path, costing a recursive font-directory scan and a multi-megabyte
    /// <c>/FontFile2</c> — and on a headless container with no fonts installed, the <c>?</c> appeared anyway.
    /// </para>
    /// </summary>
    private static bool TryMapWinAnsi(int codePoint, out byte encoded)
    {
        if (codePoint is (>= 0x20 and <= 0x7E) or (>= 0xA0 and <= 0xFF))
        {
            encoded = (byte)codePoint;
            return true;
        }
        return WinAnsiHighRange.TryGetValue(codePoint, out encoded);
    }

    private static string EscapePdfText(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var rune in text.EnumerateRunes())
        {
            int value = rune.Value is '\r' or '\n' ? ' ' : rune.Value;
            if (!TryMapWinAnsi(value, out byte b))
            {
                b = (byte)'?';   // genuinely outside WinAnsi; the layer stays valid, that glyph is not searchable
            }

            switch (b)
            {
                case (byte)'\\': sb.Append("\\\\"); break;
                case (byte)'(': sb.Append("\\("); break;
                case (byte)')': sb.Append("\\)"); break;
                default:
                    // Bytes >= 0x20 go out raw: this string is serialized with Encoding.Latin1
                    // (see BuildStandardContent), which maps char n to byte n across the whole 0x00-0xFF
                    // range, so the WinAnsi byte lands in the file exactly as computed. Control bytes have
                    // no place in a text-showing operator, so escape those octally instead.
                    if (b < 0x20)
                    {
                        sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    }
                    else
                    {
                        sb.Append((char)b);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------
    // Unicode output (Type0 / CIDFontType2 / Identity-H with an embedded subset)
    // ---------------------------------------------------------------------------------------------

    private byte[] BuildWithEmbeddedFont(TrueTypeFont font)
    {
        // Object numbering: 1=Catalog, 2=Pages, 3=Type0 font, 4=CIDFont, 5=FontDescriptor,
        // 6=FontFile2, 7=ToUnicode, then per page (content, image, page).
        const int fontObj = 3;
        const int cidFontObj = 4;
        const int descriptorObj = 5;
        const int fontFileObj = 6;
        const int toUnicodeObj = 7;
        const int firstPageObj = 8;
        int objCount = 7 + _pages.Count * 3;

        var toUnicode = new Dictionary<ushort, string>();
        var contents = new List<string>(_pages.Count);
        int dropped = 0;

        foreach (var page in _pages)
        {
            contents.Add(BuildUnicodeContent(page, font, toUnicode, ref dropped));
        }

        if (dropped > 0)
        {
            _logger?.LogWarning(
                "The text layer dropped {Count} character(s) that the embedded font {Font} has no glyph for; " +
                "they will not be searchable.", dropped, font.PostScriptName);
        }

        var glyphs = toUnicode.Keys.ToList();
        glyphs.Sort();

        var widths = new Dictionary<ushort, int>(glyphs.Count);
        foreach (var glyph in glyphs)
        {
            widths[glyph] = font.ToThousandths(font.GetAdvanceWidth(glyph));
        }

        var subset = font.CreateSubset(glyphs);
        var compressedSubset = Deflate(subset);
        var toUnicodeBytes = Encoding.ASCII.GetBytes(BuildToUnicodeCMap(toUnicode));
        string baseFont = $"{SubsetTag(font.PostScriptName, glyphs)}+{font.PostScriptName}";

        var stream = new MemoryStream();
        var offsets = new long[objCount + 1];

        WriteHeader(stream);

        int ContentObjNum(int p) => firstPageObj + p * 3;
        int ImageObjNum(int p) => firstPageObj + p * 3 + 1;
        int PageObjNum(int p) => firstPageObj + p * 3 + 2;

        offsets[1] = stream.Position;
        WriteAscii(stream, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = stream.Position;
        WriteAscii(stream, $"2 0 obj\n<< /Type /Pages /Count {_pages.Count} /Kids [{BuildKids(PageObjNum)}] >>\nendobj\n");

        offsets[fontObj] = stream.Position;
        WriteAscii(stream,
            $"{fontObj} 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /{baseFont} /Encoding /Identity-H " +
            $"/DescendantFonts [{cidFontObj} 0 R] /ToUnicode {toUnicodeObj} 0 R >>\nendobj\n");

        offsets[cidFontObj] = stream.Position;
        WriteAscii(stream,
            $"{cidFontObj} 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{baseFont} " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
            $"/FontDescriptor {descriptorObj} 0 R /DW 1000 /W {BuildWidthArray(widths)} " +
            "/CIDToGIDMap /Identity >>\nendobj\n");

        var box = font.BoundingBox;
        offsets[descriptorObj] = stream.Position;
        WriteAscii(stream,
            $"{descriptorObj} 0 obj\n<< /Type /FontDescriptor /FontName /{baseFont} /Flags {Num(font.Flags)} " +
            $"/FontBBox [{Num(font.ToThousandths(box.MinX))} {Num(font.ToThousandths(box.MinY))} " +
            $"{Num(font.ToThousandths(box.MaxX))} {Num(font.ToThousandths(box.MaxY))}] " +
            $"/ItalicAngle {Num(font.ItalicAngle)} /Ascent {Num(font.ToThousandths(font.Ascent))} " +
            $"/Descent {Num(font.ToThousandths(font.Descent))} /CapHeight {Num(font.ToThousandths(font.CapHeight))} " +
            $"/StemV {Num(font.StemV)} /MissingWidth 1000 /FontFile2 {fontFileObj} 0 R >>\nendobj\n");

        offsets[fontFileObj] = stream.Position;
        WriteAscii(stream,
            $"{fontFileObj} 0 obj\n<< /Length {compressedSubset.Length} /Length1 {subset.Length} " +
            "/Filter /FlateDecode >>\nstream\n");
        stream.Write(compressedSubset);
        WriteAscii(stream, "\nendstream\nendobj\n");

        offsets[toUnicodeObj] = stream.Position;
        WriteAscii(stream, $"{toUnicodeObj} 0 obj\n<< /Length {toUnicodeBytes.Length} >>\nstream\n");
        stream.Write(toUnicodeBytes);
        WriteAscii(stream, "\nendstream\nendobj\n");

        for (int p = 0; p < _pages.Count; p++)
        {
            var page = _pages[p];

            offsets[ContentObjNum(p)] = stream.Position;
            var contentBytes = Encoding.ASCII.GetBytes(contents[p]);
            WriteAscii(stream, $"{ContentObjNum(p)} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
            stream.Write(contentBytes);
            WriteAscii(stream, "\nendstream\nendobj\n");

            WriteImageObject(stream, offsets, ImageObjNum(p), page);
            WritePageObject(stream, offsets, PageObjNum(p), page, ContentObjNum(p), ImageObjNum(p), fontObj);
        }

        WriteTrailer(stream, offsets, objCount);
        return stream.ToArray();
    }

    /// <summary>
    /// Builds the content stream of one page for the embedded-font layer. Text is written as hex strings
    /// of glyph identifiers, per word when the OCR result carries word geometry and per line otherwise
    /// (word geometry is opt-in, so per line is the common case), and horizontally scaled so the
    /// invisible run covers the same width as the recognized ink.
    /// </summary>
    private static string BuildUnicodeContent(Page page, TrueTypeFont font, Dictionary<ushort, string> toUnicode, ref int dropped)
    {
        var sb = new StringBuilder();
        AppendImage(sb, page);

        sb.Append("BT\n3 Tr\n");
        foreach (var run in BuildRuns(page, font, toUnicode, ref dropped))
        {
            sb.Append("/F1 ").Append(Num(run.Size)).Append(" Tf\n");
            sb.Append(Num(run.HorizontalScale)).Append(" Tz\n");
            sb.Append("1 0 0 1 ").Append(Num(run.X)).Append(' ').Append(Num(run.Baseline)).Append(" Tm\n");
            sb.Append(ToGlyphHex(run.Glyphs)).Append(" Tj\n");
        }
        sb.Append("ET\n");
        return sb.ToString();
    }

    private static List<TextRun> BuildRuns(Page page, TrueTypeFont font, Dictionary<ushort, string> toUnicode, ref int dropped)
    {
        var runs = new List<TextRun>();
        foreach (var line in page.Ocr.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text))
            {
                continue;
            }

            var box = line.BoundingBox;
            double size = Math.Max(1.0, box.Height * page.Scale);

            if (HasUsableWordGeometry(line))
            {
                foreach (var word in line.Words)
                {
                    var wordBox = word.BoundingBox;
                    AddRun(runs, font, word.Text, toUnicode, ref dropped,
                        x: wordBox.MinX * page.Scale,
                        baseline: page.HeightPt - (wordBox.MaxY * page.Scale),
                        size: size,
                        targetWidth: wordBox.Width * page.Scale);
                }
            }
            else
            {
                AddRun(runs, font, line.Text, toUnicode, ref dropped,
                    x: box.MinX * page.Scale,
                    baseline: page.HeightPt - (box.MaxY * page.Scale),
                    size: size,
                    targetWidth: box.Width * page.Scale);
            }
        }

        return runs;
    }

    /// <summary>
    /// True when every word of the line carries a usable box, i.e. word-level detail was requested and
    /// produced geometry. <see cref="OcrLine.Words"/> is empty by default, so this is normally false.
    /// </summary>
    private static bool HasUsableWordGeometry(OcrLine line)
    {
        if (line.Words.Count == 0)
        {
            return false;
        }

        foreach (var word in line.Words)
        {
            if (string.IsNullOrWhiteSpace(word.Text) || word.BoundingBox.Width <= 0 || word.BoundingBox.Height <= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddRun(
        List<TextRun> runs,
        TrueTypeFont font,
        string text,
        Dictionary<ushort, string> toUnicode,
        ref int dropped,
        double x,
        double baseline,
        double size,
        double targetWidth)
    {
        var glyphs = MapGlyphs(font, text, toUnicode, ref dropped);
        if (glyphs.Length == 0)
        {
            return;
        }

        double natural = 0;
        foreach (var glyph in glyphs)
        {
            natural += font.GetAdvanceWidth(glyph);
        }

        natural = natural / font.UnitsPerEm * size;
        double horizontalScale = natural > 0 && targetWidth > 0
            ? Math.Clamp(targetWidth / natural * 100.0, 10.0, 1000.0)
            : 100.0;

        runs.Add(new TextRun(x, baseline, size, horizontalScale, glyphs));
    }

    /// <summary>
    /// Maps text to glyph identifiers, recording the reverse mapping the <c>/ToUnicode</c> CMap needs.
    /// Characters the font cannot render are dropped (counted in <paramref name="dropped"/>) rather than
    /// emitted as <c>.notdef</c>, which would neither render nor extract.
    /// </summary>
    private static ushort[] MapGlyphs(TrueTypeFont font, string text, Dictionary<ushort, string> toUnicode, ref int dropped)
    {
        var glyphs = new List<ushort>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value is '\r' or '\n' ? new Rune(' ') : rune;
            if (!font.TryGetGlyphId(value.Value, out ushort glyph))
            {
                dropped++;
                continue;
            }

            glyphs.Add(glyph);
            RecordToUnicode(toUnicode, glyph, value);
        }

        return glyphs.ToArray();
    }

    /// <summary>
    /// Records the glyph → text entry the <c>/ToUnicode</c> CMap is built from, resolving the case where two
    /// different code points share one glyph.
    /// <para>
    /// The font is embedded with <c>/CIDToGIDMap /Identity</c>, so a CID <i>is</i> a glyph id and the reverse
    /// map has one slot per glyph. Sharing is common: a symbol font aliases every <c>U+F0xx</c> code point to
    /// its low byte (see <c>TrueTypeSubsetter</c>'s cmap handling), so <b>every</b> glyph in such a font has
    /// two code points; in ordinary text fonts U+00A0 shares the space glyph, U+00AD the hyphen, U+2126 the
    /// omega, and CJK compatibility ideographs (U+F900–U+FA6D) share glyphs with their unified forms.
    /// </para>
    /// <para>
    /// A plain <c>TryAdd</c> kept whichever code point the OCR happened to emit first, so the other one
    /// extracted — and searched — as the wrong character. Preferring a non-private-use code point resolves
    /// the symbol-font case deterministically and in the right direction: the real character wins over its
    /// <c>U+F0xx</c> alias. Genuine BMP-to-BMP ties (U+00A0 vs U+0020) keep first-wins, which is as good as
    /// an Identity map can do — distinguishing those would need a real CID→GID stream.
    /// </para>
    /// </summary>
    private static void RecordToUnicode(Dictionary<ushort, string> toUnicode, ushort glyph, Rune value)
    {
        if (!toUnicode.TryGetValue(glyph, out var existing))
        {
            toUnicode[glyph] = value.ToString();
            return;
        }

        // Replace a private-use placeholder with a real character; never the other way round.
        if (IsPrivateUse(existing) && !IsPrivateUse(value.ToString()))
        {
            toUnicode[glyph] = value.ToString();
        }
    }

    /// <summary>Whether the (single-scalar) text sits in a Unicode private-use area.</summary>
    private static bool IsPrivateUse(string text)
    {
        if (!Rune.TryGetRuneAt(text, 0, out var rune)) return false;
        return rune.Value is (>= 0xE000 and <= 0xF8FF)      // BMP PUA
            or (>= 0xF0000 and <= 0xFFFFD)                   // Plane 15
            or (>= 0x100000 and <= 0x10FFFD);                // Plane 16
    }

    // ---------------------------------------------------------------------------------------------
    // PDF font-object payloads (pure string builders, unit-tested directly)
    // ---------------------------------------------------------------------------------------------

    /// <summary>Formats glyph identifiers as the hex string a <c>Tj</c> operator takes, e.g. <c>&lt;00480065&gt;</c>.</summary>
    internal static string ToGlyphHex(IReadOnlyList<ushort> glyphs)
    {
        var sb = new StringBuilder((glyphs.Count * 4) + 2);
        sb.Append('<');
        foreach (var glyph in glyphs)
        {
            sb.Append(glyph.ToString("X4", CultureInfo.InvariantCulture));
        }

        return sb.Append('>').ToString();
    }

    /// <summary>Formats a string as UTF-16BE hex digits, the value side of a <c>/ToUnicode</c> entry.</summary>
    internal static string ToUtf16BeHex(string text)
    {
        var sb = new StringBuilder(text.Length * 4);
        foreach (char c in text)
        {
            sb.Append(((ushort)c).ToString("X4", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the <c>/W</c> array of a CIDFont: glyph widths in 1/1000 em, grouped into runs of
    /// consecutive identifiers, e.g. <c>[3 [500 600] 10 [250]]</c>.
    /// </summary>
    internal static string BuildWidthArray(IReadOnlyDictionary<ushort, int> widths)
    {
        var glyphs = widths.Keys.ToList();
        glyphs.Sort();

        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < glyphs.Count;)
        {
            int j = i;
            while (j + 1 < glyphs.Count && glyphs[j + 1] == glyphs[j] + 1)
            {
                j++;
            }

            if (sb.Length > 1)
            {
                sb.Append(' ');
            }

            sb.Append(glyphs[i].ToString(CultureInfo.InvariantCulture)).Append(" [");
            for (int k = i; k <= j; k++)
            {
                if (k > i)
                {
                    sb.Append(' ');
                }

                sb.Append(widths[glyphs[k]].ToString(CultureInfo.InvariantCulture));
            }

            sb.Append(']');
            i = j + 1;
        }

        return sb.Append(']').ToString();
    }

    /// <summary>
    /// Builds the <c>/ToUnicode</c> CMap that maps each glyph identifier back to the text it stands for.
    /// Without it a viewer can render and even search the layer, but copy-paste returns glyph indices.
    /// </summary>
    internal static string BuildToUnicodeCMap(IReadOnlyDictionary<ushort, string> glyphToText)
    {
        var glyphs = glyphToText.Keys.ToList();
        glyphs.Sort();

        var sb = new StringBuilder(256 + (glyphs.Count * 20));
        sb.Append("/CIDInit /ProcSet findresource begin\n");
        sb.Append("12 dict begin\n");
        sb.Append("begincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n");
        sb.Append("/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

        // beginbfchar blocks are limited to 100 entries each by the CMap specification.
        for (int i = 0; i < glyphs.Count; i += 100)
        {
            int count = Math.Min(100, glyphs.Count - i);
            sb.Append(count.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            for (int k = i; k < i + count; k++)
            {
                sb.Append('<').Append(glyphs[k].ToString("X4", CultureInfo.InvariantCulture)).Append("> <")
                  .Append(ToUtf16BeHex(glyphToText[glyphs[k]])).Append(">\n");
            }

            sb.Append("endbfchar\n");
        }

        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    /// <summary>
    /// Derives the conventional six-uppercase-letter subset prefix. Deterministic in the font name and
    /// the glyph set, so the same document always produces the same bytes.
    /// </summary>
    internal static string SubsetTag(string baseName, IEnumerable<ushort> glyphs)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in baseName)
            {
                hash = (hash ^ c) * 16777619;
            }

            foreach (var glyph in glyphs)
            {
                hash = (hash ^ glyph) * 16777619;
            }

            var tag = new char[6];
            for (int i = 0; i < tag.Length; i++)
            {
                tag[i] = (char)('A' + (hash % 26));
                hash /= 26;
            }

            return new string(tag);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Shared PDF plumbing
    // ---------------------------------------------------------------------------------------------

    private static void AppendImage(StringBuilder sb, Page page)
    {
        // Draw the page image to fill the MediaBox.
        sb.Append("q\n").Append(Num(page.WidthPt)).Append(" 0 0 ").Append(Num(page.HeightPt)).Append(" 0 0 cm\n/Im0 Do\nQ\n");
    }

    private string BuildKids(Func<int, int> pageObjNum)
    {
        var kids = new StringBuilder();
        for (int p = 0; p < _pages.Count; p++)
        {
            if (p > 0) kids.Append(' ');
            kids.Append(pageObjNum(p)).Append(" 0 R");
        }

        return kids.ToString();
    }

    private static void WriteHeader(MemoryStream stream)
    {
        WriteAscii(stream, "%PDF-1.7\n");
        // Binary marker so tools treat the file as binary.
        stream.WriteByte((byte)'%');
        stream.Write(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 });
        stream.WriteByte((byte)'\n');
    }

    private static void WriteImageObject(MemoryStream stream, long[] offsets, int objNum, Page page)
    {
        offsets[objNum] = stream.Position;
        WriteAscii(stream,
            $"{objNum} 0 obj\n<< /Type /XObject /Subtype /Image /Width {page.PixelWidth} /Height {page.PixelHeight} " +
            $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {page.Jpeg.Length} >>\nstream\n");
        stream.Write(page.Jpeg);
        WriteAscii(stream, "\nendstream\nendobj\n");
    }

    private static void WritePageObject(MemoryStream stream, long[] offsets, int objNum, Page page, int contentObj, int imageObj, int fontObj)
    {
        offsets[objNum] = stream.Position;
        WriteAscii(stream,
            $"{objNum} 0 obj\n<< /Type /Page /Parent 2 0 R " +
            $"/MediaBox [0 0 {Num(page.WidthPt)} {Num(page.HeightPt)}] " +
            $"/Resources << /XObject << /Im0 {imageObj} 0 R >> /Font << /F1 {fontObj} 0 R >> >> " +
            $"/Contents {contentObj} 0 R >>\nendobj\n");
    }

    private static void WriteTrailer(MemoryStream stream, long[] offsets, int objCount)
    {
        long xrefPos = stream.Position;
        WriteAscii(stream, $"xref\n0 {objCount + 1}\n");
        WriteAscii(stream, "0000000000 65535 f \n");
        for (int i = 1; i <= objCount; i++)
        {
            WriteAscii(stream, $"{offsets[i]:D10} 00000 n \n");
        }

        WriteAscii(stream, $"trailer\n<< /Size {objCount + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a PDF integer invariantly. Every number written here goes through
    /// <see cref="WriteAscii(Stream, string)"/>, so an ambient-culture negative sign is not merely unusual —
    /// it is unrepresentable. Under sv-SE / fi-FI / nb-NO / lt-LT, <c>int.ToString()</c> renders the sign as
    /// U+2212 MINUS SIGN (ar-SA and fa-IR prepend a bidi mark), which <see cref="Encoding.ASCII"/> turns into
    /// <c>?</c> — not a PDF number token, so readers reject the object. <c>/Descent</c> and the
    /// <c>/FontBBox</c> corners are negative for essentially every real font.
    /// </summary>
    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void WriteAscii(Stream stream, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        stream.Write(bytes);
    }
}
