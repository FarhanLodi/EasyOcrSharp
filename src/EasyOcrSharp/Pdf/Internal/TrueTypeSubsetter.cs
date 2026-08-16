using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Text;

namespace EasyOcrSharp.Pdf.Internal;

/// <summary>
/// A small, dependency-free reader and subsetter for <c>glyf</c>-flavoured sfnt fonts (<c>.ttf</c> and
/// the first face of a <c>.ttc</c>), used to embed a real Unicode font in the invisible text layer of a
/// searchable PDF.
/// </summary>
/// <remarks>
/// <para>
/// Only what a PDF <c>CIDFontType2</c> needs is understood: the table directory, <c>head</c>,
/// <c>hhea</c>, <c>hmtx</c>, <c>maxp</c>, <c>loca</c>, <c>glyf</c>, <c>cmap</c> and — purely for the font
/// descriptor — <c>OS/2</c>, <c>post</c> and <c>name</c>. CFF-flavoured OpenType (an <c>OTTO</c>
/// signature, i.e. most <c>.otf</c> files) is rejected, because its outlines cannot be embedded through
/// <c>/FontFile2</c>; the caller simply moves on to the next candidate font.
/// </para>
/// <para>
/// The subset keeps the original glyph identifiers — the PDF is written with
/// <c>/CIDToGIDMap /Identity</c>, so CID == GID — and empties the <c>glyf</c> entries of every glyph the
/// document does not use. <c>hmtx</c>, <c>hhea</c>, <c>maxp</c> and the hinting tables are copied
/// verbatim, so a subset of a large CJK font still carries a full-length (but cheap) metrics array while
/// the expensive outline data shrinks to the handful of glyphs actually drawn.
/// </para>
/// <para>All parsing is bounds-checked and every failure is reported as <see langword="null"/>: a corrupt
/// or unsupported font must never throw into the PDF pipeline, it must only make the caller fall back to
/// the standard base-14 text layer.</para>
/// </remarks>
internal sealed class TrueTypeFont
{
    /// <summary>Reads <paramref name="destination"/>.Length bytes starting at <paramref name="offset"/>.</summary>
    private delegate void RangeReader(int offset, Span<byte> destination);

    private readonly record struct TableRecord(int Offset, int Length);

    /// <summary>Refuse absurdly large files outright (the biggest real-world CJK fonts are ~40 MB).</summary>
    private const long MaxFontFileBytes = 192L * 1024 * 1024;

    /// <summary>Upper bound on characters taken from a single <c>cmap</c> subtable (malformed-font guard).</summary>
    private const int MaxCmapEntries = 1 << 20;

    private const uint TagTrueType = 0x00010000u;
    private const uint TagTrue = 0x74727565u;   // 'true' (legacy Apple)
    private const uint TagTtcf = 0x74746366u;   // 'ttcf' (TrueType collection)

    private static readonly string[] OutlineTables = ["head", "hhea", "hmtx", "maxp", "loca", "glyf"];
    private static readonly string[] HintingTables = ["cvt ", "fpgm", "prep"];

    private readonly byte[] _data;
    private readonly FrozenDictionary<string, TableRecord> _tables;
    private readonly Dictionary<int, ushort> _cmap;
    private readonly int[] _loca;              // GlyphCount + 1 offsets into the glyf table
    private readonly ushort[] _advances;       // advance width per glyph, in font units
    private readonly TableRecord _glyf;
    private readonly int _numberOfHMetrics;

    private TrueTypeFont(
        byte[] data,
        string filePath,
        FrozenDictionary<string, TableRecord> tables,
        Dictionary<int, ushort> cmap,
        int[] loca,
        ushort[] advances,
        TableRecord glyf,
        int numberOfHMetrics)
    {
        _data = data;
        _tables = tables;
        _cmap = cmap;
        _loca = loca;
        _advances = advances;
        _glyf = glyf;
        _numberOfHMetrics = numberOfHMetrics;
        FilePath = filePath;
        GlyphCount = advances.Length;
        PostScriptName = "EmbeddedFont";
        BoundingBox = (0, 0, 1000, 1000);
    }

    /// <summary>Gets the file the font was loaded from.</summary>
    public string FilePath { get; }

    /// <summary>Gets the sanitized PostScript name, safe to use directly as a PDF name token.</summary>
    public string PostScriptName { get; private set; }

    /// <summary>Gets the design units per em (the divisor for every metric below).</summary>
    public int UnitsPerEm { get; private set; }

    /// <summary>Gets the number of glyphs in the font, i.e. the exclusive upper bound for a glyph id.</summary>
    public int GlyphCount { get; }

    /// <summary>Gets the typographic ascent in font units.</summary>
    public int Ascent { get; private set; }

    /// <summary>Gets the typographic descent in font units (negative below the baseline).</summary>
    public int Descent { get; private set; }

    /// <summary>Gets the capital height in font units (estimated when the font does not declare one).</summary>
    public int CapHeight { get; private set; }

    /// <summary>Gets the italic angle in degrees, counter-clockwise from vertical (0 for upright fonts).</summary>
    public double ItalicAngle { get; private set; }

