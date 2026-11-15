using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EasyOcrSharp.Models;
using EasyOcrSharp.Structure.Engine.Models;

namespace EasyOcrSharp.Structure;

/// <summary>
/// The result of a full document-structure analysis: the document's <see cref="Blocks"/> in reading order
/// together with the source image dimensions (<see cref="SourceWidth"/> / <see cref="SourceHeight"/>) the
/// block bounds are expressed in. Provides two exporters, <see cref="ToMarkdown"/> and <see cref="ToJson()"/>,
/// for serializing the analyzed layout.
/// </summary>
public sealed partial class StructureResult
{
    /// <summary>
    /// The analyzed blocks, ordered by their reading-order <see cref="StructureBlock.Order"/>.
    /// </summary>
    public required IReadOnlyList<StructureBlock> Blocks { get; init; }

    /// <summary>
    /// Width (px) of the source image the block bounds are expressed in, or 0 if unknown.
    /// </summary>
    public int SourceWidth { get; init; }

    /// <summary>
    /// Height (px) of the source image the block bounds are expressed in, or 0 if unknown.
    /// </summary>
    public int SourceHeight { get; init; }

    /// <summary>
    /// Recognized OCR lines that no single layout block owns: lines on empty ground or inside a figure or
    /// chart region, lines without a usable box, and the original reading of a line that straddles several
    /// blocks (its per-block fragments are still placed in those blocks). They are kept as evidence, in the
    /// same coordinate frame as <see cref="Blocks"/>, instead of being dropped; they are not inserted into
    /// <see cref="ToMarkdown"/> or the other exports. Empty when every line found an owner.
    /// <see cref="ToJson()"/> writes them as <c>OrphanLines</c> only when there are any.
    /// </summary>
    public IReadOnlyList<OcrLine> OrphanLines { get; init; } = Array.Empty<OcrLine>();

    /// <summary>
    /// An empty structure result.
    /// </summary>
    public static StructureResult Empty { get; } = new() { Blocks = Array.Empty<StructureBlock>() };

    /// <summary>
    /// Renders the analyzed document to Markdown, walking <see cref="Blocks"/> in reading order with
    /// Python PP-StructureV3's rendering rules (<c>MarkdownConverter.convert</c> +
    /// <c>markdown_format_funcs.py</c>):
    /// <list type="bullet">
    ///   <item>Page furniture (headers, footers, page numbers, footnotes, asides) is omitted — see
    ///   <see cref="MarkdownRenderOptions.IgnoredBlockTypes"/>.</item>
    ///   <item>The doc title becomes <c>#</c>; section titles get their heading level from a leading
    ///   <c>1.2.3</c>-style numbering prefix (<c>format_title</c>); the first line of an abstract /
    ///   reference block is promoted to a <c>##</c> heading when it is the literal word.</item>
    ///   <item>Consecutive text blocks that geometrically continue one paragraph (10&#160;px
    ///   indent/outdent tests, <c>get_seg_flag</c>) are joined <b>without</b> a blank line.</item>
    ///   <item>Text paragraphs collapse soft hyphens (<c>-\n</c>) and double their newlines; lists use
    ///   Markdown hard line breaks (<c>"  \n"</c>).</item>
    ///   <item>Tables are emitted as their recovered HTML — <c>&lt;table border="1"&gt;</c> when
    ///   <see cref="MarkdownRenderOptions.PrettyTables"/> is on; formulas as one-line <c>$$…$$</c>;
    ///   figures/charts/seals as a placeholder image plus any recovered caption/text.</item>
    /// </list>
    /// Blocks with no renderable content are skipped.
    /// </summary>
    /// <param name="options">Rendering options; <c>null</c> uses <see cref="MarkdownRenderOptions.Default"/> (Python-parity behavior).</param>
    /// <returns>The Markdown representation of the document.</returns>
    public string ToMarkdown(MarkdownRenderOptions? options = null)
        => RenderMarkdownPage(options ?? MarkdownRenderOptions.Default).Markdown;

