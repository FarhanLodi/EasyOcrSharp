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
    /// regenerate whenever the models are re-exported.
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

        // int8-quantized recognizer variants (EasyOcrServiceOptions.Quantize), produced by
        // tools/quantize_onnx.py. Hosted alongside the float models; vocab sidecars are shared.
        ["arabic_g2.int8.onnx"]     = "96873B85CB75F24AB590D5AFF82BDD9AC9B6C12C4462342EF52553225A4DFA94",
        ["bengali_g2.int8.onnx"]    = "8980FB0B0C8A9F90780C3BEF763630E5C4FE744C134DACC7C8F939CC7D98DFB2",
        ["cyrillic_g2.int8.onnx"]   = "3BD8A1D5916DA44B2D6DEE49B22E488F855D344778A57474FCC1E1D742661ECE",
        ["devanagari_g2.int8.onnx"] = "8A92C9185E0DA513A6DF15A0C9F3E15134AB2F0C014B19776880B9F5C2BCCA63",
        ["japanese_g2.int8.onnx"]   = "F29AD867D2A6D3CF3ABCEFB849A318D722D1F0D81C99006E4D83FDADDDFF475B",
        ["kannada_g2.int8.onnx"]    = "37A8B4F51ECEF1FA4F5750B50A43B7BD90D7635F3BC53A5715A18B4D26837A77",
        ["korean_g2.int8.onnx"]     = "92BC1D47F78CF4682DF6D30158FA9E655E7D07F28F7884BA09E9040696C8A470",
        ["latin_g2.int8.onnx"]      = "EDC800BB03D35BE2392CD0BD33903EE64E2713B7DDF9A5CE96124C01BAFFCED4",
        ["tamil_g1.int8.onnx"]      = "460A9913C35A81F72C40D4C10CCF44C519CF774E6ECFA77BAA28177A6FCF41D6",
        ["telugu_g2.int8.onnx"]     = "D1E88F1B39C547050E84D1D2EB06D06651C36DFF69CBCBB7EE4488CEEDC4CC8C",
        ["thai_g1.int8.onnx"]       = "0489D01351BDF912C09409CA1FF58CE8CFEC8E1F5A5FE68AFCD57D155C73B3D2",
        ["zh_sim_g2.int8.onnx"]     = "E321B59FE43A22444E6AEED9B3E23EB50049E4F842C7285649FA2EB676DE6AC3",
        ["zh_tra_g1.int8.onnx"]     = "F2A543425A360F80EB9E1973ECFC2CBB05D7C1F38173A67BD5FFE70E85C82E7C",
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

    // Language lists mirror EasyOCR's config.py exactly, so every one of EasyOCR's 86 supported
    // languages resolves to the recognizer pack it was trained on.
    // 'en' is intentionally first so it serves as the pack's representative (e.g. for auto-detect).
    public static readonly RecognizerDefinition Latin = Pack("latin_g2", new[]
    {
        "en","af","az","bs","cs","cy","da","de","es","et","fr","ga","hr","hu","id","is","it",
        "ku","la","lt","lv","mi","ms","mt","nl","no","oc","pi","pl","pt","ro","rs_latin","sk",
        "sl","sq","sv","sw","tl","tr","uz","vi"
    });

    public static readonly RecognizerDefinition Cyrillic = Pack("cyrillic_g2", new[]
    {
        "ru","rs_cyrillic","be","bg","uk","mn","abq","ady","kbd","ava","dar","inh","che","lbe","lez","tab","tjk"
    });

    public static readonly RecognizerDefinition Arabic = Pack("arabic_g2", new[]
    {
        "ar","fa","ug","ur"
    });

    public static readonly RecognizerDefinition Devanagari = Pack("devanagari_g2", new[]
    {
        "hi","mr","ne","bh","mai","ang","bho","mah","sck","new","gom","sa","bgc"
    });

    public static readonly RecognizerDefinition Bengali = Pack("bengali_g2", new[]
    {
        "bn","as","mni"
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

    /// <summary>
    /// The int8-quantized variant of a recognizer's ONNX model (EasyOCR's <c>quantize=True</c>),
    /// hosted alongside the float model as <c>&lt;pack&gt;.int8.onnx</c>. The vocab sidecar is shared.
    /// Produced by <c>tools/quantize_onnx.py</c>; its checksum, once known, can be added to
    /// <see cref="Checksums"/>.
    /// </summary>
    public static ModelAsset QuantizedModel(RecognizerDefinition def) => Asset($"{def.Name}.int8.onnx");
}
