using EasyOcrSharp.Internal;
using Xunit;

namespace EasyOcrSharp.Tests;

public class ModelRegistryTests
{
    [Theory]
    [InlineData("en", "latin_g2")]
    [InlineData("fr", "latin_g2")]
    [InlineData("ru", "cyrillic_g2")]
    [InlineData("ar", "arabic_g2")]
    [InlineData("hi", "devanagari_g2")]
    [InlineData("bn", "bengali_g2")]
    [InlineData("ch_sim", "zh_sim_g2")]
    [InlineData("ko", "korean_g2")]
    [InlineData("ja", "japanese_g2")]
    [InlineData("th", "thai_g1")]
    [InlineData("ta", "tamil_g1")]
    [InlineData("te", "telugu_g2")]
    [InlineData("kn", "kannada_g2")]
    [InlineData("ch_tra", "zh_tra_g1")]
    public void FindByLanguage_maps_language_to_expected_pack(string lang, string expectedPack)
    {
        var def = ModelRegistry.FindByLanguage(lang);
        Assert.NotNull(def);
        Assert.Equal(expectedPack, def!.Name);
    }

    [Theory]
    [InlineData("sw", "latin_g2")]    // Swahili
    [InlineData("tjk", "cyrillic_g2")] // Tajik (cyrillic)
    [InlineData("mni", "bengali_g2")]  // Meitei
    [InlineData("gom", "devanagari_g2")] // Konkani
    [InlineData("ug", "arabic_g2")]    // Uyghur
    public void FindByLanguage_covers_full_easyocr_language_set(string lang, string expectedPack)
    {
        var def = ModelRegistry.FindByLanguage(lang);
        Assert.NotNull(def);
        Assert.Equal(expectedPack, def!.Name);
    }

    [Fact]
    public void Registry_covers_at_least_80_languages()
    {
        var total = ModelRegistry.All.SelectMany(p => p.Languages).Distinct().Count();
        Assert.True(total >= 80, $"expected >=80 languages, got {total}");
    }

    [Theory]
    [InlineData("EN")]
    [InlineData("Fr")]
    public void FindByLanguage_is_case_insensitive(string lang)
        => Assert.NotNull(ModelRegistry.FindByLanguage(lang));

    [Theory]
    [InlineData("el")]   // Greek — unsupported by EasyOCR
    [InlineData("xx")]   // nonsense
    public void FindByLanguage_returns_null_for_unsupported(string lang)
        => Assert.Null(ModelRegistry.FindByLanguage(lang));

    [Fact]
    public void Every_pack_has_a_distinct_name_and_at_least_one_language()
    {
        var names = ModelRegistry.All.Select(p => p.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(ModelRegistry.All, p => Assert.NotEmpty(p.Languages));
    }

    [Fact]
    public void All_published_assets_have_sha256_checksums()
    {
        // Detector + every pack's model and vocab must carry a checksum so downloads are verified.
        Assert.False(string.IsNullOrEmpty(ModelRegistry.Detector.Sha256));
        foreach (var pack in ModelRegistry.All)
        {
            Assert.False(string.IsNullOrEmpty(pack.Model.Sha256), $"{pack.Name} model missing checksum");
            Assert.False(string.IsNullOrEmpty(pack.Vocab.Sha256), $"{pack.Name} vocab missing checksum");
            Assert.EndsWith(".onnx", pack.Model.FileName);
            Assert.EndsWith(".vocab.json", pack.Vocab.FileName);
        }
    }
}