    /// <summary>
    /// Renders the page and additionally reports its paragraph-continuation flags — whether the first
    /// rendered element starts a fresh paragraph and whether the last one ends mid-paragraph — the C#
    /// counterpart of Python's <c>page_continuation_flags</c>, consumed by
    /// <see cref="StructureMarkdownExtensions.ConcatenateMarkdownPages(IEnumerable{StructureResult}, MarkdownRenderOptions)"/>.
    /// </summary>
    internal MarkdownPageRender RenderMarkdownPage(MarkdownRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sb = new StringBuilder();
        StructureBlockType? lastRenderedType = null;
        StructureBlock? prevRendered = null;
        bool segEnd = true;
        bool? firstSegStart = null;

        // Walk in reading order. Blocks usually arrive pre-sorted by Order, but sort defensively so the
        // exporter is correct even if a caller hands us an unordered list.
        foreach (var block in OrderedBlocks())
        {
            // Continuation flags are computed for EVERY block (Python calls get_seg_flag before the
            // handler lookup), so an ignored trailing block still decides the page-end flag; only blocks
            // that actually render become the "previous block" of the geometry test.
            var flags = GetSegFlags(block, prevRendered);
            bool segStart = flags.SegStart;
            segEnd = flags.SegEnd;
            firstSegStart ??= segStart;

            // Python's handler dict has no entry for formula_number (it is only merged into the formula
            // in the VL pipeline), so the block renders nothing and does not become the previous block.
            if (block.Type == StructureBlockType.FormulaNumber) continue;
            if (options.IsIgnored(block.Type)) continue;

            string chunk = RenderMarkdown(block, options);
            prevRendered = block;
            if (chunk.Length == 0)
            {
                lastRenderedType = block.Type;
                continue;
            }

            // Consecutive text blocks that continue one paragraph concatenate directly — no blank line,
            // no space (the line assembler leaves a trailing space after Latin line ends).
            bool continuesParagraph = sb.Length > 0
                && block.Type == lastRenderedType
                && IsMergeableText(block.Type)
                && !segStart;

            if (sb.Length > 0 && !continuesParagraph) sb.Append("\n\n");
            sb.Append(chunk);
            lastRenderedType = block.Type;
        }

        return new MarkdownPageRender(sb.ToString().Trim('\n'), firstSegStart ?? true, segEnd);
    }

    /// <summary>
    /// Block types whose consecutive runs may merge into one paragraph (Python merges only
    /// <c>label == last_label == "text"</c>; equality of the two types is checked by the caller).
    /// </summary>
    private static bool IsMergeableText(StructureBlockType type)
        => type is StructureBlockType.Text or StructureBlockType.Paragraph;

    // -----------------------------------------------------------------------------------------------------
    // Paragraph-continuation geometry — the port of layout_parsing/utils.py get_seg_flag(). All
    // coordinates are page pixels; "seg start/end" are the first line's left edge and the last line's
    // right edge, tested against the block edges with a 10 px indent/outdent tolerance.
    // -----------------------------------------------------------------------------------------------------

    /// <summary>The 10 px indent/outdent tolerance used by every seg-flag test.</summary>
    private const double SegIndentTolerance = 10;

    /// <summary>
    /// Computes the (seg_start, seg_end) flags for <paramref name="block"/> given the previously rendered
    /// <paramref name="prev"/> block — <c>seg_start == false</c> means the block continues the previous
    /// paragraph, <c>seg_end == false</c> means the block ends mid-paragraph (its last line reaches the
    /// right edge). Faithful port of <c>get_seg_flag</c>.
    /// </summary>
    private static (bool SegStart, bool SegEnd) GetSegFlags(StructureBlock block, StructureBlock? prev)
    {
        bool segStart = true;
        bool segEnd = true;

        double contextLeft = block.Bounds.MinX;
        double contextRight = block.Bounds.MaxX;
        double segStartX = SegStartCoordinate(block);
        double segEndX = SegEndCoordinate(block);

        if (prev is not null)
        {
            double prevSegEndX = SegEndCoordinate(prev);
            bool prevEndSpaceSmall = Math.Abs(prev.Bounds.MaxX - prevSegEndX) < SegIndentTolerance;
            bool prevMultiLine = LineCount(prev) > 1;

            bool overlaps = contextLeft < prev.Bounds.MaxX && contextRight > prev.Bounds.MinX;
            double edgeDistance;
            if (overlaps)
            {
                // Same column: widen the context to the union of both blocks before the edge tests.
                contextLeft = Math.Min(prev.Bounds.MinX, contextLeft);
                contextRight = Math.Max(prev.Bounds.MaxX, contextRight);
                prevEndSpaceSmall = Math.Abs(contextRight - prevSegEndX) < SegIndentTolerance;
                edgeDistance = 0;
            }
            else
            {
                edgeDistance = Math.Abs(block.Bounds.MinX - prev.Bounds.MaxX);
            }

            bool currentStartSpaceSmall = segStartX - contextLeft < SegIndentTolerance;

            if (prevEndSpaceSmall
                && currentStartSpaceSmall
                && prevMultiLine
                && edgeDistance < Math.Max(prev.Bounds.Width, block.Bounds.Width))
            {
                segStart = false;
            }
        }
        else if (segStartX - contextLeft < SegIndentTolerance)
        {
            segStart = false;
        }

        if (contextRight - segEndX < SegIndentTolerance) segEnd = false;

        return (segStart, segEnd);
    }