    /// <summary>Gets an estimated vertical stem width in 1/1000 em, derived from the OS/2 weight class.</summary>
    public int StemV { get; private set; }

    /// <summary>Gets the PDF font descriptor flags (always symbolic — the font is used through Identity-H).</summary>
    public int Flags { get; private set; }

    /// <summary>Gets the font bounding box in font units.</summary>
    public (int MinX, int MinY, int MaxX, int MaxY) BoundingBox { get; private set; }

    /// <summary>Converts a value in font units to the 1/1000 em unit PDF font dictionaries use.</summary>
    public int ToThousandths(int fontUnits) =>
        UnitsPerEm <= 0 ? fontUnits : (int)Math.Round(fontUnits * 1000.0 / UnitsPerEm, MidpointRounding.AwayFromZero);

    /// <summary>Looks up the glyph that renders <paramref name="codePoint"/>; false when the font has none.</summary>
    public bool TryGetGlyphId(int codePoint, out ushort glyphId)
    {
        if (_cmap.TryGetValue(codePoint, out glyphId) && glyphId != 0 && glyphId < GlyphCount)
        {
            return true;
        }

        glyphId = 0;
        return false;
    }

    /// <summary>Gets the advance width of a glyph in font units (0 for an out-of-range id).</summary>
    public int GetAdvanceWidth(ushort glyphId) => glyphId < _advances.Length ? _advances[glyphId] : 0;

    /// <summary>
    /// Loads a font file, returning <see langword="null"/> when it is missing, unreadable, too large, or
    /// not a <c>glyf</c>-flavoured sfnt this class can subset.
    /// </summary>
    public static TrueTypeFont? TryLoad(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 12 || info.Length > MaxFontFileBytes)
            {
                return null;
            }

