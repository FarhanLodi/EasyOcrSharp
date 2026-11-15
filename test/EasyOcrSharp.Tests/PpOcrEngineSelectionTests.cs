using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

using PaddleGrouping = EasyOcrSharp.Structure.Engine.Models.TextGrouping;
using PaddleProvider = EasyOcrSharp.Structure.Engine.Services.OcrExecutionProvider;

namespace EasyOcrSharp.Tests;

/// <summary>
/// CI-safe tests for the opt-in PP-OCRv5 engine selection (<see cref="RecognitionOptions.Engine"/>): the
/// EasyOCR-code → PP-OCRv5 pack mapping, the option mapping onto the structure engine, the language
/// report, and the validation every routed entry point performs before any model is loaded. None of
/// these download or load a model.
/// </summary>
public class PpOcrEngineSelectionTests
{
    private static PpOcrLanguagePlan Plan(params string[] languages)
        => PpOcrEngineAdapter.CreatePlan(languages, RecognitionOptions.Default);

    // ---- the option itself ----

    [Fact]
    public void Default_engine_is_EasyOcr_so_existing_callers_are_unchanged()
    {
        Assert.Equal(RecognitionEngine.EasyOcr, RecognitionOptions.Default.Engine);
        Assert.Equal(RecognitionEngine.EasyOcr, new RecognitionOptions().Engine);
        Assert.Equal(RecognitionEngine.EasyOcr, default(RecognitionEngine));
    }

    [Fact]
    public void Engine_values_are_pinned()
    {
        // Persisted configs and serialized option bags store the numeric value.
        Assert.Equal(0, (int)RecognitionEngine.EasyOcr);
        Assert.Equal(1, (int)RecognitionEngine.PpOcrV5);
    }

    [Fact]
    public void Engine_is_an_init_property_that_survives_with_expressions()
    {
        var options = new RecognitionOptions { Engine = RecognitionEngine.PpOcrV5, MinConfidence = 0.5 };
        var copy = options with { Grouping = TextGrouping.Paragraph };

        Assert.Equal(RecognitionEngine.PpOcrV5, copy.Engine);
        Assert.NotEqual(RecognitionOptions.Default, options);
    }