    /// <summary>
    /// The first OCR line's left edge, or +∞ when the block has no usable line geometry (Python leaves
    /// <c>seg_start_coordinate = inf</c> on blocks whose text is never line-assembled) — which makes every
    /// indent test fail and the block start a fresh paragraph.
    /// </summary>
    private static double SegStartCoordinate(StructureBlock block)
        => UsesLineGeometry(block.Type) && block.Lines is { Count: > 0 } lines
            ? lines[0].BoundingBox.MinX
            : double.PositiveInfinity;

    /// <summary>
    /// The last OCR line's right edge, or −∞ when unavailable. Python only assigns the end coordinate on
    /// <c>line_idx == len - 1</c> of an <i>elif</i>, so single-line blocks keep −∞ (they never read as
    /// "ends mid-paragraph"); the ≥ 2 guard reproduces that.
    /// </summary>
    private static double SegEndCoordinate(StructureBlock block)
        => UsesLineGeometry(block.Type) && block.Lines is { Count: > 1 } lines
            ? lines[^1].BoundingBox.MaxX
            : double.NegativeInfinity;

    /// <summary>
    /// Number of OCR lines backing the block (1 when unknown, matching Python's default).
    /// </summary>
    private static int LineCount(StructureBlock block)
        => block.Lines is { Count: > 0 } lines ? lines.Count : 1;

    /// <summary>
    /// Whether the block's <see cref="StructureBlock.Lines"/> represent flowed text whose geometry should
    /// drive the seg flags. Vision blocks (tables, figures, charts, seals) and formulas carry lines only as
    /// raw OCR evidence — Python never line-assembles them, so their seg coordinates stay at ±∞.
    /// </summary>
    private static bool UsesLineGeometry(StructureBlockType type) => type switch
    {
        StructureBlockType.Table or StructureBlockType.Figure or StructureBlockType.Chart
            or StructureBlockType.Seal or StructureBlockType.Formula
            or StructureBlockType.FormulaNumber => false,
        _ => true,
    };

    // -----------------------------------------------------------------------------------------------------
    // Per-block rendering — the port of markdown_format_funcs.build_handle_funcs_dict() +
    // result_v2._build_handle_funcs_dict().
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Renders a single block to its Markdown fragment (no trailing separator); empty when nothing to emit.
    /// </summary>
    private static string RenderMarkdown(StructureBlock block, MarkdownRenderOptions options)
    {
        switch (block.Type)
        {
            case StructureBlockType.DocTitle:
                return string.IsNullOrWhiteSpace(block.Text)
                    ? string.Empty
                    : CollapseSoftNewlines("# " + block.Text.Trim());

            case StructureBlockType.Title:
                return FormatTitle(block.Text);

            case StructureBlockType.Abstract:
                // First-line promotion: "Abstract …" / "摘要 …" begins with a ## heading.
                return PromoteFirstLine(block.Text, AbstractTemplates, splitter: " ", trailingNewline: true);

            case StructureBlockType.Reference:
                // First-line promotion: a block starting with the literal "References" / "参考文献" line.
                return PromoteFirstLine(block.Text, ReferenceTemplates, splitter: "\n", trailingNewline: false);

            case StructureBlockType.Table:
            {
                // Markdown allows inline HTML; emit the recovered <table> fragment — the recognizer wraps
                // it in <html><body> (PaddleOCR parity), which is stripped here. Pretty mode adds
                // border="1" (Python's '<table border="1">' replacement). Fall back to any recovered text.
                var fragment = Export.TableHtmlFragment.Extract(block.TableHtml);
                if (fragment.Contains("<table", StringComparison.OrdinalIgnoreCase))
                {
                    return options.PrettyTables
                        ? fragment.Replace("<table>", "<table border=\"1\">", StringComparison.Ordinal)
                        : fragment;
                }
                return block.Text?.Trim() ?? string.Empty;
            }

            case StructureBlockType.Formula:
                // Python: f"$${block.content}$$" — a single-line display-math block.
                return string.IsNullOrWhiteSpace(block.Latex)
                    ? string.Empty
                    : "$$" + block.Latex.Trim() + "$$";

            case StructureBlockType.Figure:
            case StructureBlockType.Chart:
            case StructureBlockType.Seal:
            {
                // No inline image bytes in the result → emit a placeholder plus any recovered text: the
                // seal's recognized text, the chart's caption, a figure's OCR content. (When block crops
                // become available, MarkdownRenderOptions.EmbedImages switches this to a data: URI.)
                var label = block.Type.ToString();
                var caption = block.Text?.Trim();
                return string.IsNullOrEmpty(caption)
                    ? $"![{label}]()"
                    : $"![{label}]()\n\n{caption}";
            }

            case StructureBlockType.List:
                // Python's 'content' (table-of-contents) handler: Markdown hard line breaks.
                return (block.Text ?? string.Empty)
                    .Replace("-\n", "  \n", StringComparison.Ordinal)
                    .Replace("\n", "  \n", StringComparison.Ordinal);

            case StructureBlockType.Algorithm:
                return (block.Text ?? string.Empty).Trim('\n');

            case StructureBlockType.Text:
            case StructureBlockType.Paragraph:
                // Python's text handler: collapse soft hyphens, then double newlines so each interior
                // line break becomes a paragraph break in Markdown.
                return NormalizeParagraphText(block.Text);

            default:
                // Captions, headers/footers (when not ignored), unknown types: plain paragraph text.
                return block.Text?.Trim() ?? string.Empty;
        }
    }