            var data = File.ReadAllBytes(info.FullName);
            return Parse(data, info.FullName);
        }
        catch (Exception ex) when (IsExpectedFontFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Counts how many of <paramref name="codePoints"/> the font at <paramref name="path"/> can render,
    /// reading only its table directory and <c>cmap</c> — cheap enough to run over a list of candidate
    /// system fonts before committing to reading a 20 MB CJK font in full. Returns -1 when the file is
    /// not a usable <c>glyf</c> font.
    /// </summary>
    public static int CountCoverage(string path, IReadOnlyCollection<int> codePoints)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 12 || info.Length > MaxFontFileBytes)
            {
                return -1;
            }

            using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = info.Length;
            RangeReader read = (offset, destination) =>
            {
                if (offset < 0 || offset + (long)destination.Length > length)
                {
                    throw new InvalidDataException("Font table read out of range.");
                }

                stream.Position = offset;
                stream.ReadExactly(destination);
            };

            var tables = ReadTableDirectory(read, length);
            if (tables is null || !tables.TryGetValue("cmap", out var cmapTable))
            {
                return -1;
            }

            foreach (var required in OutlineTables)
            {
                if (!tables.ContainsKey(required))
                {
                    return -1;
                }
            }

            var cmapBytes = new byte[cmapTable.Length];
            read(cmapTable.Offset, cmapBytes);

            var cmap = ParseCmap(cmapBytes, 0, cmapBytes.Length);
            if (cmap is null)
            {
                return -1;
            }

            int covered = 0;
            foreach (int cp in codePoints)
            {
                if (cmap.TryGetValue(cp, out ushort gid) && gid != 0)
                {
                    covered++;
                }
            }

            return covered;
        }
        catch (Exception ex) when (IsExpectedFontFailure(ex))
        {
            return -1;
        }
    }

    /// <summary>
    /// Parses an in-memory font. Exposed for tests, which round-trip a subset back through the parser.
    /// </summary>
    /// <param name="data">The complete sfnt file.</param>
    /// <param name="filePath">Path the bytes came from, used only for diagnostics.</param>
    /// <param name="requireCmap">
    /// When <c>true</c> (the default) a font without a usable <c>cmap</c> is rejected, which is what
    /// font <i>discovery</i> needs — coverage is probed through the character map. Pass <c>false</c> to
    /// accept a font that deliberately has none, as a subset produced by
    /// <see cref="CreateSubset(IEnumerable{ushort})"/> does.
    /// </param>
    public static TrueTypeFont? TryParse(byte[] data, string filePath = "", bool requireCmap = true)
    {
        try
        {
            return Parse(data, filePath, requireCmap);
        }
        catch (Exception ex) when (IsExpectedFontFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a subset font containing only <paramref name="glyphIds"/> (plus <c>.notdef</c> and any
    /// components they reference), with the original glyph numbering preserved.
    /// </summary>
    public byte[] CreateSubset(IEnumerable<ushort> glyphIds)
    {
        ArgumentNullException.ThrowIfNull(glyphIds);

        var keep = new HashSet<ushort> { 0 };
        foreach (var gid in glyphIds)
        {
            if (gid < GlyphCount)
            {
                keep.Add(gid);
            }
        }

        // Composite glyphs reference other glyphs; those have to survive too. A queue (rather than
        // recursion) plus the "only enqueue newly added ids" rule makes cyclic references harmless.
        var pending = new Queue<ushort>(keep);
        while (pending.Count > 0)
        {
            ushort gid = pending.Dequeue();
            foreach (ushort component in GetComponents(gid))
            {
                if (component < GlyphCount && keep.Add(component))
                {
                    pending.Enqueue(component);
                }
            }
        }

        // glyf + loca (always written in the long loca format, so offsets never need to be halved).
        using var glyf = new MemoryStream();
        var loca = new uint[GlyphCount + 1];
        for (int gid = 0; gid < GlyphCount; gid++)
        {
            loca[gid] = (uint)glyf.Length;
            if (!keep.Contains((ushort)gid))
            {
                continue;
            }

            int start = _loca[gid];
            int end = _loca[gid + 1];
            if (end > start)
            {
                glyf.Write(_data, _glyf.Offset + start, end - start);
                while (glyf.Length % 4 != 0)
                {
                    glyf.WriteByte(0);
                }
            }
        }

        loca[GlyphCount] = (uint)glyf.Length;

        var locaBytes = new byte[loca.Length * 4];
        for (int i = 0; i < loca.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(locaBytes.AsSpan(i * 4), loca[i]);
        }

        var output = new List<(string Tag, byte[] Data)>
        {
            ("glyf", glyf.ToArray()),
            ("head", BuildSubsetHead()),
            ("hhea", CopyTable("hhea")),
            ("hmtx", BuildSubsetHmtx()),
            ("loca", locaBytes),
            ("maxp", CopyTable("maxp")),
        };

        foreach (var tag in HintingTables)
        {
            if (_tables.ContainsKey(tag))
            {
                output.Add((tag, CopyTable(tag)));
            }
        }

        return Assemble(output);
    }

    /// <summary>Serializes a set of tables as a complete sfnt file, with correct checksums.</summary>
    private static byte[] Assemble(List<(string Tag, byte[] Data)> tables)
    {
        tables.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));

        int count = tables.Count;
        int power = 1;
        int selector = 0;
        while (power * 2 <= count)
        {
            power *= 2;
            selector++;
        }

        int searchRange = power * 16;
        int headerLength = 12 + (count * 16);
        int total = headerLength;
        var offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            offsets[i] = total;
            total += (tables[i].Data.Length + 3) & ~3;
        }

        var file = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(0), TagTrueType);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(8), (ushort)selector);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(10), (ushort)((count * 16) - searchRange));

        int headOffset = -1;
        for (int i = 0; i < count; i++)
        {
            var (tag, data) = tables[i];
            int record = 12 + (i * 16);
            Encoding.ASCII.GetBytes(tag, file.AsSpan(record, 4));
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(record + 4), Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(record + 8), (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(record + 12), (uint)data.Length);
            data.CopyTo(file.AsSpan(offsets[i]));
            if (tag == "head")
            {
                headOffset = offsets[i];
            }
        }

        // head.checkSumAdjustment is defined in terms of the checksum of the finished file.
        if (headOffset >= 0 && headOffset + 12 <= file.Length)
        {
            uint adjustment = unchecked(0xB1B0AFBAu - Checksum(file));
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(headOffset + 8), adjustment);
        }

        return file;
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i)));
        }

        if (i < data.Length)
        {
            uint tail = 0;
            for (int k = 0; k < 4; k++)
            {
                tail <<= 8;
                if (i + k < data.Length)
                {
                    tail |= data[i + k];
                }
            }

            sum = unchecked(sum + tail);
        }

        return sum;
    }

    private byte[] CopyTable(string tag)
    {
        var record = _tables[tag];
        return _data.AsSpan(record.Offset, record.Length).ToArray();
    }

    private byte[] BuildSubsetHead()
    {
        var head = CopyTable("head");
        if (head.Length >= 54)
        {
            BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 0);   // checkSumAdjustment, fixed up later
            BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);   // indexToLocFormat = long
        }

        return head;
    }

    private byte[] BuildSubsetHmtx()
    {
        // hmtx must stay in step with hhea.numberOfHMetrics and maxp.numGlyphs, both copied verbatim.
        int required = (_numberOfHMetrics * 4) + ((GlyphCount - _numberOfHMetrics) * 2);
        var source = _tables["hmtx"];
        var hmtx = new byte[Math.Max(required, 4)];
        int copy = Math.Min(source.Length, hmtx.Length);
        _data.AsSpan(source.Offset, copy).CopyTo(hmtx);
        return hmtx;
    }

    /// <summary>Gets the glyph ids referenced by a composite glyph (empty for a simple glyph).</summary>
    private List<ushort> GetComponents(ushort glyphId)
    {
        var components = new List<ushort>();
        if (glyphId >= GlyphCount)
        {
            return components;
        }

        int start = _glyf.Offset + _loca[glyphId];
        int end = _glyf.Offset + _loca[glyphId + 1];
        if (end - start < 10 || end > _data.Length)
        {
            return components;
        }

        if (I16(_data, start) >= 0)
        {
            return components; // simple glyph
        }

        int p = start + 10;
        while (p + 4 <= end)
        {
            ushort flags = U16(_data, p);
            components.Add(U16(_data, p + 2));
            p += 4;
            p += (flags & 0x0001) != 0 ? 4 : 2;              // ARG_1_AND_2_ARE_WORDS
            if ((flags & 0x0008) != 0) p += 2;               // WE_HAVE_A_SCALE
            else if ((flags & 0x0040) != 0) p += 4;          // WE_HAVE_AN_X_AND_Y_SCALE
            else if ((flags & 0x0080) != 0) p += 8;          // WE_HAVE_A_TWO_BY_TWO
            if ((flags & 0x0020) == 0) break;                // MORE_COMPONENTS
        }

        return components;
    }

    private static TrueTypeFont? Parse(byte[] data, string filePath, bool requireCmap = true)
    {
        RangeReader read = (offset, destination) =>
        {
            if (offset < 0 || offset + (long)destination.Length > data.Length)
            {
                throw new InvalidDataException("Font table read out of range.");
            }

            data.AsSpan(offset, destination.Length).CopyTo(destination);
        };

        var tables = ReadTableDirectory(read, data.Length);
        if (tables is null)
        {
            return null;
        }

        foreach (var required in OutlineTables)
        {
            if (!tables.ContainsKey(required))
            {
                return null;
            }
        }

        // A CIDFontType2 subset carries no 'cmap': the PDF supplies char->glyph itself through
        // Identity-H plus /CIDToGIDMap /Identity, so CreateSubset drops the table. Font *discovery*
        // still needs it — that is how coverage is probed — hence a flag rather than dropping the
        // requirement outright.
        bool hasCmap = tables.TryGetValue("cmap", out var cmapTable);
        if (requireCmap && !hasCmap)
        {
            return null;
        }

        var head = tables["head"];
        var maxp = tables["maxp"];
        var hhea = tables["hhea"];
        var hmtx = tables["hmtx"];
        var locaTable = tables["loca"];
        var glyf = tables["glyf"];

        if (head.Length < 54 || maxp.Length < 6 || hhea.Length < 36)
        {
            return null;
        }

        int unitsPerEm = U16(data, head.Offset + 18);
        if (unitsPerEm is < 16 or > 16384)
        {
            return null;
        }

        short indexToLocFormat = I16(data, head.Offset + 50);
        int numGlyphs = U16(data, maxp.Offset + 4);
        if (numGlyphs is <= 0 or > 65535)
        {
            return null;
        }

        var loca = new int[numGlyphs + 1];
        if (indexToLocFormat == 0)
        {
            if (locaTable.Length < (numGlyphs + 1) * 2)
            {
                return null;
            }

            for (int i = 0; i <= numGlyphs; i++)
            {
                loca[i] = U16(data, locaTable.Offset + (i * 2)) * 2;
            }
        }
        else
        {
            if (locaTable.Length < (numGlyphs + 1) * 4)
            {
                return null;
            }

            for (int i = 0; i <= numGlyphs; i++)
            {
                uint value = U32(data, locaTable.Offset + (i * 4));
                if (value > int.MaxValue)
                {
                    return null;
                }

                loca[i] = (int)value;
            }
        }

        // Normalize: a glyph whose range is inverted or runs past the table is treated as empty.
        for (int i = 0; i <= numGlyphs; i++)
        {
            if (loca[i] > glyf.Length)
            {
                loca[i] = glyf.Length;
            }
        }

        for (int i = 1; i <= numGlyphs; i++)
        {
            if (loca[i] < loca[i - 1])
            {
                loca[i] = loca[i - 1];
            }
        }

        int numberOfHMetrics = U16(data, hhea.Offset + 34);
        if (numberOfHMetrics <= 0 || numberOfHMetrics > numGlyphs)
        {
            numberOfHMetrics = Math.Clamp(numberOfHMetrics, 1, numGlyphs);
        }

        var advances = new ushort[numGlyphs];
        ushort last = 0;
        for (int gid = 0; gid < numGlyphs; gid++)
        {
            if (gid < numberOfHMetrics)
            {
                int at = hmtx.Offset + (gid * 4);
                last = at + 2 <= hmtx.Offset + hmtx.Length ? U16(data, at) : last;
            }

            advances[gid] = last;
        }

        var cmap = hasCmap ? ParseCmap(data, cmapTable.Offset, cmapTable.Length) : new Dictionary<int, ushort>();
        if (cmap is null || cmap.Count == 0)
        {
            if (requireCmap)
            {
                return null;
            }

            cmap = new Dictionary<int, ushort>();
        }

        var font = new TrueTypeFont(
            data,
            filePath,
            tables.ToFrozenDictionary(StringComparer.Ordinal),
            cmap,
            loca,
            advances,
            glyf,
            numberOfHMetrics)
        {
            UnitsPerEm = unitsPerEm,
        };

        font.ReadDescriptorMetrics(tables, head, hhea);
        font.PostScriptName = ReadPostScriptName(data, tables, filePath);
        return font;
    }

    private void ReadDescriptorMetrics(
        Dictionary<string, TableRecord> tables,
        TableRecord head,
        TableRecord hhea)
    {
        BoundingBox = (
            I16(_data, head.Offset + 36),
            I16(_data, head.Offset + 38),
            I16(_data, head.Offset + 40),
            I16(_data, head.Offset + 42));

        ushort macStyle = U16(_data, head.Offset + 44);
        Ascent = I16(_data, hhea.Offset + 4);
        Descent = I16(_data, hhea.Offset + 6);

        int weightClass = 400;
        bool italic = (macStyle & 0x0002) != 0;
        CapHeight = 0;

        if (tables.TryGetValue("OS/2", out var os2) && os2.Length >= 78)
        {
            int version = U16(_data, os2.Offset);
            weightClass = U16(_data, os2.Offset + 4);
            short typoAscender = I16(_data, os2.Offset + 68);
            short typoDescender = I16(_data, os2.Offset + 70);
            if (typoAscender > 0)
            {
                Ascent = typoAscender;
                Descent = typoDescender;
            }

            if ((U16(_data, os2.Offset + 62) & 0x0001) != 0)
            {
                italic = true;
            }

            if (version >= 2 && os2.Length >= 90)
            {
                CapHeight = I16(_data, os2.Offset + 88);
            }
        }

        if (Ascent <= 0)
        {
            Ascent = (int)(UnitsPerEm * 0.8);
        }

        if (Descent >= 0)
        {
            Descent = -(int)(UnitsPerEm * 0.2);
        }

        if (CapHeight <= 0)
        {
            CapHeight = (int)(UnitsPerEm * 0.7);
        }

        bool fixedPitch = false;
        if (tables.TryGetValue("post", out var post) && post.Length >= 32)
        {
            ItalicAngle = I32(_data, post.Offset + 4) / 65536.0;
            fixedPitch = U32(_data, post.Offset + 12) != 0;
        }
        else if (italic)
        {
            ItalicAngle = -12;
        }

        if (weightClass is < 1 or > 1000)
        {
            weightClass = 400;
        }

        StemV = (int)Math.Clamp(Math.Round(50 + Math.Pow(weightClass / 65.0, 2)), 50, 250);

        // Symbolic: the font is addressed through Identity-H glyph ids, not a standard PDF encoding.
        Flags = 4 | (fixedPitch ? 1 : 0) | (italic ? 64 : 0);
    }

    private static string ReadPostScriptName(byte[] data, Dictionary<string, TableRecord> tables, string filePath)
    {
        string? name = null;
        if (tables.TryGetValue("name", out var table) && table.Length >= 6)
        {
            int count = U16(data, table.Offset + 2);
            int storage = table.Offset + U16(data, table.Offset + 4);
            int best = -1;
            for (int i = 0; i < count; i++)
            {
                int record = table.Offset + 6 + (i * 12);
                if (record + 12 > table.Offset + table.Length)
                {
                    break;
                }

                int platform = U16(data, record);
                if (U16(data, record + 6) != 6)
                {
                    continue; // nameID 6 == PostScript name
                }

                int length = U16(data, record + 8);
                int offset = storage + U16(data, record + 10);
                if (length <= 0 || offset + length > data.Length)
                {
                    continue;
                }

                int score = platform == 3 ? 2 : 1;
                if (score > best)
                {
                    best = score;
                    var raw = data.AsSpan(offset, length);
                    name = platform == 3 ? DecodeUtf16Be(raw) : Encoding.ASCII.GetString(raw);
                }
            }
        }

        name ??= Path.GetFileNameWithoutExtension(filePath);
        return SanitizeName(name);
    }

    private static string DecodeUtf16Be(ReadOnlySpan<byte> raw)
    {
        var sb = new StringBuilder(raw.Length / 2);
        for (int i = 0; i + 1 < raw.Length; i += 2)
        {
            sb.Append((char)((raw[i] << 8) | raw[i + 1]));
        }

        return sb.ToString();
    }

    /// <summary>Reduces a font name to characters that are legal, unescaped, inside a PDF name token.</summary>
    private static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "EmbeddedFont";
        }

        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '+' or '_')
            {
                sb.Append(c);
            }

            if (sb.Length >= 60)
            {
                break;
            }
        }

        return sb.Length == 0 ? "EmbeddedFont" : sb.ToString();
    }

    private static Dictionary<string, TableRecord>? ReadTableDirectory(RangeReader read, long fileLength)
    {
        Span<byte> header = stackalloc byte[12];
        read(0, header);
        uint tag = BinaryPrimitives.ReadUInt32BigEndian(header);

        int sfnt = 0;
        if (tag == TagTtcf)
        {
            Span<byte> firstFace = stackalloc byte[4];
            read(12, firstFace);
            long offset = BinaryPrimitives.ReadUInt32BigEndian(firstFace);
            if (offset <= 0 || offset + 12 > fileLength)
            {
                return null;
            }

            sfnt = (int)offset;
            read(sfnt, header);
            tag = BinaryPrimitives.ReadUInt32BigEndian(header);
        }

        if (tag is not (TagTrueType or TagTrue))
        {
            return null; // 'OTTO' (CFF outlines) and anything else cannot go into /FontFile2
        }

        int numTables = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(4));
        if (numTables is <= 0 or > 512)
        {
            return null;
        }

        var directory = new byte[numTables * 16];
        read(sfnt + 12, directory);

        var tables = new Dictionary<string, TableRecord>(numTables, StringComparer.Ordinal);
        for (int i = 0; i < numTables; i++)
        {
            var entry = directory.AsSpan(i * 16, 16);
            string name = Encoding.ASCII.GetString(entry.Slice(0, 4));
            long offset = BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(8));
            long length = BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(12));
            if (offset < 0 || length < 0 || offset + length > fileLength || length > int.MaxValue)
            {
                continue;
            }

            tables[name] = new TableRecord((int)offset, (int)length);
        }

        return tables;
    }

    private static Dictionary<int, ushort>? ParseCmap(byte[] data, int offset, int length)
    {
        if (length < 4)
        {
            return null;
        }

        int numTables = U16(data, offset + 2);
        int bestOffset = -1;
        int bestScore = -1;
        bool bestIsSymbol = false;

        for (int i = 0; i < numTables; i++)
        {
            int record = offset + 4 + (i * 8);
            if (record + 8 > offset + length)
            {
                break;
            }

            int platform = U16(data, record);
            int encoding = U16(data, record + 2);
            long subtable = offset + (long)U32(data, record + 4);
            if (subtable + 4 > offset + length || subtable > int.MaxValue)
            {
                continue;
            }

            int score = (platform, encoding) switch
            {
                (3, 10) => 100,     // Windows, UCS-4
                (0, 4) or (0, 6) => 95,
                (3, 1) => 90,       // Windows, BMP
                (0, _) => 85,       // Unicode
                (3, 0) => 50,       // Windows, symbol
                (1, 0) => 40,       // Macintosh Roman
                _ => 10,
            };

            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = (int)subtable;
                bestIsSymbol = platform == 3 && encoding == 0;
            }
        }

        if (bestOffset < 0)
        {
            return null;
        }

        var map = ParseCmapSubtable(data, bestOffset, offset + length);
        if (map is null || !bestIsSymbol)
        {
            return map;
        }

        // Symbol subtables live in the 0xF000 private-use block; expose the low byte as well.
        foreach (var (codePoint, glyph) in map.ToArray())
        {
            if (codePoint is >= 0xF000 and <= 0xF0FF)
            {
                map.TryAdd(codePoint & 0xFF, glyph);
            }
        }

        return map;
    }

    private static Dictionary<int, ushort>? ParseCmapSubtable(byte[] data, int offset, int limit)
    {
        var map = new Dictionary<int, ushort>();
        int format = U16(data, offset);

        switch (format)
        {
            case 0:
            {
                for (int c = 0; c < 256; c++)
                {
                    int at = offset + 6 + c;
                    if (at >= limit || at >= data.Length)
                    {
                        break;
                    }

                    if (data[at] != 0)
                    {
                        map[c] = data[at];
                    }
                }

                break;
            }

            case 4:
            {
                int segCountX2 = U16(data, offset + 6);
                if (segCountX2 < 2 || segCountX2 % 2 != 0)
                {
                    return null;
                }

                int segCount = segCountX2 / 2;
                int endCodes = offset + 14;
                int startCodes = endCodes + segCountX2 + 2;
                int deltas = startCodes + segCountX2;
                int rangeOffsets = deltas + segCountX2;
                if (rangeOffsets + segCountX2 > limit)
                {
                    return null;
                }

                for (int seg = 0; seg < segCount; seg++)
                {
                    int end = U16(data, endCodes + (seg * 2));
                    int start = U16(data, startCodes + (seg * 2));
                    short delta = I16(data, deltas + (seg * 2));
                    int rangeOffset = U16(data, rangeOffsets + (seg * 2));
                    if (start > end)
                    {
                        continue;
                    }

                    for (int c = start; c <= end && c <= 0xFFFF; c++)
                    {
                        if (map.Count >= MaxCmapEntries)
                        {
                            break;
                        }

                        if (c == 0xFFFF)
                        {
                            continue;
                        }

                        ushort glyph;
                        if (rangeOffset == 0)
                        {
                            glyph = (ushort)((c + delta) & 0xFFFF);
                        }
                        else
                        {
                            int at = rangeOffsets + (seg * 2) + rangeOffset + ((c - start) * 2);
                            if (at + 2 > limit || at + 2 > data.Length)
                            {
                                continue;
                            }

                            glyph = U16(data, at);
                            if (glyph != 0)
                            {
                                glyph = (ushort)((glyph + delta) & 0xFFFF);
                            }
                        }

                        if (glyph != 0)
                        {
                            map[c] = glyph;
                        }
                    }
                }

                break;
            }

            case 6:
            {
                int first = U16(data, offset + 6);
                int count = U16(data, offset + 8);
                for (int i = 0; i < count; i++)
                {
                    int at = offset + 10 + (i * 2);
                    if (at + 2 > limit)
                    {
                        break;
                    }

                    ushort glyph = U16(data, at);
                    if (glyph != 0)
                    {
                        map[first + i] = glyph;
                    }
                }

                break;
            }

            case 12:
            {
                long groups = U32(data, offset + 12);
                for (long g = 0; g < groups; g++)
                {
                    long at = offset + 16 + (g * 12);
                    if (at + 12 > limit || at + 12 > data.Length)
                    {
                        break;
                    }

                    long startChar = U32(data, (int)at);
                    long endChar = U32(data, (int)at + 4);
                    long startGlyph = U32(data, (int)at + 8);
                    if (endChar < startChar || endChar > 0x10FFFF)
                    {
                        continue;
                    }

                    for (long c = startChar; c <= endChar; c++)
                    {
                        if (map.Count >= MaxCmapEntries)
                        {
                            break;
                        }

                        long glyph = startGlyph + (c - startChar);
                        if (glyph is > 0 and <= 0xFFFF)
                        {
                            map[(int)c] = (ushort)glyph;
                        }
                    }
                }

                break;
            }

            default:
                return null;
        }

        return map.Count == 0 ? null : map;
    }

    private static bool IsExpectedFontFailure(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException
            or OverflowException
            or IndexOutOfRangeException
            or EndOfStreamException;

    private static ushort U16(byte[] data, int offset)
    {
        if (offset < 0 || offset + 2 > data.Length)
        {
            throw new InvalidDataException("Font read out of range.");
        }

        return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
    }

    private static short I16(byte[] data, int offset)
    {
        if (offset < 0 || offset + 2 > data.Length)
        {
            throw new InvalidDataException("Font read out of range.");
        }

        return BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset));
    }

    private static uint U32(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length)
        {
            throw new InvalidDataException("Font read out of range.");
        }

        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
    }

    private static int I32(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length)
        {
            throw new InvalidDataException("Font read out of range.");
        }

        return BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
    }
}