    [Fact]
    public void Unknown_engine_values_are_rejected()
    {
        PpOcrEngineAdapter.EnsureKnownEngine(RecognitionEngine.EasyOcr);
        PpOcrEngineAdapter.EnsureKnownEngine(RecognitionEngine.PpOcrV5);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PpOcrEngineAdapter.EnsureKnownEngine((RecognitionEngine)42));
        Assert.Equal(nameof(RecognitionOptions.Engine), ex.ParamName);
    }

    // ---- language → pack mapping ----

    [Theory]
    [InlineData("en", "en")]
    [InlineData("ch_sim", "ch_sim")]
    [InlineData("ch_tra", "ch_tra")]
    [InlineData("ja", "ja")]
    [InlineData("ko", "ko")]
    [InlineData("ar", "ar")]
    [InlineData("fa", "fa")]
    [InlineData("ur", "ur")]
    [InlineData("ug", "ug")]
    [InlineData("ru", "ru")]
    [InlineData("uk", "uk")]
    [InlineData("bg", "bg")]
    [InlineData("hi", "hi")]
    [InlineData("mr", "mr")]
    [InlineData("fr", "fr")]
    [InlineData("de", "de")]
    [InlineData("th", "th")]
    [InlineData("ta", "ta")]
    [InlineData("te", "te")]
    [InlineData("el", "el")]
    public void A_single_language_runs_one_pack(string language, string engineLanguage)
    {
        var plan = Plan(language);

        Assert.False(plan.MultiPack);
        Assert.False(plan.AutoDetect);
        Assert.Equal(new[] { engineLanguage }, plan.EngineLanguages);
        Assert.Equal(new[] { language }, plan.Requested);
    }

    [Theory]
    [InlineData("ch_sim,en,ja")]   // the default PP-OCRv5 pack reads all three
    [InlineData("ch_sim,ch_tra")]  // two registry packs over one network: never run twice
    [InlineData("ar,fa,ur")]       // one Arabic-script pack
    [InlineData("ru,uk,be")]       // one East-Slavic pack
    [InlineData("hi,mr,ne")]       // one Devanagari pack
    [InlineData("fr,de,es,it,pt")] // one Latin pack
    public void Languages_sharing_a_recognizer_collapse_to_one_pack(string languages)
    {
        var codes = languages.Split(',');
        var plan = Plan(codes);

        Assert.False(plan.MultiPack);
        Assert.Equal(new[] { codes[0] }, plan.EngineLanguages);
        Assert.Equal(codes, plan.Requested);
    }

    [Theory]
    [InlineData("en,fr", "fr")]
    [InlineData("fr,en", "fr")]
    [InlineData("en,ru", "ru")]
    [InlineData("en,ar", "ar")]
    [InlineData("en,hi", "hi")]
    [InlineData("en,ko", "ko")]
    [InlineData("en,th", "th")]
    public void English_beside_another_script_needs_no_pack_of_its_own(string languages, string engineLanguage)
    {
        // Every PP-OCRv5 dictionary carries the ASCII letters, so the other script's pack reads English too.
        var plan = Plan(languages.Split(','));

        Assert.False(plan.MultiPack);
        Assert.Equal(new[] { engineLanguage }, plan.EngineLanguages);
    }

    [Fact]
    public void Two_scripts_read_every_line_with_both_packs_and_the_default_pack_goes_last()
    {
        // Default pack last: the engine's confident-Latin/CJK shortcut only fires when the default pack is
        // tried first, and it would otherwise skip the Korean pack the caller asked for.
        var plan = Plan("ch_sim", "ko");

        Assert.True(plan.MultiPack);
        Assert.False(plan.AutoDetect);
        Assert.Equal(new[] { "ko", "ch_sim" }, plan.EngineLanguages);
    }

    [Fact]
    public void Chinese_beside_English_keeps_the_default_pack_when_it_is_needed_for_more_than_English()
    {
        var plan = Plan("en", "ch_sim", "ar");

        Assert.True(plan.MultiPack);
        Assert.Equal(new[] { "ar", "en" }, plan.EngineLanguages);
    }

    [Fact]
    public void East_Slavic_and_other_Cyrillic_codes_are_different_packs()
    {
        var plan = Plan("ru", "bg");

        Assert.True(plan.MultiPack);
        Assert.Equal(new[] { "ru", "bg" }, plan.EngineLanguages);
    }

    [Fact]
    public void Every_EasyOcr_language_maps_except_the_scripts_PP_OCRv5_cannot_spell()
    {
        var unsupported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bn", "as", "mni", "kn", "vi" };
        foreach (var code in ModelRegistry.All.SelectMany(pack => pack.Languages))
        {
            Assert.True(PpOcrEngineAdapter.IsSupported(code) == !unsupported.Contains(code),
                $"'{code}': expected supported = {!unsupported.Contains(code)}");
        }
    }

    [Fact]
    public void Supported_languages_are_sorted_and_cover_the_headline_codes()
    {
        var supported = PpOcrEngineAdapter.SupportedLanguages;

        Assert.Equal(supported.Order(StringComparer.Ordinal), supported);
        foreach (var code in new[] { "en", "ch_sim", "ch_tra", "ja", "ko", "ar", "fa", "ur", "ru", "hi", "fr", "th", "ta", "te", "el" })
            Assert.Contains(code, supported);
        Assert.DoesNotContain("bn", supported);
        Assert.DoesNotContain("kn", supported);
    }

    [Fact]
    public void An_unsupported_EasyOcr_language_names_itself_the_default_engine_and_the_supported_codes()
    {
        var ex = Assert.Throws<ArgumentException>(() => Plan("en", "bn", "kn"));

        Assert.Equal("languages", ex.ParamName);
        Assert.Contains("'bn'", ex.Message);
        Assert.Contains("'kn'", ex.Message);
        Assert.DoesNotContain("'en'", ex.Message);
        Assert.Contains("RecognitionEngine.EasyOcr", ex.Message);
        Assert.Contains("Supported codes:", ex.Message);
        Assert.Contains("ch_sim", ex.Message);
    }

    [Fact]
    public void An_unknown_language_is_rejected_without_pointing_at_the_default_engine()
    {
        var ex = Assert.Throws<ArgumentException>(() => Plan("xx"));

        Assert.Contains("'xx'", ex.Message);
        Assert.DoesNotContain("supported by the default", ex.Message);
    }

    [Fact]
    public void An_empty_request_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Plan());
    }

    // ---- auto-detection ----

    [Fact]
    public void Auto_detection_without_candidates_uses_the_default_engines_set_collapsed_to_packs()
    {
        var plan = PpOcrEngineAdapter.CreatePlan(Array.Empty<string>(), new RecognitionOptions { AutoDetectLanguage = true });

        Assert.True(plan.AutoDetect);
        Assert.True(plan.MultiPack);
        // en / ch_sim / ja share the default pack (which leads, enabling the engine's fast path); ru and ko
        // are their own packs.
        Assert.Equal(new[] { "en", "ru", "ko" }, plan.EngineLanguages);
        Assert.Empty(plan.Requested);
    }

    [Fact]
    public void Auto_detection_honors_explicit_candidates_and_validates_them()
    {
        var plan = PpOcrEngineAdapter.CreatePlan(new[] { "en" },
            new RecognitionOptions { AutoDetectLanguage = true, AutoDetectCandidates = new[] { " AR ", "hi", "ar" } });
        Assert.Equal(new[] { "ar", "hi" }, plan.EngineLanguages);

        var ex = Assert.Throws<ArgumentException>(() => PpOcrEngineAdapter.CreatePlan(Array.Empty<string>(),
            new RecognitionOptions { AutoDetectLanguage = true, AutoDetectCandidates = new[] { "en", "bn" } }));
        Assert.Equal(nameof(RecognitionOptions.AutoDetectCandidates), ex.ParamName);
    }

    [Theory]
    [InlineData("japan", "ja")]
    [InlineData("ch", "ch_sim")]
    [InlineData("latin", "en")]
    [InlineData("eslav", "ru")]
    [InlineData("korean", "ko")]
    public void Detected_codes_are_reported_in_EasyOcr_style(string detected, string reported)
    {
        var plan = PpOcrEngineAdapter.CreatePlan(Array.Empty<string>(), new RecognitionOptions { AutoDetectLanguage = true });

        Assert.Equal(new[] { reported }, PpOcrEngineAdapter.ReportedLanguages(plan, new[] { detected }));
    }

    [Theory]
    [InlineData("Hello World 2024", "en")]
    [InlineData("中山路 今日菜单", "ch_sim")]
    [InlineData("しんじゅく よよぎ", "ja")]
    [InlineData(null, "ch_sim")]
    public void A_default_pack_win_is_reported_by_the_script_it_read(string? text, string reported)
    {
        // The vote path reports the default pack as "ch", which also serves English and Japanese.
        var plan = PpOcrEngineAdapter.CreatePlan(Array.Empty<string>(), new RecognitionOptions { AutoDetectLanguage = true });

        Assert.Equal(new[] { reported }, PpOcrEngineAdapter.ReportedLanguages(plan, new[] { "ch" }, text));
    }

    [Fact]
    public void Detected_pack_codes_prefer_the_candidate_the_caller_named()
    {
        var latin = PpOcrEngineAdapter.CreatePlan(Array.Empty<string>(),
            new RecognitionOptions { AutoDetectLanguage = true, AutoDetectCandidates = new[] { "fr", "ru" } });
        Assert.Equal(new[] { "fr" }, PpOcrEngineAdapter.ReportedLanguages(latin, new[] { "latin" }));

        var cyrillic = PpOcrEngineAdapter.CreatePlan(Array.Empty<string>(),
            new RecognitionOptions { AutoDetectLanguage = true, AutoDetectCandidates = new[] { "bg" } });
        Assert.Equal(new[] { "bg" }, PpOcrEngineAdapter.ReportedLanguages(cyrillic, new[] { "cyrillic" }));
    }

    [Fact]
    public void Reported_languages_fall_back_like_the_default_engine()
    {
        // No detection: the request is reported.
        Assert.Equal(new[] { "en", "fr" }, PpOcrEngineAdapter.ReportedLanguages(Plan("en", "fr"), Array.Empty<string>()));

        // Auto-detection that found nothing, with nothing requested: "en", as on the default engine.
        var auto = PpOcrEngineAdapter.CreatePlan(Array.Empty<string>(), new RecognitionOptions { AutoDetectLanguage = true });
        Assert.Equal(new[] { "en" }, PpOcrEngineAdapter.ReportedLanguages(auto, Array.Empty<string>()));

        // Auto-detection that found nothing, with a request: the request.
        var autoWithRequest = PpOcrEngineAdapter.CreatePlan(new[] { "de" }, new RecognitionOptions { AutoDetectLanguage = true });
        Assert.Equal(new[] { "de" }, PpOcrEngineAdapter.ReportedLanguages(autoWithRequest, Array.Empty<string>()));
    }

    // ---- option mapping ----

    // The engine's enums are internal, so the theories carry their names (a public test method cannot take
    // an internal parameter type).
    [Theory]
    [InlineData(TextGrouping.Word, false, nameof(PaddleGrouping.Word))]
    [InlineData(TextGrouping.Line, false, nameof(PaddleGrouping.Line))]
    [InlineData(TextGrouping.Paragraph, false, nameof(PaddleGrouping.Line))] // paragraphs are merged by the service
    [InlineData(TextGrouping.Line, true, nameof(PaddleGrouping.Word))]       // one result per supplied region
    [InlineData(TextGrouping.Paragraph, true, nameof(PaddleGrouping.Word))]
    public void Grouping_maps_onto_the_engine(TextGrouping grouping, bool perRegion, string expected)
    {
        var mapped = PpOcrEngineAdapter.ToEngineOptions(new RecognitionOptions { Grouping = grouping }, Plan("en"), perRegion);
        Assert.Equal(Enum.Parse<PaddleGrouping>(expected), mapped.Grouping);
    }

    [Theory]
    [InlineData(1, PpOcrEngineAdapter.DefaultBatchSize)]
    [InlineData(0, PpOcrEngineAdapter.DefaultBatchSize)]
    [InlineData(-3, PpOcrEngineAdapter.DefaultBatchSize)]
    [InlineData(2, 2)]
    [InlineData(16, 16)]
    public void Batch_size_keeps_PP_OCRv5s_default_unless_raised(int batchSize, int expected)
    {
        var mapped = PpOcrEngineAdapter.ToEngineOptions(new RecognitionOptions { BatchSize = batchSize }, Plan("en"), perRegion: false);
        Assert.Equal(expected, mapped.BatchSize);
    }

    [Fact]
    public void Defaults_map_to_a_plain_PP_OCRv5_run_with_every_surrounding_stage_left_to_the_service()
    {
        var mapped = PpOcrEngineAdapter.ToEngineOptions(RecognitionOptions.Default, Plan("en"), perRegion: false);

        Assert.Equal(0.0, mapped.DropScore);
        Assert.True(mapped.UseTextLineOrientation);
        Assert.True(mapped.VerifyOrientationByRecognition);
        Assert.False(mapped.UseDocOrientation);
        Assert.False(mapped.UseDocUnwarp);
        Assert.False(mapped.AutoDetectLanguage);
        Assert.Null(mapped.AutoDetectCandidates);
        Assert.Null(mapped.Allowlist);
        Assert.Null(mapped.Blocklist);
        Assert.Null(mapped.Region);
        Assert.Equal(EasyOcrSharp.Structure.Engine.Models.PreprocessingOptions.None, mapped.Preprocessing);
    }

    [Theory]
    [InlineData(WordLevelDetail.None, false)]
    [InlineData(WordLevelDetail.Words, true)]
    [InlineData(WordLevelDetail.Characters, true)]
    public void Word_level_detail_requests_the_engines_word_boxes(WordLevelDetail detail, bool expected)
    {
        var mapped = PpOcrEngineAdapter.ToEngineOptions(new RecognitionOptions { WordLevelDetail = detail }, Plan("en"), perRegion: false);
        Assert.Equal(expected, mapped.ReturnWordBoxes);
    }

    [Fact]
    public void Default_detection_keeps_PaddleOCRs_own_thresholds()
    {
        var mapped = PpOcrEngineAdapter.ToEngineOptions(RecognitionOptions.Default, Plan("en"), perRegion: false);
        Assert.Same(EasyOcrSharp.Structure.Engine.Models.DetectionOptions.Default, mapped.Detection);

        // CRAFT thresholds mean nothing to the DB detector and are not forwarded.
        var craftTuned = PpOcrEngineAdapter.ToEngineDetection(new DetectionOptions { TextThreshold = 0.3, LinkThreshold = 0.2, CanvasSize = 4000 });
        Assert.Same(EasyOcrSharp.Structure.Engine.Models.DetectionOptions.Default, craftTuned);
    }

    [Fact]
    public void Engine_neutral_detection_opt_ins_reach_the_DB_detector_without_changing_its_thresholds()
    {
        var mapped = PpOcrEngineAdapter.ToEngineDetection(new DetectionOptions
        {
            TileLargeImages = true,
            MinTextHeight = 16,
            EnhanceContrast = true,
            TextThreshold = 0.3,
        });

        var defaults = EasyOcrSharp.Structure.Engine.Models.DetectionOptions.Default;
        Assert.True(mapped.TileLargeImages);
        Assert.Equal(16, mapped.MinTextHeight);
        Assert.True(mapped.EnhanceContrast);
        Assert.Equal(defaults with { TileLargeImages = true, MinTextHeight = 16, EnhanceContrast = true }, mapped);
    }

    [Fact]
    public void Min_confidence_and_a_clamped_parallelism_are_forwarded()
    {
        var mapped = PpOcrEngineAdapter.ToEngineOptions(
            new RecognitionOptions { MinConfidence = 0.42, MaxDegreeOfParallelism = 3 }, Plan("en"), perRegion: false);
        Assert.Equal(0.42, mapped.DropScore);
        Assert.Equal(3, mapped.MaxDegreeOfParallelism);

        var clamped = PpOcrEngineAdapter.ToEngineOptions(new RecognitionOptions { MaxDegreeOfParallelism = 0 }, Plan("en"), perRegion: false);
        Assert.Equal(1, clamped.MaxDegreeOfParallelism);
    }

    [Fact]
    public void Allowlist_wins_over_blocklist_as_on_the_default_engine()
    {
        var both = PpOcrEngineAdapter.ToEngineOptions(
            new RecognitionOptions { Allowlist = "0123456789", Blocklist = "5" }, Plan("en"), perRegion: false);
        Assert.NotNull(both.Allowlist);
        Assert.Equal("0123456789".Select(c => c.ToString()), both.Allowlist!);
        Assert.Null(both.Blocklist);

        var blockOnly = PpOcrEngineAdapter.ToEngineOptions(new RecognitionOptions { Blocklist = "@#" }, Plan("en"), perRegion: false);
        Assert.Null(blockOnly.Allowlist);
        Assert.NotNull(blockOnly.Blocklist);
        Assert.Equal(new[] { "@", "#" }, blockOnly.Blocklist!);
    }

    [Fact]
    public void A_multi_pack_plan_drives_the_engines_per_line_best_of_sweep()
    {
        var plan = Plan("ch_sim", "ko");
        var mapped = PpOcrEngineAdapter.ToEngineOptions(RecognitionOptions.Default, plan, perRegion: false);

        Assert.True(mapped.AutoDetectLanguage);
        Assert.NotNull(mapped.AutoDetectCandidates);
        Assert.Equal(plan.EngineLanguages, mapped.AutoDetectCandidates!);
    }

    [Theory]
    [InlineData(nameof(PaddleProvider.Cpu), OcrExecutionProvider.Cpu)]
    [InlineData(nameof(PaddleProvider.Cuda), OcrExecutionProvider.Cuda)]
    [InlineData(nameof(PaddleProvider.DirectMl), OcrExecutionProvider.DirectMl)]
    [InlineData(nameof(PaddleProvider.CoreMl), OcrExecutionProvider.CoreMl)]
    public void Execution_providers_map_back_onto_the_public_enum(string engine, OcrExecutionProvider expected)
        => Assert.Equal(expected, PpOcrEngineAdapter.ToPublicProvider(Enum.Parse<PaddleProvider>(engine)));

    [Fact]
    public void Engine_regions_map_onto_the_public_type()
    {
        var polygon = new[] { new OcrPoint(1, 2), new OcrPoint(9, 2), new OcrPoint(9, 6), new OcrPoint(1, 6) };
        var regions = PpOcrEngineAdapter.ToPublicRegions(new[]
        {
            new EasyOcrSharp.Structure.Engine.Models.DetectedRegion { BoundingPolygon = polygon, BoundingBox = OcrBoundingBox.FromPoints(polygon) },
        });

        var region = Assert.Single(regions);
        Assert.Equal(polygon, region.BoundingPolygon.ToArray());
        Assert.Equal(OcrBoundingBox.FromPoints(polygon), region.BoundingBox);
        Assert.Empty(PpOcrEngineAdapter.ToPublicRegions(Array.Empty<EasyOcrSharp.Structure.Engine.Models.DetectedRegion>()));
    }

    // ---- service-level validation (fires before any model is loaded) ----

    private static readonly RecognitionOptions Paddle = new() { Engine = RecognitionEngine.PpOcrV5 };

    private static Image<Rgb24> Blank() => new(32, 16, new Rgb24(255, 255, 255));

    [Fact]
    public async Task ExtractTextFromImage_rejects_an_unsupported_language_before_loading_anything()
    {
        await using var svc = new EasyOcrService();
        using var image = Blank();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => svc.ExtractTextFromImage(image, new[] { "bn" }, Paddle));
        Assert.Contains("'bn'", ex.Message);
    }

    [Fact]
    public async Task ExtractTextFromImage_rejects_an_unknown_engine()
    {
        await using var svc = new EasyOcrService();
        using var image = Blank();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            svc.ExtractTextFromImage(image, new[] { "en" }, new RecognitionOptions { Engine = (RecognitionEngine)7 }));
    }

    [Fact]
    public async Task ExtractTextFromImage_still_requires_a_language()
    {
        await using var svc = new EasyOcrService();
        using var image = Blank();

        await Assert.ThrowsAsync<ArgumentException>(() => svc.ExtractTextFromImage(image, new[] { " " }, Paddle));
    }

    [Fact]
    public async Task RecognizeRegionsAsync_validates_languages_even_with_no_regions()
    {
        await using var svc = new EasyOcrService();
        using var image = Blank();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.RecognizeRegionsAsync(image, Array.Empty<IReadOnlyList<OcrPoint>>(), new[] { "kn" }, Paddle));
    }

    [Fact]
    public async Task RecognizeRegionsAsync_with_no_regions_returns_an_empty_PP_OCRv5_result_without_a_model()
    {
        await using var svc = new EasyOcrService();
        using var image = Blank();

        var result = await svc.RecognizeRegionsAsync(image, Array.Empty<IReadOnlyList<OcrPoint>>(), new[] { "EN" }, Paddle);

        Assert.Empty(result.Lines);
        Assert.Equal(string.Empty, result.FullText);
        Assert.Equal(new[] { "en" }, result.Languages);
        Assert.NotEqual(OcrExecutionProvider.Auto, result.ExecutionProvider);
        Assert.Equal(image.Width, result.SourceWidth);
    }

    [Fact]
    public async Task DetectRegionsAsync_rejects_an_unknown_engine()
    {
        await using var svc = new EasyOcrService();
        using var image = Blank();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            svc.DetectRegionsAsync(image, new RecognitionOptions { Engine = (RecognitionEngine)7 }));
    }

    [Fact]
    public async Task ExtractTextStreamAsync_rejects_an_unsupported_language_before_detection()
    {
        await using var svc = new EasyOcrService();
        using var image = Blank();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in svc.ExtractTextStreamAsync(image, new[] { "mni" }, Paddle)) { }
        });
    }

    [Fact]
    public async Task A_disposed_service_rejects_PP_OCRv5_calls()
    {
        var svc = new EasyOcrService();
        await svc.DisposeAsync();
        using var image = Blank();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => svc.ExtractTextFromImage(image, new[] { "en" }, Paddle));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => svc.DetectRegionsAsync(image, Paddle));
    }

    [Fact]
    public async Task A_pre_cancelled_PP_OCRv5_call_is_cancelled_not_run()
    {
        await using var svc = new EasyOcrService();
        using var image = Blank();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.ExtractTextFromImage(image, new[] { "en" }, Paddle, cts.Token));
    }
}