    /// <summary>First-line templates promoted to a heading inside an abstract block (lower-case).</summary>
    private static readonly string[] AbstractTemplates = { "摘要", "abstract" };

    /// <summary>First-line templates promoted to a heading inside a reference block (lower-case).</summary>
    private static readonly string[] ReferenceTemplates = { "参考文献", "references" };

    /// <summary>
    /// Collapses soft-hyphen line breaks (<c>-\n</c> → <c>""</c>) and hard line breaks (<c>\n</c> →
    /// <c>" "</c>) — Python's <c>_collapse_soft_newlines</c>, applied to titles and image paths.
    /// </summary>
    private static string CollapseSoftNewlines(string s)
        => s.Replace("-\n", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>
    /// Python's <c>_format_normalize_newlines</c> with soft-hyphen collapse: <c>-\n</c> joins hyphenated
    /// words, then interior newlines are doubled so they survive as Markdown paragraph breaks.
    /// </summary>
    private static string NormalizeParagraphText(string? text)
        => (text ?? string.Empty)
            .Replace("-\n", "", StringComparison.Ordinal)
            .Replace("\n\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\n\n", StringComparison.Ordinal);

    /// <summary>
    /// Ports <c>format_title</c>: normalizes a leading <c>1.2.3</c>-style (or CJK / roman-numeral)
    /// numbering prefix, strips trailing dots, and derives the heading level from the number of dots —
    /// a plain title renders as <c>##</c>, "1.2 Overview" as <c>###</c>, and so on (one <c>#</c> more
    /// than the dot count + 1, exactly as Python emits <c>"#" + "#" * level</c>).
    /// </summary>
    private static string FormatTitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        string title = text.Trim();
        var match = TitleNumberingRegex().Match(title);
        if (match.Success)
        {
            string numbering = match.Groups[1].Value.Trim();
            string rest = match.Groups[3].Value.TrimStart();
            title = numbering + " " + rest;
        }

        title = title.TrimEnd('.');
        int level = title.Contains('.') ? title.Count(c => c == '.') + 1 : 1;
        return CollapseSoftNewlines("#" + new string('#', level) + " " + title);
    }