/// <summary>
/// Finds a system font able to render a given set of characters, so a searchable PDF can embed a real
/// Unicode text layer without the package itself shipping (tens of megabytes of) font data.
/// </summary>
/// <remarks>
/// <para>Directories searched, in order:</para>
/// <list type="bullet">
///   <item><description>Windows: <c>%WINDIR%\Fonts</c> and the per-user
///   <c>%LOCALAPPDATA%\Microsoft\Windows\Fonts</c>.</description></item>
///   <item><description>Linux: <c>/usr/share/fonts</c>, <c>/usr/local/share/fonts</c>,
///   <c>~/.local/share/fonts</c> and <c>~/.fonts</c> (recursively).</description></item>
///   <item><description>macOS: <c>/System/Library/Fonts</c>, <c>/System/Library/Fonts/Supplemental</c>,
///   <c>/Library/Fonts</c> and <c>~/Library/Fonts</c>.</description></item>
/// </list>
/// <para>
/// Within those directories a per-platform priority list of well-known file names is probed, ordered
/// "smallest font that could plausibly do the job first": broad Latin/Greek/Cyrillic/Arabic/Hebrew faces,
/// then Indic and Thai, then the large CJK faces, then the universal fallbacks. Each candidate is scored
/// by how many of the required characters its <c>cmap</c> actually contains, and the first candidate that
/// covers <em>all</em> of them wins — so a Greek document embeds a small Arial-sized font while a Chinese
/// document reaches the CJK entry further down the list. If nothing covers everything, the best-covering
/// candidate is used and the remaining characters are dropped from the (invisible) text layer. If none of
/// the candidates covers anything, the caller falls back to the standard base-14 font.
/// </para>
/// <para>The directory listing is captured once per process, so fonts installed while the process is
/// running are not picked up.</para>
/// </remarks>
internal static class SystemFontProbe
{
    /// <summary>Upper bound on how many candidate files are opened for a single lookup.</summary>
    private const int MaxProbes = 40;

