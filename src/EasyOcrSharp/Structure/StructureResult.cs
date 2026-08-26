using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyOcrSharp.Structure;

/// <summary>
/// The result of a full document-structure analysis: the document's <see cref="Blocks"/> in reading order
/// together with the source image dimensions (<see cref="SourceWidth"/> / <see cref="SourceHeight"/>) the
/// block bounds are expressed in. Provides two exporters, <see cref="ToMarkdown"/> and <see cref="ToJson"/>,
/// for serializing the analyzed layout.
/// </summary>
public sealed class StructureResult
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
    /// An empty structure result.
    /// </summary>
    public static StructureResult Empty { get; } = new() { Blocks = Array.Empty<StructureBlock>() };

    /// <summary>
    /// Renders the analyzed document to Markdown, walking <see cref="Blocks"/> in reading order: doc/section
    /// titles become ATX headings (<c># / ## / ###</c>), text-like blocks become paragraphs, lists keep
    /// their text, tables are emitted as their recovered HTML (Markdown permits raw HTML), formulas as
    /// <c>$$…$$</c> LaTeX blocks, and figures/charts/seals as a placeholder line. Blocks with no renderable
    /// content are skipped.
    /// </summary>
    /// <returns>The Markdown representation of the document.</returns>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();

        // Walk in reading order. Blocks usually arrive pre-sorted by Order, but sort defensively so the
        // exporter is correct even if a caller hands us an unordered list.
        foreach (var block in OrderedBlocks())
        {
            string chunk = RenderMarkdown(block);
            if (chunk.Length == 0) continue;

            sb.Append(chunk);
            // Blank line between blocks so paragraphs/headings/tables stay separate Markdown elements.
            sb.Append("\n\n");
        }

        // Trim the trailing blank line we always append between blocks.
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Renders a single block to its Markdown fragment (no trailing separator); empty when nothing to emit.
    /// </summary>
    private static string RenderMarkdown(StructureBlock block)
    {
        switch (block.Type)
        {
            case StructureBlockType.DocTitle:
                return Heading(block.Text, level: 1);

            case StructureBlockType.Title:
            case StructureBlockType.Abstract:
                return Heading(block.Text, level: 2);

            case StructureBlockType.Table:
                // Markdown allows inline HTML; emit the recovered <table> verbatim. Fall back to any
                // recovered text (e.g. GFM) if HTML is absent.
                return !string.IsNullOrWhiteSpace(block.TableHtml)
                    ? block.TableHtml!.Trim()
                    : (block.Text?.Trim() ?? string.Empty);

            case StructureBlockType.Formula:
                return string.IsNullOrWhiteSpace(block.Latex)
                    ? string.Empty
                    : $"$$\n{block.Latex!.Trim()}\n$$";

            case StructureBlockType.Figure:
            case StructureBlockType.Chart:
            case StructureBlockType.Seal:
                // No inline image bytes in the result → emit a placeholder plus any recovered caption/text.
                var label = block.Type.ToString();
                var caption = block.Text?.Trim();
                return string.IsNullOrEmpty(caption)
                    ? $"![{label}]()"
                    : $"![{label}]()\n\n{caption}";

            default:
                // Text, Paragraph, List, captions, headers/footers, references, etc.: plain paragraph text.
                return block.Text?.Trim() ?? string.Empty;
        }
    }

    /// <summary>
    /// Formats an ATX heading at <paramref name="level"/> (#, ##, …); empty when the text is blank.
    /// </summary>
    private static string Heading(string? text, int level)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return new string('#', level) + " " + text.Trim();
    }

    /// <summary>
    /// Serializes the analyzed document (blocks, bounds, recovered text/HTML/LaTeX and source dimensions)
    /// to a JSON string via the source-generated <see cref="StructureJsonContext"/>, keeping the exporter
    /// trim / Native-AOT safe. The block list is projected onto a flat DTO that captures the renderable
    /// fields (the underlying OCR lines and polygons are intentionally omitted to keep the output compact).
    /// </summary>
    /// <returns>The JSON representation of the document.</returns>
    public string ToJson()
    {
        var dto = new StructureResultDto
        {
            SourceWidth = SourceWidth,
            SourceHeight = SourceHeight,
            Blocks = OrderedBlocks().Select(StructureBlockDto.From).ToArray(),
        };
        return JsonSerializer.Serialize(dto, StructureJsonContext.Default.StructureResultDto);
    }

    /// <summary>
    /// Returns the blocks sorted by reading-order <see cref="StructureBlock.Order"/> (stable).
    /// </summary>
    private IEnumerable<StructureBlock> OrderedBlocks()
        => Blocks.OrderBy(b => b.Order);
}