    /// <summary>
    /// Ports <c>format_first_line</c>: splits the content by <paramref name="splitter"/>, and when the
    /// first non-empty piece is one of the lower-case <paramref name="templates"/>, replaces it with a
    /// <c>##</c> heading (plus a newline for the space-split abstract form); everything else is unchanged.
    /// </summary>
    private static string PromoteFirstLine(string? text, string[] templates, string splitter, bool trailingNewline)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var pieces = text.Split(splitter);
        for (int i = 0; i < pieces.Length; i++)
        {
            if (pieces[i].Trim().Length == 0) continue;
            if (templates.Contains(pieces[i].ToLowerInvariant()))
                pieces[i] = "## " + pieces[i] + (trailingNewline ? "\n" : string.Empty);
            break; // only the first non-empty piece is considered, matching or not.
        }
        return string.Join(splitter, pieces);
    }

    /// <summary>
    /// The title-numbering pattern of <c>markdown_format_funcs.compile_title_pattern()</c>, verbatim:
    /// dotted arabic numbering ("1.2.3." / "1、"), parenthesized arabic/CJK numerals, bare CJK numerals,
    /// or roman numerals I–X followed by a dot or space. Group 1 = numbering, group 3 = the title text.
    /// </summary>
    [GeneratedRegex(
        @"^\s*((?:[1-9][0-9]*(?:\.[1-9][0-9]*)*[\.、]?|[\(（](?:[1-9][0-9]*|[一二三四五六七八九十百千万亿零壹贰叁肆伍陆柒捌玖拾]+)[\)）]|[一二三四五六七八九十百千万亿零壹贰叁肆伍陆柒捌玖拾]+[、\.]?|(?:I|II|III|IV|V|VI|VII|VIII|IX|X)(?:\.|\s)))(\s*)(.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TitleNumberingRegex();

    /// <summary>
    /// Serializes the analyzed document (blocks, bounds, recovered text/HTML/LaTeX and source dimensions)
    /// to a JSON string via the source-generated <see cref="StructureJsonContext"/>, keeping the exporter
    /// trim / Native-AOT safe. The block list is projected onto a flat DTO that captures the renderable
    /// fields (the underlying OCR lines and polygons are intentionally omitted to keep the output compact).
    /// Recognized text is written verbatim in every script — Cyrillic, CJK, Arabic and the rest stay
    /// readable rather than turning into <c>\uXXXX</c> escapes (see <see cref="EasyOcrJson.Encoder"/>).
    /// </summary>
    /// <returns>The JSON representation of the document.</returns>
    public string ToJson()
        => JsonSerializer.Serialize(ToDto(), StructureJsonContext.Unescaped.StructureResultDto);

    /// <summary>
    /// Serializes the analyzed document with caller-supplied <paramref name="options"/> — indentation,
    /// naming policy, a different <see cref="System.Text.Json.JsonSerializerOptions.Encoder"/>, and so on.
    /// The options are copied onto a source-generated context, so this overload is as trim / Native-AOT
    /// safe as <see cref="ToJson()"/>; only the formatting differs.
    /// </summary>
    /// <param name="options">The serializer options to apply. The instance is copied, not retained.</param>
    /// <returns>The JSON representation of the document.</returns>
    public string ToJson(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var context = new StructureJsonContext(EasyOcrJson.ForContext(options));
        return JsonSerializer.Serialize(ToDto(), context.StructureResultDto);
    }

    /// <summary>
    /// Projects the document onto its flat, serialization-friendly DTO, blocks in reading order.
    /// </summary>
    private StructureResultDto ToDto() => new()
    {
        SourceWidth = SourceWidth,
        SourceHeight = SourceHeight,
        Blocks = OrderedBlocks().Select(StructureBlockDto.From).ToArray(),
        // Omitted (null) when empty so pages whose every line has an owner serialize exactly as before.
        OrphanLines = OrphanLines is { Count: > 0 } orphans ? orphans : null,
    };

    /// <summary>
    /// Returns the blocks sorted by reading-order <see cref="StructureBlock.Order"/> (stable).
    /// </summary>
    private IEnumerable<StructureBlock> OrderedBlocks()
        => Blocks.OrderBy(b => b.Order);
}

/// <summary>
/// One page rendered to Markdown together with its paragraph-continuation flags — Python's
/// <c>page_continuation_flags</c>: whether the page's first element starts a fresh paragraph
/// (<see cref="FirstBlockSegStart"/>) and whether its last element ends one cleanly
/// (<see cref="LastBlockSegEnd"/>). Both default to <c>true</c> on empty pages.
/// </summary>
/// <param name="Markdown">The page's rendered Markdown (outer newlines trimmed).</param>
/// <param name="FirstBlockSegStart"><c>true</c> when the first block starts a new paragraph.</param>
/// <param name="LastBlockSegEnd"><c>true</c> when the last block ends its paragraph (does not run to the edge).</param>
internal readonly record struct MarkdownPageRender(string Markdown, bool FirstBlockSegStart, bool LastBlockSegEnd);