    /// <summary>Upper bound on how many files the directory index holds.</summary>
    private const int MaxIndexedFiles = 8192;

    private static readonly Lazy<FrozenDictionary<string, string>> Index =
        new(BuildIndex, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Gets the platform font directories that are searched, in probe order.</summary>
    public static IReadOnlyList<string> FontDirectories
    {
        get
        {
            var dirs = new List<string>(4);

            void Add(string? path)
            {
                if (!string.IsNullOrWhiteSpace(path) && !dirs.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    dirs.Add(path);
                }
            }

            if (OperatingSystem.IsWindows())
            {
                Add(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));
                string? windir = Environment.GetEnvironmentVariable("WINDIR");
                Add(string.IsNullOrWhiteSpace(windir) ? null : Path.Combine(windir, "Fonts"));
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                Add(string.IsNullOrWhiteSpace(local) ? null : Path.Combine(local, "Microsoft", "Windows", "Fonts"));
            }
            else if (OperatingSystem.IsMacOS())
            {
                Add("/System/Library/Fonts");
                Add("/System/Library/Fonts/Supplemental");
                Add("/Library/Fonts");
                Add(HomeRelative("Library/Fonts"));
            }
            else
            {
                Add("/usr/share/fonts");
                Add("/usr/local/share/fonts");
                Add(HomeRelative(".local/share/fonts"));
                Add(HomeRelative(".fonts"));
            }

            return dirs;
        }
    }

