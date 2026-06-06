using System.Text.Json.Serialization;

namespace EasyOcrSharp.Models;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the OCR result types, so they can be
/// (de)serialized in trimmed / Native-AOT apps with no reflection warnings. Used by the
/// <c>ToJson()</c> exporter; also usable directly:
/// <c>JsonSerializer.Serialize(result, EasyOcrJsonContext.Default.OcrResult)</c>.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(OcrResult))]
[JsonSerializable(typeof(OcrLine))]
[JsonSerializable(typeof(IReadOnlyList<OcrLine>))]
[JsonSerializable(typeof(OcrPoint))]
[JsonSerializable(typeof(OcrBoundingBox))]
[JsonSerializable(typeof(DetectedRegion))]
[JsonSerializable(typeof(IReadOnlyList<DetectedRegion>))]
public partial class EasyOcrJsonContext : JsonSerializerContext
{
}
