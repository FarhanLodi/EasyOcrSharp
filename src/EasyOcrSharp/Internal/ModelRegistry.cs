using System.Collections.Frozen;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Defines a downloadable ONNX model asset.
/// </summary>
internal sealed record ModelAsset(string FileName, string Url, string? Sha256);

/// <summary>
/// Describes a recognizer model: the ONNX network plus the <see cref="Vocab"/> sidecar
/// holding the exact ordered character set the network emits (the CTC label order, minus
/// the blank token at index 0). The vocabulary is downloaded alongside the model rather
/// than hardcoded so the decode order can never drift out of sync with the exported weights.
/// </summary>
internal sealed record RecognizerDefinition(
    string Name,
    ModelAsset Model,
    ModelAsset Vocab,
    string[] Languages);

/// <summary>
/// Static catalogue of EasyOCR-derived ONNX models exported from upstream PyTorch weights.
/// Each language is mapped to one of EasyOCR's recognizer "packs" (latin, cyrillic, arabic, etc.)
/// because EasyOCR ships one CRNN model per script family, not per individual language.
/// </summary>
internal static class ModelRegistry
{
    /// <summary>
    /// Base URL where exported ONNX assets are hosted. Override at runtime via
    /// the EASYOCRSHARP_MODEL_BASE_URL environment variable to use a private mirror.
    /// </summary>
    public const string DefaultBaseUrl =
        "https://huggingface.co/EasyOcrSharp/EasyOcrSharp-models/resolve/main";

    /// <summary>
    /// CRAFT text-detection model (shared across all languages).
    /// </summary>
    public static readonly ModelAsset Detector = new(
        FileName: "craft_mlt_25k.onnx",
        Url: $"{DefaultBaseUrl}/craft_mlt_25k.onnx",
        Sha256: null);

    private static RecognizerDefinition Pack(string name, string[] languages) => new(
        Name: name,
        Model: new ModelAsset($"{name}.onnx", $"{DefaultBaseUrl}/{name}.onnx", null),
        Vocab: new ModelAsset($"{name}.vocab.json", $"{DefaultBaseUrl}/{name}.vocab.json", null),
        Languages: languages);

    public static readonly RecognizerDefinition Latin = Pack("latin_g2", new[]
    {
        "en","es","fr","de","it","pt","nl","pl","cs","sv","hu","fi","ro","no","da","hr",
        "sk","sl","sr_latn","sq","et","lv","lt","is","ga","mt","af","id","ms","tl","vi",
        "tr","ca","eu","gl","ku","la","cy","mi","oc","rs_latin"
    });

    public static readonly RecognizerDefinition Cyrillic = Pack("cyrillic_g2", new[]
    {
        "ru","sr","kk","az","uz","ky","mn","be","uk","bg","mk","tg","ab"
    });

    public static readonly RecognizerDefinition Arabic = Pack("arabic_g2", new[]
    {
        "ar","fa","ur","ug","ps"
    });

    public static readonly RecognizerDefinition Devanagari = Pack("devanagari_g2", new[]
    {
        "hi","mr","ne","sa"
    });

    public static readonly RecognizerDefinition Bengali = Pack("bengali_g2", new[]
    {
        "bn","as"
    });

    public static readonly RecognizerDefinition Chinese = Pack("zh_sim_g2", new[]
    {
        "ch_sim","zh_sim"
    });

    public static readonly RecognizerDefinition Korean = Pack("korean_g2", new[] { "ko" });

    public static readonly RecognizerDefinition Japanese = Pack("japanese_g2", new[] { "ja" });

    public static readonly IReadOnlyList<RecognizerDefinition> All = new[]
    {
        Latin, Cyrillic, Arabic, Devanagari, Bengali, Chinese, Korean, Japanese
    };

    private static readonly FrozenDictionary<string, RecognizerDefinition> ByLanguage = BuildLanguageIndex();

    private static FrozenDictionary<string, RecognizerDefinition> BuildLanguageIndex()
    {
        var dict = new Dictionary<string, RecognizerDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in All)
        {
            foreach (var lang in def.Languages)
            {
                dict[lang] = def;
            }
        }
        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the recognizer pack that handles the given language code, or null if unsupported.
    /// </summary>
    public static RecognizerDefinition? FindByLanguage(string language)
        => ByLanguage.TryGetValue(language, out var def) ? def : null;
}