    /// <summary>
    /// Resolves a font able to render <paramref name="requiredCodePoints"/>, honouring an explicit
    /// <paramref name="explicitFontPath"/> first. Returns <see langword="null"/> when nothing usable is
    /// installed, which is the caller's signal to keep the standard base-14 text layer.
    /// </summary>
    public static TrueTypeFont? Resolve(string? explicitFontPath, IReadOnlyCollection<int> requiredCodePoints)
    {
        ArgumentNullException.ThrowIfNull(requiredCodePoints);

        if (!string.IsNullOrWhiteSpace(explicitFontPath))
        {
            // An explicitly configured font is used as-is: the caller knows what their document needs.
            var chosen = TrueTypeFont.TryLoad(explicitFontPath);
            if (chosen is not null)
            {
                return chosen;
            }
        }

        if (requiredCodePoints.Count == 0)
        {
            return null;
        }

        string? bestPath = null;
        int bestCoverage = 0;
        int probes = 0;

        foreach (var candidate in EnumerateCandidates())
        {
            if (probes++ >= MaxProbes)
            {
                break;
            }

            int coverage = TrueTypeFont.CountCoverage(candidate, requiredCodePoints);
            if (coverage <= 0)
            {
                continue;
            }

            if (coverage >= requiredCodePoints.Count)
            {
                var complete = TrueTypeFont.TryLoad(candidate);
                if (complete is not null)
                {
                    return complete;
                }

                continue;
            }

            if (coverage > bestCoverage)
            {
                bestCoverage = coverage;
                bestPath = candidate;
            }
        }

        return bestPath is null ? null : TrueTypeFont.TryLoad(bestPath);
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        var index = Index.Value;
        var preferred = PreferredFileNames();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in preferred)
        {
            if (index.TryGetValue(name, out var path) && seen.Add(name))
            {
                yield return path;
            }
        }

