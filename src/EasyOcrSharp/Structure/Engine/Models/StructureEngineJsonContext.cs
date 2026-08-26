using System.Text.Json.Serialization;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Engine.Models;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the OCR result types, so they can be
/// (de)serialized in trimmed / Native-AOT apps with no reflection warnings. Used by the
/// <c>ToJson()</c> exporter; also usable directly:
/// <c>JsonSerializer.Serialize(result, StructureEngineJsonContext.Default.OcrResult)</c>.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(OcrResult))]
[JsonSerializable(typeof(OcrLine))]
[JsonSerializable(typeof(IReadOnlyList<OcrLine>))]
[JsonSerializable(typeof(OcrPoint))]
[JsonSerializable(typeof(OcrBoundingBox))]
[JsonSerializable(typeof(DetectedRegion))]
[JsonSerializable(typeof(IReadOnlyList<DetectedRegion>))]
internal partial class StructureEngineJsonContext : JsonSerializerContext
{
}
