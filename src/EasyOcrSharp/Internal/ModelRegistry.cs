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
    /// SHA256 checksums (upper-case hex) of every published asset, verified after download by
    /// <see cref="ModelDownloadManager"/>. Generated from the exact files uploaded to the model repo;
    /// regenerate whenever the models are re-exported (see tools/export_onnx.py).
    /// </summary>
    private static readonly FrozenDictionary<string, string> Checksums = new Dictionary<string, string>
    {
        ["craft_mlt_25k.onnx"]      = "47544A1E1B0F5EDD9C31B0059EBC1C3E9F57886F9C3E2BA65CB222687323B187",
        ["latin_g2.onnx"]           = "8AF0B165299DAD129E5F34D6BFD6B58BE1F9AA8FA6641D6DD5FC32F1E00A9E8F",
        ["latin_g2.vocab.json"]     = "48F41D497C34C7240F9FCC50252C0AB5716B0A7EE4F274E0F407E0E081992575",
        ["cyrillic_g2.onnx"]        = "B9B4DFAD1AB354270908A50BA28072CAC36DD355CD61F003693B182EF7AF575D",
        ["cyrillic_g2.vocab.json"]  = "4B41CD0EA790BC4650D81FE809A9679A82AEEA9FC44191A4FE993E17A6069A82",
        ["arabic_g2.onnx"]          = "132973A45315699E90BA2D8B8552C2EBA6C1E7CD127EFBC98B9D2DA077A0DBAD",
        ["arabic_g2.vocab.json"]    = "4600FC98FC870E4137EDAA6769244FE84008C26FE8F2ADD28E5C9D56B8CE03D3",
        ["devanagari_g2.onnx"]      = "15398F20B50FB2C63929BE8C348608F9E06F60D3FD40D30ADEF498738C2AD229",
        ["devanagari_g2.vocab.json"]= "5CA6FA8CB163A0FC6656A95B8A5C661D89384F9A3611D384BF9477D12C9788D3",
        ["bengali_g2.onnx"]         = "529E371C514BB72531E1B656ADA90A8505DD291E16782297FC971610054B54B8",
        ["bengali_g2.vocab.json"]   = "23E7EAA3D4461A9ED039DE9B05494E10BFDB883277FB955A8436F86D622A45BD",
        ["zh_sim_g2.onnx"]          = "62D2884F3409C2054124E0390A6CDFB1AF6AA882AB88EC02F379B7115C59E728",
        ["zh_sim_g2.vocab.json"]    = "A17EE945D74B616133AC2B6C18F64B6D4E4CE47D9DFC4733B4F46AD82C928E60",
        ["korean_g2.onnx"]          = "DA2119CE5C32B8D9D50E69E3EFD93227F8F1ED24B0898849D94BB8A3DF87C2EF",
        ["korean_g2.vocab.json"]    = "527FD3815C3A08A1AA21E4897758CF41BC6753335110B1456D9714BAAAFFAAC2",
        ["japanese_g2.onnx"]        = "37BEA1EA1C8E91D77E8AAF1B8FE62336FA96A4656077CB77432FF3EBE0D7E682",
        ["japanese_g2.vocab.json"]  = "2152E694D4E6C40DB530EBC16524DCB2CD63035465C2AD3BB0259B5372518D2F",
        ["thai_g1.onnx"]            = "45A89FA4ADF6804432D86C34BF2B032C68308A3C6A3E0551B41DEF9D70238596",
        ["thai_g1.vocab.json"]      = "BEC206DF38D8FA070116C82779036A41A9AEC389A3F589210DC69996C84BA677",
        ["tamil_g1.onnx"]           = "3788BEB63E3817BBD51C51CBA84181D573C74E11756A49D191DDFA61FF4BFEC0",
        ["tamil_g1.vocab.json"]     = "08119B6DE36511989B1ED4008603FC9607D2F091DA695BAF2E914737BB7D1B1D",
        ["telugu_g2.onnx"]          = "25B848EC2BAC78A00262ABF505C7788BE48C69CCA6001FBF40FC225477D9FF39",
        ["telugu_g2.vocab.json"]    = "2C4F97A4206C740479546A9E07AB0B395EF4F89DC9EA84D77E6CA65D6AD89937",
        ["kannada_g2.onnx"]         = "5CC821D34B7A403A7E82264F77FE93052A3451DA2E061869AA58FA08258E0DD2",
        ["kannada_g2.vocab.json"]   = "EF031572EA8E2598966625797975E8548C3EBE1E02162AB31BC2652F119D0709",
        ["zh_tra_g1.onnx"]          = "FEE9D297196CDDF74D8B1C4DCC1C269589133EF4A54452B4822078FD98928CCC",
        ["zh_tra_g1.vocab.json"]    = "3D481F58A7AA5283F17E07974DCE7BBBBE904E56E088CCA8279B72B175C60275",
    }.ToFrozenDictionary();

    private static ModelAsset Asset(string fileName) =>
        new(fileName, $"{DefaultBaseUrl}/{fileName}", Checksums.GetValueOrDefault(fileName));

    /// <summary>
    /// CRAFT text-detection model (shared across all languages).
    /// </summary>
    public static readonly ModelAsset Detector = Asset("craft_mlt_25k.onnx");

    private static RecognizerDefinition Pack(string name, string[] languages) => new(
        Name: name,
        Model: Asset($"{name}.onnx"),
        Vocab: Asset($"{name}.vocab.json"),
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

    public static readonly RecognizerDefinition Thai = Pack("thai_g1", new[] { "th" });

    public static readonly RecognizerDefinition Tamil = Pack("tamil_g1", new[] { "ta" });

    public static readonly RecognizerDefinition Telugu = Pack("telugu_g2", new[] { "te" });

    public static readonly RecognizerDefinition Kannada = Pack("kannada_g2", new[] { "kn" });

    public static readonly RecognizerDefinition ChineseTraditional = Pack("zh_tra_g1", new[] { "ch_tra" });

    public static readonly IReadOnlyList<RecognizerDefinition> All = new[]
    {
        Latin, Cyrillic, Arabic, Devanagari, Bengali, Chinese, Korean, Japanese, Thai,
        Tamil, Telugu, Kannada, ChineseTraditional
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