        // Nothing well-known installed (a minimal container, say): try whatever else is there.
        foreach (var entry in index.OrderBy(static e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(entry.Key))
            {
                yield return entry.Value;
            }
        }
    }

    /// <summary>Well-known font file names, ordered smallest-plausible-font first.</summary>
    private static string[] PreferredFileNames()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                // Latin, Greek, Cyrillic, Arabic, Hebrew — small files.
                "arial.ttf", "segoeui.ttf", "tahoma.ttf", "micross.ttf", "calibri.ttf", "verdana.ttf",
                // Indic and Thai.
                "nirmala.ttf", "mangal.ttf", "leelawui.ttf", "leelawad.ttf",
                // CJK — tens of megabytes, hence last but one.
                "msyh.ttc", "msyh.ttf", "msjh.ttc", "meiryo.ttc", "msgothic.ttc", "yugothm.ttc",
                "malgun.ttf", "simsun.ttc", "mingliu.ttc", "gulim.ttc", "batang.ttc", "msmincho.ttc",
                // Universal fallback.
                "arialuni.ttf",
            ];
        }

        if (OperatingSystem.IsMacOS())
        {
            return
            [
                "Arial.ttf", "Helvetica.ttc", "Verdana.ttf", "Tahoma.ttf", "Geneva.ttf",
                "DevanagariSangamMN.ttc", "Kohinoor.ttc", "Thonburi.ttc",
                "AppleGothic.ttf", "AppleMyungjo.ttf", "HiraginoSansGB.ttc", "PingFang.ttc",
                "Arial Unicode.ttf", "ArialUnicode.ttf",
            ];
        }

        return
        [
            "DejaVuSans.ttf", "NotoSans-Regular.ttf", "LiberationSans-Regular.ttf", "FreeSans.ttf",
            "NotoSansDevanagari-Regular.ttf", "NotoSansThai-Regular.ttf", "NotoNaskhArabic-Regular.ttf",
            "NotoSansHebrew-Regular.ttf",
            "NotoSansCJK-Regular.ttc", "NotoSansCJKsc-Regular.ttc", "DroidSansFallbackFull.ttf",
            "DroidSansFallback.ttf", "wqy-zenhei.ttc", "wqy-microhei.ttc",
            "unifont.ttf",
        ];
    }

    private static FrozenDictionary<string, string> BuildIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MaxRecursionDepth = 6,
        };

        foreach (var directory in FontDirectories)
        {
            if (index.Count >= MaxIndexedFiles)
            {
                break;
            }

            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(directory, "*", options))
                {
                    if (index.Count >= MaxIndexedFiles)
                    {
                        break;
                    }

                    var extension = Path.GetExtension(file.AsSpan());
                    if (!extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    index.TryAdd(Path.GetFileName(file), file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A font directory we cannot read is simply not a source of candidates.
            }
        }

        return index.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static string? HomeRelative(string relative)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
