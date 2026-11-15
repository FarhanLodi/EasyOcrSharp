using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyOcrSharp.Structure;

/// <summary>
/// Serializable projection of a <see cref="StructureResult"/> (blocks + source dimensions).
/// </summary>
internal sealed class StructureResultDto
{
    /// <summary>
    /// Width (px) of the source image, or 0 if unknown.
    /// </summary>
    public int SourceWidth { get; init; }

    /// <summary>
    /// Height (px) of the source image, or 0 if unknown.
    /// </summary>
    public int SourceHeight { get; init; }

    /// <summary>
    /// The analyzed blocks in reading order.
    /// </summary>
    public IReadOnlyList<StructureBlockDto> Blocks { get; init; } = Array.Empty<StructureBlockDto>();

    /// <summary>
    /// Source OCR lines no single block owns (see <see cref="StructureResult.OrphanLines"/>); omitted when
    /// there are none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<EasyOcrSharp.Models.OcrLine>? OrphanLines { get; init; }
}
