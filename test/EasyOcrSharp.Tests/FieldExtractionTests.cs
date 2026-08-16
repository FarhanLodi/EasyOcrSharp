using System.Text.RegularExpressions;
using EasyOcrSharp.Extraction;
using EasyOcrSharp.Models;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Unit tests for anchor-based field extraction (feature #8). Everything here is pure geometry over a
/// hand-built <see cref="OcrResult"/>, so the whole file is CI-safe: no models, no network, no I/O.
/// </summary>
/// <remarks>
/// The fixtures are laid out on a 20 px line height so every distance in the assertions can be read as a
/// whole number of line-heights — the unit the whole API is expressed in.
/// </remarks>
public partial class FieldExtractionTests
{
    // ---- fixtures ----

    /// <summary>Builds a recognized line with an axis-aligned box at (x, y) of the given size.</summary>
    private static OcrLine Line(string text, double x, double y, double width, double height, double confidence = 0.9)
        => new()
        {
            Text = text,
            Confidence = confidence,
            BoundingBox = new OcrBoundingBox(x, y, x + width, y + height),
            BoundingPolygon = new[]
            {
                new OcrPoint(x, y), new OcrPoint(x + width, y),
                new OcrPoint(x + width, y + height), new OcrPoint(x, y + height),
            },
        };

    /// <summary>Builds a recognized line with no geometry at all — the degraded case the inline path must survive.</summary>
    private static OcrLine TextOnly(string text, double confidence = 0.9)
        => new() { Text = text, Confidence = confidence };

    private static OcrResult Page(params OcrLine[] lines)
        => new()
        {
            FullText = string.Join('\n', lines.Select(l => l.Text)),
            Lines = lines,
            Languages = new[] { "en" },
        };

    /// <summary>Multiplies every coordinate by <paramref name="factor"/> — a different scan resolution.</summary>
    private static OcrResult Scale(OcrResult result, double factor)
        => result with
        {
            Lines = result.Lines.Select(line => line with
            {
                BoundingBox = new OcrBoundingBox(
                    line.BoundingBox.MinX * factor,
                    line.BoundingBox.MinY * factor,
                    line.BoundingBox.MaxX * factor,
                    line.BoundingBox.MaxY * factor),
                BoundingPolygon = line.BoundingPolygon
                    .Select(p => new OcrPoint(p.X * factor, p.Y * factor))
                    .ToArray(),
            }).ToArray(),
        };

    /// <summary>
    /// A label with one candidate in each of the four geometric directions, every one of them exactly two
    /// line-heights away and dead on axis, so only the direction under test can decide the winner.
    /// </summary>
    private static OcrResult Cross(string anchorText = "Total:") => Page(
        Line("ABOVE1", 100, 40, 60, 20),     // (100, 40) – (160, 60)
        Line("LEFT1", 20, 100, 40, 20),      // ( 20,100) – ( 60,120)
        Line(anchorText, 100, 100, 60, 20),  // (100,100) – (160,120)
        Line("RIGHT1", 200, 100, 60, 20),    // (200,100) – (260,120)
        Line("BELOW1", 100, 160, 60, 20));   // (100,160) – (160,180)

    private static FieldDefinition Total(FieldDirection direction) => new()
    {
        Name = "Total",
        Anchors = new[] { "Total" },
        Direction = direction,
    };

    private static ExtractedField Single(OcrResult page, FieldDefinition definition, FieldExtractionOptions? options = null)
        => Assert.Single(page.ExtractFields(new[] { definition }, options));

    [GeneratedRegex(@"\d+\.\d{2}", RegexOptions.CultureInvariant)]
    private static partial Regex MoneyRegex();

    [GeneratedRegex(@"SN\s*(?<value>\d{3,})", RegexOptions.CultureInvariant)]
    private static partial Regex SerialRegex();

    [GeneratedRegex(@"Ref\s*[:#]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RefAnchorRegex();

    // ---- directions ----

    [Fact]
    public void RightDirectionTakesTheValueBesideTheLabelAndIgnoresEveryOtherNeighbour()
    {
        var field = Single(Cross(), Total(FieldDirection.Right));

        Assert.Equal("RIGHT1", field.Value);
        Assert.Equal(FieldMatchKind.Right, field.MatchKind);
        Assert.Equal(2.0, field.Distance, 6);
    }

    [Fact]
    public void BelowDirectionTakesTheValueUnderTheLabelAndIgnoresEveryOtherNeighbour()
    {
        var field = Single(Cross(), Total(FieldDirection.Below));

        Assert.Equal("BELOW1", field.Value);
        Assert.Equal(FieldMatchKind.Below, field.MatchKind);
        Assert.Equal(2.0, field.Distance, 6);
    }

    [Fact]
    public void LeftDirectionTakesTheValueBeforeTheLabelAndIgnoresEveryOtherNeighbour()
    {
        var field = Single(Cross(), Total(FieldDirection.Left));

        Assert.Equal("LEFT1", field.Value);
        Assert.Equal(FieldMatchKind.Left, field.MatchKind);
        Assert.Equal(2.0, field.Distance, 6);
    }

    [Fact]
    public void AboveDirectionTakesTheValueOverTheLabelAndIgnoresEveryOtherNeighbour()
    {
        var field = Single(Cross(), Total(FieldDirection.Above));

        Assert.Equal("ABOVE1", field.Value);
        Assert.Equal(FieldMatchKind.Above, field.MatchKind);
        Assert.Equal(2.0, field.Distance, 6);
    }

    [Fact]
    public void SameLineDirectionTakesTheRemainderOfTheLabelLineAndIgnoresGeometryEntirely()
    {
        var field = Single(Cross("Total: INLINE1"), Total(FieldDirection.SameLine));

        Assert.Equal("INLINE1", field.Value);
        Assert.Equal(FieldMatchKind.Inline, field.MatchKind);
        Assert.Equal(0.0, field.Distance, 6);
    }

    [Fact]
    public void InlineBeatsRightWhichBeatsBelowWhenEverythingElseIsEqual()
    {
        var page = Cross("Total: INLINE1");

        // All five candidates are legal here; the per-direction bias breaks the tie.
        var everywhere = Single(page, Total(FieldDirection.All));
        Assert.Equal("INLINE1", everywhere.Value);

        // Drop the inline path and the value beside the label wins over the one under it.
        var geometric = Single(page, Total(FieldDirection.Horizontal | FieldDirection.Vertical));
        Assert.Equal("RIGHT1", geometric.Value);
        Assert.Equal(FieldMatchKind.Right, geometric.MatchKind);
        Assert.True(everywhere.Score > geometric.Score, "an inline value should outscore an equivalent one to the right");
    }

    [Fact]
    public void NoneDirectionMatchesNothing()
    {
        Assert.Empty(Cross("Total: INLINE1").ExtractFields(new[] { Total(FieldDirection.None) }));
    }

    // ---- MaxDistance ----

    [Fact]
    public void MaxDistanceIncludesACandidateExactlyOnTheBoundAndExcludesOneBeyondIt()
    {
        var definition = Total(FieldDirection.Right) with { MaxDistance = 3.0 };

        // Gap of 60 px over a 20 px line height = exactly 3.0 line-heights.
        var onBound = Page(
            Line("Total:", 100, 100, 60, 20),
            Line("42.00", 220, 100, 60, 20));
        Assert.Equal("42.00", Single(onBound, definition).Value);

        // Gap of 80 px = 4.0 line-heights, one past the bound.
        var beyond = Page(
            Line("Total:", 100, 100, 60, 20),
            Line("42.00", 240, 100, 60, 20));
        Assert.Empty(beyond.ExtractFields(new[] { definition }));
    }

    [Fact]
    public void MaxDistanceIsResolutionIndependent()
    {
        var definition = Total(FieldDirection.Right) with { MaxDistance = 3.0 };
        var page = Page(
            Line("Total:", 100, 100, 60, 20),
            Line("42.00", 220, 100, 60, 20));

        var atOneX = Single(page, definition);
        var atThreeX = Single(Scale(page, 3.0), definition);

        Assert.Equal(atOneX.Value, atThreeX.Value);
        Assert.Equal(atOneX.Distance, atThreeX.Distance, 6);
        Assert.Equal(atOneX.Score, atThreeX.Score, 6);

        // The same 3x scan with the value one line-height further out is still rejected, so the bound
        // really is measured in line-heights and not in pixels.
        var tooFar = Scale(
            Page(Line("Total:", 100, 100, 60, 20), Line("42.00", 240, 100, 60, 20)),
            3.0);
        Assert.Empty(tooFar.ExtractFields(new[] { definition }));
    }

    // ---- SearchBand ----

    [Fact]
    public void SearchBandRejectsARightCandidateWhoseBaselineIsOffAxis()
    {
        var definition = Total(FieldDirection.Right);

        // Centre-y offset of 30 px = 1.5 line-heights, well past the 0.75 default band.
        var offBand = Page(
            Line("Total:", 100, 100, 60, 20),
            Line("42.00", 200, 130, 60, 20));
        Assert.Empty(offBand.ExtractFields(new[] { definition }));

        // 10 px = 0.5 line-heights, inside the band.
        var inBand = Page(
            Line("Total:", 100, 100, 60, 20),
            Line("42.00", 200, 110, 60, 20));
        Assert.Equal("42.00", Single(inBand, definition).Value);

        // Widening the band recovers the rejected candidate.
        Assert.Equal("42.00", Single(offBand, definition with { SearchBand = 2.0 }).Value);
    }

    [Fact]
    public void SearchBandRejectsABelowCandidateWhoseColumnDoesNotOverlapTheLabel()
    {
        var definition = Total(FieldDirection.Below);

        // x-ranges 40 px apart = 2.0 line-heights of horizontal gap.
        var offBand = Page(
            Line("Total:", 100, 100, 60, 20),
            Line("42.00", 200, 160, 60, 20));
        Assert.Empty(offBand.ExtractFields(new[] { definition }));

        // 10 px of gap = 0.5 line-heights, inside the band.
        var inBand = Page(
            Line("Total:", 100, 100, 60, 20),
            Line("42.00", 170, 160, 60, 20));
        var field = Single(inBand, definition);
        Assert.Equal("42.00", field.Value);
        Assert.Equal(FieldMatchKind.Below, field.MatchKind);
    }

    // ---- ValuePattern ----

    [Fact]
    public void ValuePatternLetsAFartherMatchingCandidateBeatANearerFailingOne()
    {
        var page = Page(
            Line("Total:", 100, 100, 60, 20),   // (100,100) – (160,120)
            Line("N/A", 180, 100, 40, 20),      // 1.0 line-heights away
            Line("42.00", 300, 100, 60, 20));   // 7.0 line-heights away

        var loose = Total(FieldDirection.Right) with { MaxDistance = 12.0 };
        Assert.Equal("N/A", Single(page, loose).Value);

        var constrained = loose with { ValuePattern = MoneyRegex() };
        var field = Single(page, constrained);
        Assert.Equal("42.00", field.Value);
        Assert.Equal(7.0, field.Distance, 6);
    }

    [Fact]
    public void ValuePatternWithANamedValueGroupYieldsOnlyThatGroup()
    {
        var page = Page(Line("Serial: SN 4477", 100, 100, 160, 20));
        var definition = new FieldDefinition
        {
            Name = "Serial",
            Anchors = new[] { "Serial" },
            Direction = FieldDirection.SameLine,
            ValuePattern = SerialRegex(),
        };

        Assert.Equal("4477", Single(page, definition).Value);
    }

    [Fact]
    public void ValuePatternThatMatchesNothingLeavesTheFieldUnextracted()
    {
        var page = Page(Line("Total: not a number", 100, 100, 200, 20));
        var definition = Total(FieldDirection.SameLine) with { ValuePattern = MoneyRegex() };

        Assert.Empty(page.ExtractFields(new[] { definition }));
    }

    // ---- fuzzy anchor matching ----

    [Theory]
    [InlineData("Totai: 42.00")]   // l -> i
    [InlineData("T0tal: 42.00")]   // o -> 0
    [InlineData("TOTAL : 42.00")]  // case and spacing
    [InlineData("total- 42.00")]   // punctuation
    public void DamagedLabelsStillMatchTheAnchor(string text)
    {
        var field = Single(Page(TextOnly(text)), Total(FieldDirection.SameLine));

        Assert.Equal("42.00", field.Value);
        Assert.Equal("Total", field.MatchedAnchor);
        Assert.InRange(field.AnchorSimilarity, 0.72, 1.0);
    }

    [Fact]
    public void FuzzyMatchingReportsLessThanPerfectSimilarityForADamagedLabel()
    {
        var damaged = Single(Page(TextOnly("Totai: 42.00")), Total(FieldDirection.SameLine));
        var clean = Single(Page(TextOnly("Total: 42.00")), Total(FieldDirection.SameLine));

        Assert.Equal(1.0, clean.AnchorSimilarity, 6);
        Assert.True(damaged.AnchorSimilarity < clean.AnchorSimilarity);
    }

    [Theory]
    [InlineData("Notes: 42.00")]
    [InlineData("Tenant: 42.00")]
    [InlineData("Postal: 42.00")]
    public void GenuinelyDifferentWordsDoNotMatchTheAnchor(string text)
    {
        Assert.Empty(Page(TextOnly(text)).ExtractFields(new[] { Total(FieldDirection.SameLine) }));
    }

    [Fact]
    public void MinAnchorSimilarityOnTheDefinitionOverridesTheGlobalOption()
    {
        var page = Page(TextOnly("Totai: 42.00"));

        Assert.Single(page.ExtractFields(new[] { Total(FieldDirection.SameLine) }));
        Assert.Empty(page.ExtractFields(new[] { Total(FieldDirection.SameLine) with { MinAnchorSimilarity = 0.95 } }));
    }

    // ---- inline fallback ----

    [Fact]
    public void InlineFallbackExtractsAValueFromASingleLineWithNoGeometry()
    {
        var field = Single(Page(TextOnly("Total: 42.00")), FieldDefinition.Create("Total", "Total"));

        Assert.Equal("42.00", field.Value);
        Assert.Equal(FieldMatchKind.Inline, field.MatchKind);
        Assert.Equal(0.0, field.Distance, 6);
        Assert.True(field.BoundingBox.IsEmpty);
        Assert.Single(field.ValueLines);
        Assert.Same(field.AnchorLine, field.ValueLines[0]);
    }

    [Theory]
    [InlineData("Total: 42.00")]
    [InlineData("Total = 42.00")]
    [InlineData("Total .... 42.00")]
    [InlineData("Total # 42.00")]
    [InlineData("Total | 42.00")]
    public void InlineSeparatorsAreStrippedFromTheFrontOfTheValue(string text)
    {
        Assert.Equal("42.00", Single(Page(TextOnly(text)), FieldDefinition.Create("Total", "Total")).Value);
    }

    [Fact]
    public void InlineValuesCanBeTurnedOffEntirely()
    {
        var page = Page(Line("Total: 42.00", 100, 100, 120, 20));
        var options = FieldExtractionOptions.Default with { AllowInlineValues = false };

        Assert.Single(page.ExtractFields(new[] { FieldDefinition.Create("Total", "Total") }));
        Assert.Empty(page.ExtractFields(new[] { FieldDefinition.Create("Total", "Total") }, options));
    }

    [Fact]
    public void ALabelWithNothingAfterItProducesNoInlineValue()
    {
        Assert.Empty(Page(TextOnly("Total:")).ExtractFields(new[] { Total(FieldDirection.SameLine) }));
    }

    [Fact]
    public void InlineExtractionHandlesNonAsciiLabelsAndValues()
    {
        var definition = new FieldDefinition
        {
            Name = "Ödeme",
            Anchors = new[] { "Ödeme" },
            Direction = FieldDirection.SameLine,
        };

        var field = Single(Page(TextOnly("Ödeme: ₺45,00")), definition);

        Assert.Equal("₺45,00", field.Value);
        Assert.Equal(1.0, field.AnchorSimilarity, 6);
    }

    // ---- anchor patterns ----

    [Fact]
    public void AnAnchorPatternMatchesAsAPerfectHitAndReportsTheMatchedText()
    {
        var definition = new FieldDefinition
        {
            Name = "Reference",
            AnchorPatterns = new[] { RefAnchorRegex() },
            Direction = FieldDirection.SameLine,
        };

        var field = Single(Page(TextOnly("Ref: XY-77")), definition);

        Assert.Equal("XY-77", field.Value);
        Assert.Equal("Ref:", field.MatchedAnchor);
        Assert.Equal(1.0, field.AnchorSimilarity, 6);
    }

    [Fact]
    public void ADefinitionWithNeitherAnchorsNorPatternsMatchesNothing()
    {
        var definition = new FieldDefinition { Name = "Nothing" };

        Assert.Empty(Page(TextOnly("Total: 42.00")).ExtractFields(new[] { definition }));
    }

    // ---- occurrence ----

    private static OcrResult ThreeTotals() => Page(
        TextOnly("Totai: 10.00"),   // damaged label
        TextOnly("Total: 20.00"),   // exact label
        TextOnly("T0tal: 30.00"));  // damaged label

    [Fact]
    public void OccurrenceFirstTakesTheEarliestAnchorInReadingOrder()
    {
        var definition = Total(FieldDirection.SameLine) with { Occurrence = FieldOccurrence.First };

        Assert.Equal("10.00", Single(ThreeTotals(), definition).Value);
    }

    [Fact]
    public void OccurrenceLastTakesTheFinalAnchorInReadingOrder()
    {
        var definition = Total(FieldDirection.SameLine) with { Occurrence = FieldOccurrence.Last };

        Assert.Equal("30.00", Single(ThreeTotals(), definition).Value);
    }

    [Fact]
    public void OccurrenceBestTakesTheHighestScoringAnchorRegardlessOfPosition()
    {
        var definition = Total(FieldDirection.SameLine) with { Occurrence = FieldOccurrence.Best };
        var field = Single(ThreeTotals(), definition);

        Assert.Equal("20.00", field.Value);
        Assert.Equal(1.0, field.AnchorSimilarity, 6);
        Assert.Equal(FieldOccurrence.Best, FieldDefinition.Create("x", "y").Occurrence);
    }

    // ---- scoring ----

    [Fact]
    public void ScoreIsHigherForABetterAnchorMatch()
    {
        var page = Page(TextOnly("Total: 42.00"));
        var exact = Single(page, Total(FieldDirection.SameLine));
        var fuzzy = Single(page, new FieldDefinition
        {
            Name = "Total",
            Anchors = new[] { "Totel" },
            Direction = FieldDirection.SameLine,
        });

        Assert.Equal(exact.Value, fuzzy.Value);
        Assert.True(exact.Score > fuzzy.Score, "an exact label match must outscore a fuzzy one");
        Assert.InRange(exact.Score, 0.0, 1.0);
        Assert.InRange(fuzzy.Score, 0.0, 1.0);
    }

    [Fact]
    public void ScoreIsHigherForACloserValue()
    {
        var definition = Total(FieldDirection.Right) with { MaxDistance = 12.0 };
        var near = Single(Page(Line("Total:", 100, 100, 60, 20), Line("42.00", 180, 100, 60, 20)), definition);
        var far = Single(Page(Line("Total:", 100, 100, 60, 20), Line("42.00", 300, 100, 60, 20)), definition);

        Assert.True(near.Distance < far.Distance);
        Assert.True(near.Score > far.Score, "a closer value must outscore a more distant one");
    }

    [Fact]
    public void MinScoreDiscardsAWeakButOtherwiseLegalMatch()
    {
        var page = Page(TextOnly("Totai: 42.00"));
        var definition = Total(FieldDirection.SameLine);

        var accepted = Single(page, definition);
        Assert.InRange(accepted.Score, 0.35, 0.999);

        var strict = FieldExtractionOptions.Default with { MinScore = 0.999 };
        Assert.Empty(page.ExtractFields(new[] { definition }, strict));
    }

    [Fact]
    public void ExplanationMentionsTheAnchorTheDirectionAndTheScore()
    {
        var field = Single(Page(TextOnly("Total: 42.00")), Total(FieldDirection.SameLine));

        Assert.Contains("Total", field.Explanation, StringComparison.Ordinal);
        Assert.Contains("Inline", field.Explanation, StringComparison.Ordinal);
        Assert.Contains("score", field.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfidenceIsTheMeanOfTheValueLinesNotTheAnchorLine()
    {
        var page = Page(
            Line("Total:", 100, 100, 60, 20, confidence: 0.10),
            Line("42.00", 200, 100, 60, 20, confidence: 0.80));

        var field = Single(page, Total(FieldDirection.Right));

        Assert.Equal(0.80, field.Confidence, 6);
    }

    // ---- multi-line values ----

    private static OcrResult BillTo() => Page(
        Line("Bill To:", 100, 100, 80, 20),
        Line("Jane Doe", 100, 130, 100, 20, confidence: 0.8),
        Line("12 Main St", 100, 160, 110, 20, confidence: 0.6));

    [Fact]
    public void MaxValueLinesDefaultsToASingleLine()
    {
        var definition = new FieldDefinition
        {
            Name = "BillTo",
            Anchors = new[] { "Bill To" },
            Direction = FieldDirection.Below,
        };

        var field = Single(BillTo(), definition);

        Assert.Equal("Jane Doe", field.Value);
        Assert.Single(field.ValueLines);
    }

    [Fact]
    public void MaxValueLinesJoinsConsecutiveLinesBelowTheLabel()
    {
        var definition = new FieldDefinition
        {
            Name = "BillTo",
            Anchors = new[] { "Bill To" },
            Direction = FieldDirection.Below,
            MaxValueLines = 2,
        };

        var field = Single(BillTo(), definition);

        Assert.Equal("Jane Doe 12 Main St", field.Value);
        Assert.Equal(2, field.ValueLines.Count);
        Assert.Equal(new OcrBoundingBox(100, 130, 210, 180), field.BoundingBox);
        Assert.Equal(0.7, field.Confidence, 6);   // mean of 0.8 and 0.6
    }

    // ---- presets ----

    /// <summary>A synthetic invoice: eight label/value lines, 20 px tall on a 30 px pitch.</summary>
    private static OcrResult Invoice() => Page(
        Line("Invoice Number: INV-2024-0042", 100, 100, 300, 20),
        Line("Purchase Order: PO-9981", 100, 130, 300, 20),
        Line("Invoice Date: 2024-01-05", 100, 160, 300, 20),
        Line("Due Date: 2024-02-04", 100, 190, 300, 20),
        Line("Subtotal: 100.00", 100, 220, 300, 20),
        Line("Tax: 20.00", 100, 250, 300, 20),
        Line("Total: 120.00", 100, 280, 300, 20),
        Line("Currency: USD", 100, 310, 300, 20));

    [Theory]
    [InlineData("InvoiceNumber", "INV-2024-0042")]
    [InlineData("PurchaseOrderNumber", "PO-9981")]
    [InlineData("InvoiceDate", "2024-01-05")]
    [InlineData("DueDate", "2024-02-04")]
    [InlineData("Subtotal", "100.00")]
    [InlineData("Tax", "20.00")]
    [InlineData("Total", "120.00")]
    [InlineData("Currency", "USD")]
    public void InvoicePresetExtractsTheExpectedFields(string name, string expected)
    {
        var values = Invoice().ExtractFieldValues(FieldPresets.Invoice);

        Assert.Equal(expected, values[name]);
    }

    [Fact]
    public void ReceiptPresetExtractsItsSubsetOfTheSameLayout()
    {
        var values = Invoice().ExtractFieldValues(FieldPresets.Receipt);

        Assert.Equal("2024-01-05", values["InvoiceDate"]);
        Assert.Equal("100.00", values["Subtotal"]);
        Assert.Equal("20.00", values["Tax"]);
        Assert.Equal("120.00", values["Total"]);
        Assert.Equal("USD", values["Currency"]);
        Assert.DoesNotContain("InvoiceNumber", values.Keys);
        Assert.DoesNotContain("DueDate", values.Keys);
    }

    [Fact]
    public void SubtotalIsNotConfusedWithTotal()
    {
        var values = Invoice().ExtractFieldValues(FieldPresets.Invoice);

        Assert.NotEqual(values["Total"], values["Subtotal"]);
    }

    [Fact]
    public void EveryPresetDefinitionHasANameAndAtLeastOneAnchor()
    {
        foreach (var definition in FieldPresets.Invoice.Concat(FieldPresets.Receipt))
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Name));
            Assert.NotEmpty(definition.Anchors);
            Assert.All(definition.Anchors, anchor => Assert.False(string.IsNullOrWhiteSpace(anchor)));
        }
    }

    [Theory]
    [InlineData("42.00", "42.00")]
    [InlineData("$1,234.56", "$1,234.56")]
    [InlineData("-99", "-99")]
    public void AmountPresetPatternCapturesTheValueGroup(string text, string expected)
    {
        var match = FieldPresets.Amount.Match(text);

        Assert.True(match.Success);
        Assert.Equal(expected, match.Groups["value"].Value);
    }

    [Theory]
    [InlineData("2024-01-05")]
    [InlineData("05/01/2024")]
    [InlineData("5 Jan 2024")]
    [InlineData("January 5, 2024")]
    public void DatePresetPatternMatchesTheCommonWrittenShapes(string text)
    {
        Assert.Matches(FieldPresets.Date, text);
    }

    [Fact]
    public void IdentifierPresetPatternRequiresADigitSoPlainWordsAreRejected()
    {
        Assert.DoesNotMatch(FieldPresets.Identifier, "Date");
        Assert.Matches(FieldPresets.Identifier, "INV-2024-0042");
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("eur")]
    [InlineData("€")]
    [InlineData("₹")]
    public void CurrencyPresetPatternMatchesCodesAndSymbols(string text)
    {
        Assert.Matches(FieldPresets.Currency, text);
    }

    // ---- misses, empties and guards ----

    [Fact]
    public void AMissingAnchorLeavesTheFieldOutOfTheResultRatherThanEmittingANullValue()
    {
        var page = Page(Line("Total: 42.00", 100, 100, 120, 20));
        var definitions = new[]
        {
            FieldDefinition.Create("Total", "Total"),
            FieldDefinition.Create("Ghost", "Zzyzx"),
        };

        var fields = page.ExtractFields(definitions);

        var only = Assert.Single(fields);
        Assert.Equal("Total", only.Name);
        Assert.DoesNotContain("Ghost", page.ExtractFieldValues(definitions).Keys);
    }

    [Fact]
    public void ExtractionOfACompletelyUnrelatedDocumentReturnsNothingAndDoesNotThrow()
    {
        var page = Page(
            Line("Lorem ipsum dolor sit amet", 100, 100, 300, 20),
            Line("consectetur adipiscing elit", 100, 130, 300, 20));

        Assert.Empty(page.ExtractFields(FieldPresets.Invoice));
        Assert.Empty(page.ExtractFieldValues(FieldPresets.Invoice));
    }

    [Fact]
    public void AnEmptyResultAndAnEmptyDefinitionListAreBothHandled()
    {
        Assert.Empty(OcrResult.Empty.ExtractFields(FieldPresets.Invoice));
        Assert.Empty(OcrResult.Empty.ExtractFieldValues(FieldPresets.Invoice));
        Assert.Empty(Invoice().ExtractFields(Array.Empty<FieldDefinition>()));
        Assert.Empty(Invoice().ExtractFields(Enumerable.Empty<FieldDefinition>()));
    }

    [Fact]
    public void LinesWithEmptyOrWhitespaceTextAreNeverPickedAsValues()
    {
        var page = Page(
            Line("Total:", 100, 100, 60, 20),
            Line("   ", 200, 100, 60, 20));

        Assert.Empty(page.ExtractFields(new[] { Total(FieldDirection.Right) }));
    }

    [Fact]
    public void NullDefinitionEntriesAreSkippedRatherThanThrowing()
    {
        var page = Page(Line("Total: 42.00", 100, 100, 120, 20));
        var definitions = new FieldDefinition[] { null!, FieldDefinition.Create("Total", "Total") };

        Assert.Single(page.ExtractFields(definitions));
    }

    [Fact]
    public void NullArgumentsThrowArgumentNullException()
    {
        var page = Invoice();

        Assert.Throws<ArgumentNullException>(() => FieldExtractionExtensions.ExtractFields(null!, FieldPresets.Invoice));
        Assert.Throws<ArgumentNullException>(() => page.ExtractFields(null!));
        Assert.Throws<ArgumentNullException>(() => FieldExtractionExtensions.ExtractFieldValues(null!, FieldPresets.Invoice));
        Assert.Throws<ArgumentNullException>(() => FieldExtractionExtensions.ToValueMap(null!));
    }

    // ---- ToValueMap ----

    [Fact]
    public void ToValueMapKeepsTheFirstFieldWhenTwoShareAName()
    {
        var fields = new[]
        {
            new ExtractedField { Name = "Total", Value = "first" },
            new ExtractedField { Name = "Total", Value = "second" },
        };

        var map = fields.ToValueMap();

        Assert.Equal("first", Assert.Single(map).Value);
    }

    [Fact]
    public void ToValueMapKeysAreOrdinalAndThereforeCaseSensitive()
    {
        var map = new[]
        {
            new ExtractedField { Name = "Total", Value = "a" },
            new ExtractedField { Name = "total", Value = "b" },
        }.ToValueMap();

        Assert.Equal(2, map.Count);
        Assert.Equal("a", map["Total"]);
        Assert.Equal("b", map["total"]);
    }

    // ---- definition and option records ----

    [Fact]
    public void CreateBuildsAStandardDefinitionFromNameAndAnchors()
    {
        var definition = FieldDefinition.Create("Total", "Total", "Amount Due");

        Assert.Equal("Total", definition.Name);
        Assert.Equal(new[] { "Total", "Amount Due" }, definition.Anchors);
        Assert.Equal(FieldDirection.Standard, definition.Direction);
        Assert.Equal(6.0, definition.MaxDistance, 6);
        Assert.Equal(0.75, definition.SearchBand, 6);
        Assert.Equal(1, definition.MaxValueLines);
        Assert.Null(definition.ValuePattern);
        Assert.Null(definition.MinAnchorSimilarity);
        Assert.Empty(definition.AnchorPatterns);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsABlankName(string name)
    {
        Assert.Throws<ArgumentException>(() => FieldDefinition.Create(name, "Total"));
    }

    [Fact]
    public void CreateRejectsANullNameOrNullAnchors()
    {
        Assert.Throws<ArgumentNullException>(() => FieldDefinition.Create(null!, "Total"));
        Assert.Throws<ArgumentNullException>(() => FieldDefinition.Create("Total", null!));
    }

    [Fact]
    public void DefinitionsAreImmutableUnderWith()
    {
        var original = FieldDefinition.Create("Total", "Total");
        var widened = original with { MaxDistance = 20.0, Occurrence = FieldOccurrence.Last };

        Assert.Equal(6.0, original.MaxDistance, 6);
        Assert.Equal(FieldOccurrence.Best, original.Occurrence);
        Assert.Equal(20.0, widened.MaxDistance, 6);
        Assert.Equal(FieldOccurrence.Last, widened.Occurrence);
        Assert.NotSame(original, widened);
    }

    [Fact]
    public void DefaultOptionsCarryTheDocumentedValuesAndAreNotMutatedByWith()
    {
        var defaults = FieldExtractionOptions.Default;

        Assert.Equal(0.72, defaults.MinAnchorSimilarity, 6);
        Assert.Equal(0.35, defaults.MinScore, 6);
        Assert.True(defaults.AllowInlineValues);
        Assert.Equal(1.0, defaults.AnchorWeight, 6);
        Assert.Equal(1.0, defaults.DistanceWeight, 6);
        Assert.Equal(0.75, defaults.AlignmentWeight, 6);
        Assert.Equal(0.5, defaults.PatternWeight, 6);

        _ = defaults with { MinScore = 0.99 };
        Assert.Equal(0.35, FieldExtractionOptions.Default.MinScore, 6);
        Assert.Same(defaults, FieldExtractionOptions.Default);
    }

    [Fact]
    public void FlagCombinationsAreTheUnionOfTheirParts()
    {
        Assert.Equal(FieldDirection.Left | FieldDirection.Right, FieldDirection.Horizontal);
        Assert.Equal(FieldDirection.Above | FieldDirection.Below, FieldDirection.Vertical);
        Assert.Equal(FieldDirection.SameLine | FieldDirection.Right | FieldDirection.Below, FieldDirection.Standard);
        Assert.Equal(FieldDirection.Standard | FieldDirection.Horizontal | FieldDirection.Vertical, FieldDirection.All);
    }

    [Fact]
    public void ValueTrimCharactersStripTrailingPunctuationFromAValue()
    {
        var page = Page(
            Line("Total:", 100, 100, 60, 20),
            Line("42.00;", 200, 100, 60, 20));

        Assert.Equal("42.00", Single(page, Total(FieldDirection.Right)).Value);
    }
}
