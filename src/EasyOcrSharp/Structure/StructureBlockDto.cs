using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyOcrSharp.Structure;

/// <summary>
/// Flat, serialization-friendly projection of a <see cref="StructureBlock"/> used by <see cref="StructureResult.ToJson"/>.
/// Carries the block's semantic type (as its name), reading-order index, axis-aligned bounds and any
/// recovered text / table-HTML / LaTeX; the heavy underlying OCR lines and polygons are dropped.
/// </summary>
internal sealed class StructureBlockDto
{
    /// <summary>
    /// The block's semantic type, as the <see cref="StructureBlockType"/> name.
    /// </summary>
    public string Type { get; init; } = nameof(StructureBlockType.Unknown);

    /// <summary>
    /// Zero-based reading-order index.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Left edge of the axis-aligned bounds (source-image px).
    /// </summary>
    public double MinX { get; init; }

    /// <summary>
    /// Top edge of the axis-aligned bounds (source-image px).
    /// </summary>
    public double MinY { get; init; }

    /// <summary>
    /// Right edge of the axis-aligned bounds (source-image px).
    /// </summary>
    public double MaxX { get; init; }

    /// <summary>
    /// Bottom edge of the axis-aligned bounds (source-image px).
    /// </summary>
    public double MaxY { get; init; }

    /// <summary>
    /// Overall block confidence (0–1).
    /// </summary>
    public float Score { get; init; }

    /// <summary>
    /// Recognized text, when applicable; omitted otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    /// <summary>
    /// Recovered table HTML, for table blocks; omitted otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TableHtml { get; init; }

    /// <summary>
    /// Recovered LaTeX, for formula blocks; omitted otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Latex { get; init; }

    /// <summary>
    /// Projects a <see cref="StructureBlock"/> onto its serializable DTO.
    /// </summary>
    public static StructureBlockDto From(StructureBlock block) => new()
    {
        Type = block.Type.ToString(),
        Order = block.Order,
        MinX = block.Bounds.MinX,
        MinY = block.Bounds.MinY,
        MaxX = block.Bounds.MaxX,
        MaxY = block.Bounds.MaxY,
        Score = block.Score,
        Text = block.Text,
        TableHtml = block.TableHtml,
        Latex = block.Latex,
    };
}
