using EasyOcrSharp.Structure.Engine;
using EasyOcrSharp.Structure.Engine.Recognition;
using Xunit;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// The multi-pack vote's tie-break between a script pack's reading and the default pack's Latin look-alike
/// reading of the same crop (<see cref="StructureTextEngine.IsBetterReading"/>).
/// </summary>
public sealed class StructureScriptEvidenceTests
{
    [Theory]
    [InlineData("ВЫХОД", "Cyrillic")]
    [InlineData("Ж", "Cyrillic")]
    [InlineData("Ωμέγα", "Greek")]
    [InlineData("مخرج", "Arabic")]
    [InlineData("निकास", "Devanagari")]
    [InlineData("ทางออก", "Thai")]
    [InlineData("출구", "Hangul")]
    [InlineData("EXIT", "Unknown")]
    [InlineData("ЕХІТ", "Unknown")]      // Cyrillic, but every letter has a Latin twin
    [InlineData("ΑΒΕΗ", "Unknown")]      // Greek look-alikes only
    [InlineData("出口", "Unknown")]       // CJK is the default pack's own script
    [InlineData("12:30", "Unknown")]
    public void Distinctive_script_ignores_latin_look_alikes(string text, string expected)
    {
        Assert.Equal(Enum.Parse<DetectedScript>(expected), ScriptDetection.DistinctiveScript(text));
    }

    [Fact]
    public void A_script_reading_beats_a_marginally_more_confident_latin_look_alike()
    {
        // Measured on the ocr_test4 Russian EXIT sign: Cyrillic pack 0.966, default pack 0.969.
        var cyrillic = new RecognizedText("ВЫХОД", 0.966f);
        var latin = new RecognizedText("BbIXOA", 0.969f);

        Assert.True(StructureTextEngine.IsBetterReading(cyrillic, latin));
        // Order-independent: the Latin reading arriving second does not displace it.
        Assert.False(StructureTextEngine.IsBetterReading(latin, cyrillic));
    }

    [Fact]
    public void Script_evidence_does_not_override_a_clearly_more_confident_latin_reading()
    {
        var cyrillic = new RecognizedText("ВЫХОД", 0.60f);
        var latin = new RecognizedText("BbIXOA", 0.95f);

        Assert.False(StructureTextEngine.IsBetterReading(cyrillic, latin));
        Assert.True(StructureTextEngine.IsBetterReading(latin, cyrillic));
    }

    [Fact]
    public void Look_alike_only_readings_compare_on_confidence()
    {
        var latin = new RecognizedText("EXIT", 0.999f);
        var lookAlike = new RecognizedText("ЕХІТ", 0.98f);

        Assert.False(StructureTextEngine.IsBetterReading(lookAlike, latin));
        Assert.True(StructureTextEngine.IsBetterReading(latin, lookAlike));
    }

    [Fact]
    public void Readings_in_the_same_script_compare_on_confidence()
    {
        Assert.True(StructureTextEngine.IsBetterReading(new RecognizedText("ВЫХОД", 0.9f), new RecognizedText("ВЫХОЛ", 0.8f)));
        Assert.False(StructureTextEngine.IsBetterReading(new RecognizedText("ВЫХОЛ", 0.8f), new RecognizedText("ВЫХОД", 0.9f)));
        Assert.True(StructureTextEngine.IsBetterReading(new RecognizedText("Hello", 0.9f), new RecognizedText("He1lo", 0.8f)));
    }

    [Fact]
    public void Script_evidence_only_competes_with_latin_readings()
    {
        // A CJK reading is not a Latin look-alike; the plain confidence rule decides.
        var han = new RecognizedText("出口", 0.97f);
        var cyrillic = new RecognizedText("ВЫХОД", 0.92f);

        Assert.False(StructureTextEngine.IsBetterReading(cyrillic, han));
    }
}
