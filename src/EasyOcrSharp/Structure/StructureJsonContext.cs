using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyOcrSharp.Structure;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the structure-export DTOs, so
/// <see cref="StructureResult.ToJson"/> stays reflection-free / trim / Native-AOT safe.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(StructureResultDto))]
[JsonSerializable(typeof(StructureBlockDto))]
[JsonSerializable(typeof(IReadOnlyList<StructureBlockDto>))]
internal partial class StructureJsonContext : JsonSerializerContext
{
}
