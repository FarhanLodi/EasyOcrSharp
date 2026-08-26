using System.Collections.Frozen;

namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// Describes a recognizer "pack": the recognition ONNX network plus the character
/// <see cref="Dictionary"/> sidecar holding the ordered ppocr key set the network's CTC head emits.
/// PaddleOCR ships one recognizer (and a matching dictionary) per language/script family, so a pack is
/// exactly the (recognition model, dictionary) pair selected for a requested language.
/// </summary>
/// <param name="Name">Stable pack identifier, also the recognizer session cache key (e.g. <c>latin_PP-OCRv5_mobile</c>).</param>
/// <param name="Model">The recognition ONNX model asset.</param>
/// <param name="Dictionary">The ppocr character-dictionary asset (one key per line).</param>
/// <param name="Languages">Language codes this pack serves; the first is its representative code.</param>
internal sealed record RecognizerPack(
    string Name,
    ModelAsset Model,
    ModelAsset Dictionary,
    string[] Languages);
