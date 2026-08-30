using EasyOcrSharp.Structure.Engine.Recognition;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for
/// <see cref="CharacterDictionary.BuildVocab"/> — the step that matches a PaddleOCR dictionary file to the
/// recognizer's output class count. Community PP-OCRv5 exports disagree on whether the file already
/// contains the CTC blank and the trailing space class, and getting that wrong shifts every decoded
/// character by one class.
/// </summary>
public class CharacterDictionaryTests
{
    [Fact]
    public void BuildVocab_uses_the_file_verbatim_when_it_is_already_the_full_class_list()
    {
        // ppocrv5_dict.txt ships as the complete class list: index 0 is the blank slot, the last class is
        // the literal space. Line count == class count, so nothing is added.
        var lines = new[] { "　", "a", "b", " " };

        var vocab = CharacterDictionary.BuildVocab(lines, numClasses: 4);

        Assert.Equal(lines, vocab);
    }

    [Fact]
    public void BuildVocab_treats_an_empty_first_line_as_the_blank_and_appends_the_space()
    {
        // Regression: the per-script PP-OCRv5 dicts (cyrillic_dict.txt, latin_dict.txt, arabic_dict.txt, …)
        // start with an empty line — that empty line IS the blank class, so the class the file omits is the
        // trailing space, not the blank. Prepending another blank shifted every character by one class and
        // turned Cyrillic output into mojibake (PaddleOcrNet issue #1, same engine).
        var lines = new[] { "", "!", "\"", "А", "Б" };

        var vocab = CharacterDictionary.BuildVocab(lines, numClasses: 6);

        Assert.Equal(new[] { CharacterDictionary.Blank, "!", "\"", "А", "Б", " " }, vocab);
        // The first real dictionary token stays at class 1 — where the network emits it.
        Assert.Equal("!", vocab[1]);
        Assert.Equal(" ", vocab[^1]);
    }

    [Fact]
    public void BuildVocab_prepends_the_blank_when_the_file_starts_with_a_real_token()
    {
        // A dict that is short by one class but whose first line is a real token omits only the blank.
        var lines = new[] { "a", "b", "c" };

        var vocab = CharacterDictionary.BuildVocab(lines, numClasses: 4);

        Assert.Equal(new[] { CharacterDictionary.Blank, "a", "b", "c" }, vocab);
    }

    [Fact]
    public void BuildVocab_applies_the_canonical_layout_when_the_file_omits_blank_and_space()
    {
        var lines = new[] { "a", "b", "c" };

        var vocab = CharacterDictionary.BuildVocab(lines, numClasses: 5);

        Assert.Equal(new[] { CharacterDictionary.Blank, "a", "b", "c", " " }, vocab);
    }

    [Fact]
    public void BuildVocab_falls_back_to_the_canonical_convention_when_the_class_count_is_unknown()
    {
        var lines = new[] { "a", "b" };

        var vocab = CharacterDictionary.BuildVocab(lines, numClasses: 0);

        Assert.Equal(new[] { CharacterDictionary.Blank, "a", "b", " " }, vocab);
    }

    [Fact]
    public void BuildVocab_pads_or_truncates_an_unrecognized_mismatch_to_stay_in_bounds()
    {
        var lines = new[] { "a", "b", "c" };

        // A wrong glyph beats an out-of-range crash mid-batch, so the vocab is sized to the network either way.
        Assert.Equal(3, CharacterDictionary.BuildVocab(lines, numClasses: 3).Count);
        Assert.Equal(9, CharacterDictionary.BuildVocab(lines, numClasses: 9).Count);
    }

    [Fact]
    public void The_reported_cyrillic_mojibake_no_longer_reproduces()
    {
        // The exact failure from the field: "ИСТОРИЯ РОССИЙСКОГО ГОСУДАРСТВА" came back as
        // "ЗРСНПЗЮ…" — every letter one position earlier in the dictionary, because the old vocab
        // prepended a second blank on top of the file's own empty first line. cyrillic_dict.txt lists the
        // Russian alphabet in order, so shifting by one class turns И into З, С into Р, Т into С, and so
        // on. This mini dictionary has the same shape as the real file: empty first line (the blank slot),
        // then tokens, and no trailing space.
        const string Alphabet = "АБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ";
        var lines = new[] { "" }.Concat(Alphabet.Select(c => c.ToString())).ToArray();

        var vocab = CharacterDictionary.BuildVocab(lines, numClasses: lines.Length + 1);

        // Class k decodes to the k-th dictionary token, not the one before it.
        const string Text = "ИСТОРИЯ";
        const string Mojibake = "ЗРСНПЗЮ";
        for (int i = 0; i < Text.Length; i++)
        {
            int classId = vocab.ToList().IndexOf(Text[i].ToString());
            Assert.NotEqual(-1, classId);
            Assert.Equal(Text[i].ToString(), vocab[classId]);
            Assert.NotEqual(Mojibake[i].ToString(), vocab[classId]);
        }
    }

    [Theory]
    // (dictionary lines, model output classes) for every recognizer StructureModelRegistry ships. The
    // per-script packs all lead with an empty line; the default ppocrv5 dict is the full class list.
    [InlineData(851, 852)]     // cyrillic_PP-OCRv5_mobile
    [InlineData(837, 838)]     // latin_PP-OCRv5_mobile
    [InlineData(748, 749)]     // arabic_PP-OCRv5_mobile
    [InlineData(569, 570)]     // devanagari_PP-OCRv5_mobile
    [InlineData(11946, 11947)] // korean_PP-OCRv5_mobile
    [InlineData(4400, 4401)]   // japan_PP-OCRv5_mobile
    [InlineData(525, 526)]     // th_PP-OCRv5_mobile
    [InlineData(355, 356)]     // el_PP-OCRv5_mobile
    [InlineData(541, 542)]     // te_PP-OCRv5_mobile
    [InlineData(514, 515)]     // ta_PP-OCRv5_mobile
    [InlineData(518, 519)]     // eslav_PP-OCRv5_mobile
    public void BuildVocab_aligns_every_shipped_language_pack_dictionary(int dictLines, int numClasses)
    {
        // Line 0 empty (the blank slot), then dictLines-1 real tokens, mirroring the shipped files.
        var lines = new string[dictLines];
        lines[0] = string.Empty;
        for (int i = 1; i < dictLines; i++)
            lines[i] = $"c{i}";

        var vocab = CharacterDictionary.BuildVocab(lines, numClasses);

        Assert.Equal(numClasses, vocab.Count);
        Assert.Equal(CharacterDictionary.Blank, vocab[0]);
        Assert.Equal(" ", vocab[^1]);
        // Every dictionary token keeps the class index the network was trained to emit for it.
        for (int i = 1; i < dictLines; i++)
            Assert.Equal(lines[i], vocab[i]);
    }
}
