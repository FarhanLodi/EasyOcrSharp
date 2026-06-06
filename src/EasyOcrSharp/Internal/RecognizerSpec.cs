using EasyOcrSharp.Services;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Uniform handle to a recognizer the engine can load, whether it is a built-in downloadable pack
/// (<see cref="RecognizerDefinition"/>) or a user-supplied local model (<see cref="CustomRecognizer"/>).
/// Lets the engine group, cache and load both kinds through a single code path.
/// </summary>
internal sealed record RecognizerSpec(
    string Name,
    string[] Languages,
    ModelAsset? RemoteModel,
    ModelAsset? RemoteVocab,
    string? LocalModelPath,
    string? LocalVocabPath,
    string? InlineCharacters)
{
    public bool IsLocal => LocalModelPath is not null;

    public static RecognizerSpec FromDefinition(RecognizerDefinition def)
        => new(def.Name, def.Languages, def.Model, def.Vocab, null, null, null);

    public static RecognizerSpec FromCustom(CustomRecognizer custom)
        => new(
            Name: custom.Name,
            Languages: custom.Languages.ToArray(),
            RemoteModel: null,
            RemoteVocab: null,
            LocalModelPath: custom.ModelPath,
            LocalVocabPath: custom.VocabPath,
            InlineCharacters: custom.Characters);
}
