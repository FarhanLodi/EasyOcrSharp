using EasyOcrSharp.Correction;
using EasyOcrSharp.Extraction;
using EasyOcrSharp.Models;
using Xunit;
using static EasyOcrSharp.Tests.ExtractionTestPages;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Model-free tests for <see cref="MrzParser"/> using the ICAO Doc 9303 specimen zones (the fictional
/// state "Utopia", holder Anna Maria Eriksson) for TD1, TD2, TD3 and the MRV-A visa, plus OCR misreads
/// that positional correction must repair and ones it must refuse to guess.
/// </summary>
public class MrzParserTests
{
    // ICAO 9303 Part 4 (TD3 passport) specimen.
    private const string Td3Line1 = "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<";
    private const string Td3Line2 = "L898902C36UTO7408122F1204159ZE184226B<<<<<10";

    // ICAO 9303 Part 5 (TD1 identity card) specimen.
    private const string Td1Line1 = "I<UTOD231458907<<<<<<<<<<<<<<<";
    private const string Td1Line2 = "7408122F1204159UTO<<<<<<<<<<<6";
    private const string Td1Line3 = "ERIKSSON<<ANNA<MARIA<<<<<<<<<<";

    // ICAO 9303 Part 6 (TD2) specimen.
    private const string Td2Line1 = "I<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<";
    private const string Td2Line2 = "D231458907UTO7408122F1204159<<<<<<<6";

    // ICAO 9303 Part 7 (MRV-A visa) specimen.
    private const string VisaLine1 = "V<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<";
    private const string VisaLine2 = "L8988901C4XXX4009078F96121096ZE184226B<<<<<<";

    private static MrzResult Parse(params string[] lines)
    {
        Assert.True(MrzParser.TryParse(lines, out var mrz));
        return mrz!;
    }

    [Fact]
    public void Td3_passport_specimen_parses_every_field()
    {
        var mrz = Parse(Td3Line1, Td3Line2);

        Assert.Equal(MrzFormat.Td3, mrz.Format);
        Assert.Equal("P", mrz.DocumentType);
        Assert.Equal("UTO", mrz.IssuingCountry);
        Assert.Equal("ERIKSSON", mrz.Surname);
        Assert.Equal("ANNA MARIA", mrz.GivenNames);
        Assert.Equal("L898902C3", mrz.DocumentNumber);
        Assert.Equal("UTO", mrz.Nationality);
        Assert.Equal(new DateOnly(1974, 8, 12), mrz.BirthDate);
        Assert.Equal("F", mrz.Sex);
        Assert.Equal(new DateOnly(2012, 4, 15), mrz.ExpiryDate);
        Assert.Equal("ZE184226B", mrz.OptionalData);
        Assert.Null(mrz.OptionalData2);
        Assert.Equal(new MrzCheckResults(true, true, true, true, true), mrz.Checks);
        Assert.True(mrz.IsValid);
        Assert.Equal(new[] { Td3Line1, Td3Line2 }, mrz.RawLines);
        Assert.Equal(new[] { Td3Line1, Td3Line2 }, mrz.CorrectedLines);
    }

    [Fact]
    public void Td1_identity_card_specimen_parses_every_field()
    {
        var mrz = Parse(Td1Line1, Td1Line2, Td1Line3);

        Assert.Equal(MrzFormat.Td1, mrz.Format);
        Assert.Equal("I", mrz.DocumentType);
        Assert.Equal("UTO", mrz.IssuingCountry);
        Assert.Equal("D23145890", mrz.DocumentNumber);
        Assert.Equal(new DateOnly(1974, 8, 12), mrz.BirthDate);
        Assert.Equal("F", mrz.Sex);
        Assert.Equal(new DateOnly(2012, 4, 15), mrz.ExpiryDate);
        Assert.Equal("UTO", mrz.Nationality);
        Assert.Equal("ERIKSSON", mrz.Surname);
        Assert.Equal("ANNA MARIA", mrz.GivenNames);
        Assert.Equal(string.Empty, mrz.OptionalData);
        Assert.Equal(string.Empty, mrz.OptionalData2);
        Assert.Null(mrz.Checks.OptionalData);
        Assert.True(mrz.Checks.Composite);
        Assert.True(mrz.IsValid);
    }

    [Fact]
    public void Td2_specimen_parses_and_verifies_its_composite()
    {
        var mrz = Parse(Td2Line1, Td2Line2);

        Assert.Equal(MrzFormat.Td2, mrz.Format);
        Assert.Equal("I", mrz.DocumentType);
        Assert.Equal("D23145890", mrz.DocumentNumber);
        Assert.Equal("ERIKSSON", mrz.Surname);
        Assert.Equal("ANNA MARIA", mrz.GivenNames);
        Assert.Equal(string.Empty, mrz.OptionalData);
        Assert.Null(mrz.Checks.OptionalData);
        Assert.True(mrz.Checks.Composite);
        Assert.True(mrz.IsValid);
    }

    [Fact]
    public void Mrv_a_visa_has_no_optional_or_composite_check()
    {
        var mrz = Parse(VisaLine1, VisaLine2);

        Assert.Equal(MrzFormat.Td3, mrz.Format);
        Assert.Equal("V", mrz.DocumentType);
        Assert.Equal("L8988901C", mrz.DocumentNumber);
        Assert.Equal("XXX", mrz.Nationality);
        Assert.Equal(new DateOnly(1940, 9, 7), mrz.BirthDate);
        Assert.Equal(new DateOnly(1996, 12, 10), mrz.ExpiryDate);
        Assert.Equal("6ZE184226B", mrz.OptionalData);
        Assert.Null(mrz.Checks.OptionalData);
        Assert.Null(mrz.Checks.Composite);
        Assert.True(mrz.IsValid);
    }

    [Fact]
    public void Td1_long_document_number_continues_in_the_optional_data()
    {
        // Ten-character number: nine in the field, '<' at position 15, then "AB" and the check digit.
        var mrz = Parse("I<UTOD23145890<AB0<<<<<<<<<<<<", "7408122F1204159UTO<<<<<<<<<<<8", Td1Line3);

        Assert.Equal("D23145890AB", mrz.DocumentNumber);
        Assert.True(mrz.Checks.DocumentNumber);
        Assert.Equal(string.Empty, mrz.OptionalData);
        Assert.True(mrz.IsValid);
    }

    // ---- OCR damage ----

    [Fact]
    public void Letters_misread_in_date_fields_are_corrected_positionally()
    {
        const string damaged = "L898902C36UTO74O8122F12O4159ZE184226B<<<<<10";
        var mrz = Parse(Td3Line1, damaged);

        Assert.True(mrz.IsValid);
        Assert.Equal(new DateOnly(1974, 8, 12), mrz.BirthDate);
        Assert.Equal(new DateOnly(2012, 4, 15), mrz.ExpiryDate);
        Assert.Equal(damaged, mrz.RawLines[1]);          // the line as read is kept
        Assert.Equal(Td3Line2, mrz.CorrectedLines[1]);   // the fields were decoded from the repaired line
    }

    [Fact]
    public void Digits_misread_in_name_and_country_fields_are_corrected_positionally()
    {
        var mrz = Parse("P<UT0ER1KSSON<<ANNA<MAR1A<<<<<<<<<<<<<<<<<<<", Td3Line2);

        Assert.Equal("UTO", mrz.IssuingCountry);
        Assert.Equal("ERIKSSON", mrz.Surname);
        Assert.Equal("ANNA MARIA", mrz.GivenNames);
        Assert.True(mrz.IsValid);
    }

    [Theory]
    [InlineData("L8989O2C36UTO7408122F1204159ZE184226B<<<<<10")] // O read for 0
    [InlineData("L89B902C36UTO7408122F1204159ZE184226B<<<<<10")] // B read for 8
    public void Document_number_misread_is_repaired_minimally_keeping_legitimate_letters(string line2)
    {
        // Converting every look-alike would also turn the leading L into 1 — which the 7-3-1 check digit
        // cannot see (L = 21 ≡ 1 mod 10). The minimal repair changes only the misread character.
        var mrz = Parse(Td3Line1, line2);

        Assert.Equal("L898902C3", mrz.DocumentNumber);
        Assert.True(mrz.Checks.DocumentNumber);
        Assert.True(mrz.IsValid);
    }

    [Fact]
    public void Spaces_guillemets_and_k_fillers_are_normalized()
    {
        var mrz = Parse(
            "P<UTO ERIKSSON«ANNA<MARIA<<<<<<<<<KKKKKKKKKK",
            "L898902C36UTO7408122F1204159ZE184226B<<<<<10");

        Assert.Equal("ERIKSSON", mrz.Surname);
        Assert.Equal("ANNA MARIA", mrz.GivenNames);
        Assert.True(mrz.IsValid);
    }

    [Fact]
    public void Up_to_four_missing_trailing_fillers_are_padded()
    {
        var mrz = Parse(Td3Line1[..41], Td3Line2);
        Assert.Equal(44, mrz.RawLines[0].Length);
        Assert.True(mrz.IsValid);
    }

    [Fact]
    public void Failing_composite_is_reported_without_rejecting_the_zone()
    {
        var mrz = Parse(Td3Line1, Td3Line2[..43] + "5");

        Assert.False(mrz.IsValid);
        Assert.Equal(new MrzCheckResults(true, true, true, true, false), mrz.Checks);
    }

    [Fact]
    public void Text_that_is_not_an_mrz_is_not_parsed()
    {
        Assert.False(MrzParser.TryParse(new[] { "Hello world", "Total 12.50" }, out var mrz));
        Assert.Null(mrz);
        Assert.False(MrzParser.TryParse(Array.Empty<string>(), out _));
    }

    [Fact]
    public void Mrz_surrounded_by_other_text_is_located()
    {
        var mrz = Parse("PASSPORT", "Surname / Nom", Td3Line1, Td3Line2, "Signature");
        Assert.Equal("L898902C3", mrz.DocumentNumber);
        Assert.True(mrz.IsValid);
    }

    // ---- OCR results ----

    [Fact]
    public void TryParse_joins_an_mrz_line_split_into_several_detections()
    {
        var page = Page(
            Line(Td3Line1, 0, 60, 440, 20),
            Line("1204159ZE184226B<<<<<10", 210, 100, 230, 20),   // right half first, as a detector may emit it
            Line("L898902C36UTO7408122F", 0, 100, 200, 20));

        Assert.True(MrzParser.TryParse(page, out var mrz));
        Assert.True(mrz!.IsValid);
        Assert.Equal("L898902C3", mrz.DocumentNumber);
    }

    [Fact]
    public void TryParse_works_on_results_without_geometry()
    {
        var page = Page(TextOnly(Td1Line1), TextOnly(Td1Line2), TextOnly(Td1Line3));

        Assert.True(MrzParser.TryParse(page, out var mrz));
        Assert.Equal(MrzFormat.Td1, mrz!.Format);
        Assert.True(mrz.IsValid);
    }

    [Fact]
    public void TryParse_validates_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => MrzParser.TryParse((OcrResult)null!, out _));
        Assert.Throws<ArgumentNullException>(() => MrzParser.TryParse((IEnumerable<string>)null!, out _));
    }

    // ---- options and shared check digit ----

    [Fact]
    public void RecommendedRecognitionOptions_restricts_output_to_the_mrz_alphabet()
    {
        var options = MrzParser.RecommendedRecognitionOptions;

        Assert.Equal(MrzParser.Alphabet, options.Allowlist);
        Assert.Equal(37, MrzParser.Alphabet.Length);
        Assert.NotSame(options, MrzParser.RecommendedRecognitionOptions);
        Assert.Null(options.Region);
    }

    [Fact]
    public void Check_digits_come_from_the_shared_field_normalizer_routine()
    {
        Assert.Equal(6, FieldNormalizers.MrzCheckDigit("L898902C3"));
        Assert.Equal(6, FieldNormalizers.ComputeMrzCheckDigit("L898902C3"));
        Assert.Equal(-1, FieldNormalizers.ComputeMrzCheckDigit("ab"));
    }
}
