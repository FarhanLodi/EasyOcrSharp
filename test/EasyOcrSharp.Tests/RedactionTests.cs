using System.Text.RegularExpressions;
using EasyOcrSharp.Models;
using EasyOcrSharp.Redaction;
using EasyOcrSharp.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Unit tests for the redaction API (feature #4). Everything here is CI-safe: no models, no network —
/// the pattern presets are pure functions and the painting is exercised against synthetic in-memory
/// images with hand-built <see cref="OcrResult"/>s.
/// </summary>
public class RedactionPatternTests
{
    private static bool Matches(RedactionRule rule, string text)
    {
        foreach (Match m in rule.Pattern.Matches(text))
        {
            if (m.Length > 0 && rule.Accepts(m.Value)) return true;
        }
        return false;
    }

    [Theory]
    [InlineData("write to jane.doe+tag@example.co.uk please")]
    [InlineData("a@b.io")]
    [InlineData("FIRST.LAST@sub.domain.example.com")]
    public void Email_matches_addresses(string text) => Assert.True(Matches(RedactionPatterns.Email, text));

    [Theory]
    [InlineData("no address here")]
    [InlineData("jane.doe@localhost")]      // no TLD
    [InlineData("@example.com")]            // no local part
    [InlineData("jane.doe@example.c")]      // one-letter TLD
    public void Email_rejects_non_addresses(string text) => Assert.False(Matches(RedactionPatterns.Email, text));

    [Theory]
    [InlineData("call +44 20 7946 0958 today")]
    [InlineData("(555) 123-4567")]
    [InlineData("555.123.4567")]
    [InlineData("+1 202 555 0182")]
    public void Phone_matches_loose_international_numbers(string text)
        => Assert.True(Matches(RedactionPatterns.Phone, text));

    [Theory]
    [InlineData("only 12345 here")]                 // 5 digits: below the E.164 minimum
    [InlineData("ref 1234567890123456789012")]      // 22 digits: above the E.164 maximum
    [InlineData("no digits at all")]
    public void Phone_rejects_implausible_digit_counts(string text)
        => Assert.False(Matches(RedactionPatterns.Phone, text));

    [Theory]
    [InlineData("card 4111 1111 1111 1111 exp 12/29")]   // Visa test number
    [InlineData("5500-0000-0000-0004")]                  // MasterCard test number
    [InlineData("378282246310005")]                      // Amex test number, 15 digits
    public void CreditCard_matches_luhn_valid_numbers(string text)
        => Assert.True(Matches(RedactionPatterns.CreditCard, text));

    [Theory]
    [InlineData("card 4111 1111 1111 1112")]   // right shape, fails Luhn
    [InlineData("order 1234567890123456")]     // 16 digits, fails Luhn
    [InlineData("po 4111 1111 1111")]          // 12 digits: too short to be a card
    public void CreditCard_rejects_non_luhn_and_wrong_length(string text)
        => Assert.False(Matches(RedactionPatterns.CreditCard, text));

    [Fact]
    public void CreditCard_regex_alone_matches_what_luhn_rejects()
    {
        // The point of the validator: the pattern is happy, the check digit is not.
        Assert.Matches(RedactionPatterns.CreditCard.Pattern, "1234567890123456");
        Assert.False(RedactionPatterns.CreditCard.Accepts("1234567890123456"));
    }

    [Theory]
    [InlineData("pay to GB82 WEST 1234 5698 7654 32")]
    [InlineData("DE89370400440532013000")]
    [InlineData("FR1420041010050500013M02606")]
    public void Iban_matches_mod97_valid(string text) => Assert.True(Matches(RedactionPatterns.Iban, text));

    [Theory]
    [InlineData("pay to GB82 WEST 1234 5698 7654 33")]   // last digit changed: mod-97 fails
    [InlineData("DE89370400440532013001")]
    [InlineData("XX00 SHORT")]                            // too short
    public void Iban_rejects_bad_check_digits(string text) => Assert.False(Matches(RedactionPatterns.Iban, text));

    [Theory]
    [InlineData("ssn 123-45-6789")]
    [InlineData("123 45 6789")]
    [InlineData("123456789")]
    public void Ssn_matches_issued_forms(string text)
        => Assert.True(Matches(RedactionPatterns.UsSocialSecurityNumber, text));

    [Theory]
    [InlineData("000-45-6789")]   // area 000 is never issued
    [InlineData("666-45-6789")]   // area 666 is never issued
    [InlineData("900-45-6789")]   // 9xx is never issued
    [InlineData("123-00-6789")]   // zero group
    [InlineData("123-45-0000")]   // zero serial
    public void Ssn_rejects_never_issued_numbers(string text)
        => Assert.False(Matches(RedactionPatterns.UsSocialSecurityNumber, text));

    [Fact]
    public void LongDigitRun_needs_six_digits()
    {
        Assert.True(Matches(RedactionPatterns.LongDigitRun, "acct 123456"));
        Assert.False(Matches(RedactionPatterns.LongDigitRun, "acct 12345"));
    }

    [Fact]
    public void Common_excludes_the_noisy_long_digit_run()
    {
        Assert.DoesNotContain(RedactionPatterns.LongDigitRun, RedactionPatterns.Common);
        Assert.Contains(RedactionPatterns.LongDigitRun, RedactionPatterns.All);
        Assert.Equal(6, RedactionPatterns.All.Count);
    }

    [Theory]
    [InlineData("4111111111111111", true)]
    [InlineData("4111 1111 1111 1111", true)]
    [InlineData("4111-1111-1111-1111", true)]
    [InlineData("4111111111111112", false)]
    [InlineData("79927398713", true)]
    [InlineData("79927398710", false)]
    [InlineData("4111 1111 11a1 1111", false)]   // non-digit content is never valid
    [InlineData("", false)]
    public void Luhn_check(string value, bool expected) => Assert.Equal(expected, RedactionPatterns.IsValidLuhn(value));

    [Theory]
    [InlineData("GB82 WEST 1234 5698 7654 32", true)]
    [InlineData("gb82west12345698765432", true)]     // case-insensitive, spacing-insensitive
    [InlineData("GB82 WEST 1234 5698 7654 33", false)]
    [InlineData("GB82WEST1234569876543", false)]     // one character short of the UK length
    [InlineData("1B82WEST12345698765432", false)]    // country code must be letters
    [InlineData("", false)]
    public void Iban_check(string value, bool expected) => Assert.Equal(expected, RedactionPatterns.IsValidIban(value));
}

/// <summary>
/// Geometry and pixel-level tests for the redaction painter: padding math, rotated-quad fill, and the
/// blur/pixelate styles actually altering the region they cover.
/// </summary>
public class RedactionPaintingTests
{
    private static readonly Rgb24 White = new(255, 255, 255);
    private static readonly Rgb24 Black = new(0, 0, 0);

    private static Image<Rgb24> Canvas(int w, int h) => new(w, h, White);

    private static OcrPoint[] Rect(double x0, double y0, double x1, double y1)
        => new[] { new OcrPoint(x0, y0), new OcrPoint(x1, y0), new OcrPoint(x1, y1), new OcrPoint(x0, y1) };

    /// <summary>A <paramref name="halfWidth"/>×<paramref name="halfHeight"/> quad rotated about its centre.</summary>
    private static OcrPoint[] RotatedRect(double cx, double cy, double halfWidth, double halfHeight, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        OcrPoint Map(double lx, double ly) => new(cx + lx * cos - ly * sin, cy + lx * sin + ly * cos);
        return new[]
        {
            Map(-halfWidth, -halfHeight),
            Map(halfWidth, -halfHeight),
            Map(halfWidth, halfHeight),
            Map(-halfWidth, halfHeight),
        };
    }

    private static OcrResult Result(params OcrLine[] lines) => new()
    {
        FullText = string.Join('\n', lines.Select(l => l.Text)),
        Lines = lines,
        Languages = new[] { "en" },
    };

    private static OcrLine Line(string text, IReadOnlyList<OcrPoint> polygon, params OcrWord[] words) => new()
    {
        Text = text,
        Confidence = 0.9,
        BoundingPolygon = polygon,
        BoundingBox = OcrBoundingBox.FromPoints(polygon),
        Words = words,
    };

    private static OcrWord Word(string text, double x0, double y0, double x1, double y1)
    {
        var poly = Rect(x0, y0, x1, y1);
        return new OcrWord { Text = text, Confidence = 0.9, BoundingPolygon = poly, BoundingBox = OcrBoundingBox.FromPoints(poly) };
    }

    // ---- padding math ----

    [Fact]
    public void Padding_grows_an_axis_aligned_quad_by_a_fraction_of_its_height()
    {
        var quad = Rect(50, 50, 150, 90);                       // 100 × 40
        var padded = RedactionEngine.Expand(quad, 0.25);        // pad = 0.25 × 40 = 10

        Assert.Equal(40, padded[0].X, 6);
        Assert.Equal(40, padded[0].Y, 6);
        Assert.Equal(160, padded[1].X, 6);
        Assert.Equal(40, padded[1].Y, 6);
        Assert.Equal(160, padded[2].X, 6);
        Assert.Equal(100, padded[2].Y, 6);
        Assert.Equal(40, padded[3].X, 6);
        Assert.Equal(100, padded[3].Y, 6);
    }

    [Fact]
    public void Padding_of_zero_leaves_the_quad_alone()
    {
        var quad = Rect(10, 10, 40, 30);
        var padded = RedactionEngine.Expand(quad, 0);
        Assert.Equal(quad, padded);
    }

    [Fact]
    public void Padding_follows_a_rotated_quads_own_axes()
    {
        // A quad rotated 90°: its "height" axis now runs horizontally, so padding must move the corners
        // along that rotated axis, not along the image axes.
        var quad = RotatedRect(100, 100, 50, 10, 90);
        var padded = RedactionEngine.Expand(quad, 0.5);         // pad = 0.5 × 20 = 10

        // Every corner moves diagonally outward by pad along both local axes, so the distance from the
        // centre grows by exactly sqrt(2)·pad in the rotated frame: local (±60, ±20) instead of (±50, ±10).
        for (int i = 0; i < 4; i++)
        {
            double dx = padded[i].X - 100, dy = padded[i].Y - 100;
            double local = Math.Sqrt(dx * dx + dy * dy);
            Assert.Equal(Math.Sqrt(60 * 60 + 20 * 20), local, 4);
        }
    }

    // ---- rotated-quad fill ----

    [Fact]
    public void FilledBox_paints_inside_the_rotated_quad_and_nothing_outside()
    {
        using var image = Canvas(200, 200);
        var quad = RotatedRect(100, 100, 60, 20, 30);
        var ocr = Result(Line("SECRET", quad));

        using var redacted = ocr.Redact(image, new RedactionOptions
        {
            Keywords = new[] { "SECRET" },
            Padding = 0,
        });

        // Centre and a point near one rotated end are covered...
        Assert.Equal(Black, redacted.Image[100, 100]);
        Assert.Equal(Black, redacted.Image[148, 128]);          // local (55, 0) rotated by 30°

        // ...while a corner of the axis-aligned bounding box, which lies outside the rotated quad, is not.
        // That corner would be blacked out by any implementation that filled the bounding box instead.
        Assert.Equal(White, redacted.Image[39, 54]);
        Assert.Equal(White, redacted.Image[161, 146]);
        Assert.Equal(White, redacted.Image[5, 5]);
    }

    [Fact]
    public void Redaction_never_touches_the_source_image()
    {
        using var image = Canvas(120, 60);
        var ocr = Result(Line("SECRET", Rect(10, 10, 110, 50)));

        using var redacted = ocr.Redact(image, new RedactionOptions { Keywords = new[] { "SECRET" } });

        Assert.Equal(Black, redacted.Image[60, 30]);
        Assert.Equal(White, image[60, 30]);
        Assert.NotSame(image, redacted.Image);
    }

    [Fact]
    public void Padding_covers_pixels_outside_the_reported_quad()
    {
        using var image = Canvas(200, 120);
        var ocr = Result(Line("SECRET", Rect(50, 50, 150, 90)));   // height 40 ⇒ pad 10 at 0.25

        using var padded = ocr.Redact(image, new RedactionOptions { Keywords = new[] { "SECRET" }, Padding = 0.25 });
        using var unpadded = ocr.Redact(image, new RedactionOptions { Keywords = new[] { "SECRET" }, Padding = 0 });

        Assert.Equal(Black, padded.Image[40, 40]);     // inside the padded quad only
        Assert.Equal(White, unpadded.Image[40, 40]);
        Assert.Equal(White, padded.Image[39, 39]);     // just outside the padded quad
    }

    [Fact]
    public void Custom_fill_colour_is_used()
    {
        using var image = Canvas(60, 40);
        var ocr = Result(Line("SECRET", Rect(5, 5, 55, 35)));
        var red = new Rgb24(255, 0, 0);

        using var redacted = ocr.Redact(image, new RedactionOptions { Keywords = new[] { "SECRET" }, FillColor = red, Padding = 0 });

        Assert.Equal(red, redacted.Image[30, 20]);
    }

    // ---- blur / pixelate ----

    [Fact]
    public void Blur_alters_the_region_and_leaves_the_rest_alone()
    {
        using var image = Canvas(100, 60);
        for (int y = 20; y < 40; y++)
        {
            for (int x = 30; x < 50; x++) image[x, y] = Black;   // a "glyph" to smear
        }

        var ocr = Result(Line("SECRET", Rect(10, 10, 90, 50)));
        using var redacted = ocr.Redact(image, new RedactionOptions
        {
            Keywords = new[] { "SECRET" },
            Style = RedactionStyle.Blur,
            BlurRadius = 6,
            Padding = 0,
        });

        // The glyph edge is no longer either pure black or pure white.
        var edge = redacted.Image[30, 30];
        Assert.True(edge.R is > 0 and < 255, $"expected a blended pixel at the glyph edge, got {edge.R}");

        // White just inside the region next to the glyph picked up some of it.
        var nearby = redacted.Image[27, 30];
        Assert.True(nearby.R < 255, "expected the blur to bleed the glyph into neighbouring pixels");

        // Outside the polygon nothing moved.
        Assert.Equal(White, redacted.Image[5, 5]);
        Assert.Equal(White, redacted.Image[95, 55]);
    }

    [Fact]
    public void Pixelate_replaces_the_region_with_uniform_blocks()
    {
        using var image = Canvas(100, 60);
        for (int y = 0; y < 60; y++)
        {
            for (int x = 0; x < 100; x++)
            {
                if (((x + y) & 1) == 0) image[x, y] = Black;     // checkerboard: every block averages to grey
            }
        }

        var ocr = Result(Line("SECRET", Rect(0, 0, 100, 60)));
        using var redacted = ocr.Redact(image, new RedactionOptions
        {
            Keywords = new[] { "SECRET" },
            Style = RedactionStyle.Pixelate,
            PixelateBlockSize = 10,
            Padding = 0,
        });

        var block = redacted.Image[10, 10];
        Assert.True(block.R is > 0 and < 255, $"expected a mosaic average, got {block.R}");
        for (int y = 10; y < 20; y++)
        {
            for (int x = 10; x < 20; x++)
            {
                Assert.Equal(block, redacted.Image[x, y]);
            }
        }
    }

    // ---- what gets covered ----

    [Fact]
    public void Word_scope_covers_only_the_matched_word()
    {
        using var image = Canvas(300, 60);
        var line = Line("Card 4111111111111111 end", Rect(10, 10, 250, 40),
            Word("Card", 10, 10, 50, 40),
            Word("4111111111111111", 60, 10, 200, 40),
            Word("end", 210, 10, 250, 40));

        using var redacted = Result(line).Redact(image, new RedactionOptions
        {
            Rules = new[] { RedactionPatterns.CreditCard },
            Scope = RedactionScope.MatchedWords,
            Padding = 0,
        });

        Assert.Equal(Black, redacted.Image[130, 25]);   // the card number
        Assert.Equal(White, redacted.Image[30, 25]);    // "Card" survives
        Assert.Equal(White, redacted.Image[230, 25]);   // "end" survives

        var entry = Assert.Single(redacted.Redactions);
        Assert.False(entry.WholeLineRedacted);
        Assert.Single(entry.RedactedPolygons);
        Assert.Equal(RedactionMatchKind.Rule, entry.Matches[0].Kind);
        Assert.Equal(nameof(RedactionPatterns.CreditCard), entry.Matches[0].Rule);
    }

    [Fact]
    public void Word_scope_falls_back_to_the_whole_line_when_word_geometry_is_missing()
    {
        using var image = Canvas(300, 60);
        // No Words: the default for every result recognized without WordLevelDetail.
        var line = Line("Card 4111111111111111 end", Rect(10, 10, 250, 40));

        using var redacted = Result(line).Redact(image, new RedactionOptions
        {
            Rules = new[] { RedactionPatterns.CreditCard },
            Scope = RedactionScope.MatchedWords,
            Padding = 0,
        });

        Assert.Equal(Black, redacted.Image[130, 25]);
        Assert.Equal(Black, redacted.Image[30, 25]);    // the fallback redacts strictly more, never less
        Assert.True(Assert.Single(redacted.Redactions).WholeLineRedacted);
    }

    [Fact]
    public void Word_scope_falls_back_when_the_word_list_disagrees_with_the_text()
    {
        using var image = Canvas(300, 60);
        // Three whitespace tokens but only two words: the mapping is untrustworthy, so cover everything.
        var line = Line("Card 4111111111111111 end", Rect(10, 10, 250, 40),
            Word("Card", 10, 10, 50, 40),
            Word("4111111111111111", 60, 10, 200, 40));

        using var redacted = Result(line).Redact(image, new RedactionOptions
        {
            Rules = new[] { RedactionPatterns.CreditCard },
            Scope = RedactionScope.MatchedWords,
            Padding = 0,
        });

        Assert.True(Assert.Single(redacted.Redactions).WholeLineRedacted);
        Assert.Equal(Black, redacted.Image[230, 25]);
    }

    [Fact]
    public void Predicate_always_covers_the_whole_line()
    {
        using var image = Canvas(300, 60);
        var line = Line("Card 4111111111111111 end", Rect(10, 10, 250, 40),
            Word("Card", 10, 10, 50, 40),
            Word("4111111111111111", 60, 10, 200, 40),
            Word("end", 210, 10, 250, 40));

        using var redacted = Result(line).Redact(image, new RedactionOptions
        {
            LinePredicate = l => l.BoundingBox.MinY < 20,
            Scope = RedactionScope.MatchedWords,
            Padding = 0,
        });

        var entry = Assert.Single(redacted.Redactions);
        Assert.True(entry.WholeLineRedacted);
        Assert.Equal(RedactionMatchKind.Predicate, entry.Matches[0].Kind);
        Assert.Equal(Black, redacted.Image[30, 25]);
        Assert.Equal(Black, redacted.Image[230, 25]);
    }

    [Fact]
    public void Keywords_are_case_insensitive_by_default_and_can_be_made_strict()
    {
        using var image = Canvas(120, 60);
        var ocr = Result(Line("Confidential", Rect(10, 10, 110, 50)));

        using var loose = ocr.Redact(image, new RedactionOptions { Keywords = new[] { "CONFIDENTIAL" } });
        using var strict = ocr.Redact(image, new RedactionOptions { Keywords = new[] { "CONFIDENTIAL" }, CaseSensitiveKeywords = true });

        Assert.True(loose.AnyRedactions);
        Assert.False(strict.AnyRedactions);
    }

    [Fact]
    public void Ad_hoc_patterns_are_reported_with_their_pattern_text()
    {
        using var image = Canvas(120, 60);
        var ocr = Result(Line("case ABC-99", Rect(10, 10, 110, 50)));

        using var redacted = ocr.Redact(image, new RedactionOptions
        {
            Patterns = new[] { new Regex(@"[A-Z]{3}-\d{2}") },
        });

        var match = Assert.Single(Assert.Single(redacted.Redactions).Matches);
        Assert.Equal(RedactionMatchKind.Pattern, match.Kind);
        Assert.Equal("ABC-99", match.Text);
        Assert.Equal(@"[A-Z]{3}-\d{2}", match.Rule);
        Assert.Equal(5, match.Index);
    }

    [Fact]
    public void Nothing_is_redacted_without_selectors()
    {
        using var image = Canvas(120, 60);
        var ocr = Result(Line("4111111111111111", Rect(10, 10, 110, 50)));

        using var redacted = ocr.Redact(image, RedactionOptions.Default);

        Assert.False(redacted.AnyRedactions);
        Assert.Equal(0, redacted.RedactedRegionCount);
        Assert.Equal(White, redacted.Image[60, 30]);
        Assert.Equal("4111111111111111", redacted.SanitizedText);
    }

    // ---- audit trail ----

    [Fact]
    public void SanitizedText_masks_the_matches_and_keeps_the_rest()
    {
        using var image = Canvas(200, 100);
        var ocr = Result(
            Line("SSN 123-45-6789 filed", Rect(10, 10, 190, 40)),
            Line("nothing sensitive", Rect(10, 50, 190, 80)));

        using var redacted = ocr.Redact(image, new RedactionOptions
        {
            Rules = new[] { RedactionPatterns.UsSocialSecurityNumber },
        });

        Assert.DoesNotContain("123-45-6789", redacted.SanitizedText, StringComparison.Ordinal);
        Assert.Contains(new string('█', 11), redacted.SanitizedText, StringComparison.Ordinal);
        Assert.Contains("SSN ", redacted.SanitizedText, StringComparison.Ordinal);
        Assert.Contains("nothing sensitive", redacted.SanitizedText, StringComparison.Ordinal);
        Assert.Single(redacted.Redactions);
        Assert.Equal(1, redacted.RedactedRegionCount);
    }

    [Fact]
    public void Mask_character_is_configurable()
    {
        using var image = Canvas(200, 60);
        var ocr = Result(Line("SSN 123-45-6789", Rect(10, 10, 190, 40)));

        using var redacted = ocr.Redact(image, new RedactionOptions
        {
            Rules = new[] { RedactionPatterns.UsSocialSecurityNumber },
            MaskCharacter = '*',
        });

        Assert.Equal("SSN ***********", redacted.SanitizedText);
    }

    // ---- options ----

    [Fact]
    public void Word_scope_raises_the_recognition_detail_it_needs()
    {
        var options = new RedactionOptions { Scope = RedactionScope.MatchedWords };
        Assert.Equal(WordLevelDetail.None, options.Recognition.WordLevelDetail);
        Assert.Equal(WordLevelDetail.Words, options.EffectiveRecognition.WordLevelDetail);

        // ...but never lowers a caller's own choice, and never mutates the instance passed in.
        var caller = new RecognitionOptions { WordLevelDetail = WordLevelDetail.Characters, BeamWidth = 9 };
        var withCharacters = new RedactionOptions { Scope = RedactionScope.MatchedWords, Recognition = caller };
        Assert.Equal(WordLevelDetail.Characters, withCharacters.EffectiveRecognition.WordLevelDetail);
        Assert.Equal(9, withCharacters.EffectiveRecognition.BeamWidth);
        Assert.Equal(WordLevelDetail.Characters, caller.WordLevelDetail);

        // Line scope leaves recognition exactly as supplied.
        var lineScope = new RedactionOptions();
        Assert.Same(lineScope.Recognition, lineScope.EffectiveRecognition);
    }

    [Theory]
    [InlineData(0, 12, 0.1)]
    [InlineData(4, 0, 0.1)]
    [InlineData(4, 12, -0.1)]
    public void Invalid_options_are_rejected(int blurRadius, int blockSize, double padding)
    {
        using var image = Canvas(20, 20);
        var ocr = Result(Line("x", Rect(1, 1, 19, 19)));
        var options = new RedactionOptions { BlurRadius = blurRadius, PixelateBlockSize = blockSize, Padding = padding };

        Assert.Throws<ArgumentOutOfRangeException>(() => ocr.Redact(image, options));
    }

    [Fact]
    public void Degenerate_geometry_is_recorded_but_paints_nothing()
    {
        using var image = Canvas(40, 40);
        var line = new OcrLine { Text = "SECRET", Confidence = 0.5 };   // no polygon, no box

        using var redacted = Result(line).Redact(image, new RedactionOptions { Keywords = new[] { "SECRET" } });

        var entry = Assert.Single(redacted.Redactions);
        Assert.Empty(entry.RedactedPolygons);
        Assert.Equal(White, redacted.Image[20, 20]);
    }
}

/// <summary>
/// End-to-end redaction over the real OCR engine. Tagged "Integration" (models are downloaded on first
/// run) and skipped when the fixture is absent.
/// </summary>
[Trait("Category", "Integration")]
[Collection(OcrIntegrationCollection.Name)]
public class RedactionIntegrationTests
{
    [SkippableFact]
    public async Task RedactAsync_blacks_out_a_keyword_on_a_real_image()
    {
        var sample = TestAssets.Image("sample.png");
        Skip.If(sample is null, "assets/sample.png not found.");

        await using var ocr = new EasyOcrService();
        using var original = await Image.LoadAsync<Rgb24>(sample!);
        using var redacted = await ocr.RedactAsync(sample!, new[] { "en" }, new RedactionOptions
        {
            Keywords = new[] { "Hello" },
        });

        Assert.True(redacted.AnyRedactions, "expected the word 'Hello' to be found and redacted.");
        Assert.DoesNotContain("Hello", redacted.SanitizedText, StringComparison.OrdinalIgnoreCase);

        int changed = 0;
        for (int y = 0; y < original.Height; y++)
        {
            for (int x = 0; x < original.Width; x++)
            {
                if (original[x, y] != redacted.Image[x, y]) changed++;
            }
        }
        Assert.True(changed > 0, "expected the redacted image to differ from the original.");
    }
}
