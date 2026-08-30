using EasyOcrSharp.Models;
using EasyOcrSharp.Structure;
using EasyOcrSharp.Structure.Layout;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for <see cref="LayoutPostProcessor"/> — the clean-up
/// chain that runs over the layout detections after score-thresholding: the overlapping-region filter, the
/// oversized-image drop, optional NMS, optional unclip, the containment merge modes, and the sort by the
/// model's own predicted reading order. The per-call score threshold
/// (<see cref="StructureOptions.LayoutScoreThreshold"/>) is exercised against the real
/// <see cref="LayoutGraph.BuildRegions"/>.
/// </summary>
public class LayoutPostProcessingTests
{
    /// <summary>
    /// Builds a region with the given bounds, defaulting the fields most tests do not care about.
    /// </summary>
    private static LayoutRegion Region(
        double x1, double y1, double x2, double y2,
        StructureBlockType type = StructureBlockType.Text,
        float score = 0.9f,
        string? label = "text",
        int classId = 0,
        int? orderIndex = null) =>
        new(type, new OcrBoundingBox(x1, y1, x2, y2), score, classId, label, orderIndex);

    /// <summary>Options with every optional pass off, so a test exercises one behaviour at a time.</summary>
    private static StructureOptions Bare => StructureOptions.Default with { FilterOverlappingRegions = false };

    private static IReadOnlyList<LayoutRegion> Run(
        IEnumerable<LayoutRegion> regions, StructureOptions options, int width = 1000, int height = 1400) =>
        LayoutPostProcessor.Apply(regions.ToList(), options, width, height);

    // -----------------------------------------------------------------------------------------------------
    // configurable score threshold: StructureOptions.LayoutScoreThreshold -> LayoutGraph.BuildRegions
    // -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Three real 7-wide PP-DocLayoutV3 rows (<c>class_id, score, x1, y1, x2, y2, order_index</c>) scoring
    /// 0.95 / 0.45 / 0.20, so a threshold sweep crosses each one in turn.
    /// </summary>
    private static LayoutGraph.Detections ThreeScoredRows() => new(
        new float[]
        {
            0, 0.95f, 10, 10, 110, 50, 0,
            1, 0.45f, 10, 60, 110, 90, 1,
            2, 0.20f, 10, 100, 210, 300, 2,
        },
        rows: 3,
        rowWidth: 7);

    /// <summary>
    /// The threshold the detectors receive per call is the one that filters, and it is exclusive
    /// (<c>score &gt; threshold</c>), so a row scoring exactly the threshold is dropped.
    /// </summary>
    [Theory]
    [InlineData(0.5f, 1)]    // the shipped default: only the 0.95 row survives
    [InlineData(0.45f, 1)]   // exclusive: a row scoring exactly the threshold is still dropped
    [InlineData(0.3f, 2)]    // lowered: the 0.45 row comes back
    [InlineData(0.1f, 3)]    // low: every row is kept
    [InlineData(0.95f, 0)]   // at the best score: nothing survives
    public void BuildRegions_keeps_only_rows_scoring_above_the_supplied_threshold(float threshold, int expected)
    {
        var classMap = new Dictionary<int, StructureBlockType>
        {
            [0] = StructureBlockType.Text,
            [1] = StructureBlockType.Title,
            [2] = StructureBlockType.Table,
        };

        var regions = LayoutGraph.BuildRegions(
            ThreeScoredRows(), classMap, threshold, scaleX: 1f, scaleY: 1f, origW: 1000, origH: 1000);

        Assert.Equal(expected, regions.Count);
        Assert.All(regions, r => Assert.True(r.Score > threshold));
    }

    [Fact]
    public void BuildRegions_carries_the_raw_label_and_the_models_order_index()
    {
        var classMap = new Dictionary<int, StructureBlockType> { [1] = StructureBlockType.Title };
        var labelNames = LayoutLabelMap.NamesFromList(new[] { "text", "Paragraph-Title", "table" });

        var region = Assert.Single(LayoutGraph.BuildRegions(
            new LayoutGraph.Detections(new float[] { 1, 0.9f, 10, 10, 110, 50, 4 }, rows: 1, rowWidth: 7),
            classMap, scoreThreshold: 0.5f, scaleX: 1f, scaleY: 1f, origW: 1000, origH: 1000, labelNames));

        Assert.Equal(StructureBlockType.Title, region.Type);
        Assert.Equal("paragraph_title", region.RawLabel);   // normalized: lower-case, '_'-separated
        Assert.Equal(4, region.OrderIndex);
    }

    [Fact]
    public void BuildRegions_leaves_the_order_index_null_on_a_six_wide_row()
    {
        var classMap = new Dictionary<int, StructureBlockType> { [0] = StructureBlockType.Text };

        var region = Assert.Single(LayoutGraph.BuildRegions(
            new LayoutGraph.Detections(new float[] { 0, 0.9f, 10, 10, 110, 50 }, rows: 1, rowWidth: 6),
            classMap, scoreThreshold: 0.5f, scaleX: 1f, scaleY: 1f, origW: 1000, origH: 1000));

        Assert.Null(region.OrderIndex);
        Assert.Null(region.RawLabel);
    }

    [Fact]
    public void LayoutScoreThreshold_defaults_to_the_documented_value_and_is_overridable()
    {
        Assert.Equal(0.5f, StructureOptions.DefaultLayoutScoreThreshold);
        Assert.Equal(0.5f, StructureOptions.Default.LayoutScoreThreshold);

        var relaxed = StructureOptions.Default with { LayoutScoreThreshold = 0.25f };
        Assert.Equal(0.25f, relaxed.LayoutScoreThreshold);
        Assert.Equal(0.5f, StructureOptions.Default.LayoutScoreThreshold); // record copy leaves the default alone
    }

    // -----------------------------------------------------------------------------------------------------
    // overlapping-region filter (on by default)
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Overlap_filter_collapses_duplicate_regions_to_the_larger_one()
    {
        // The smaller box sits entirely inside the larger, so the overlap is 1.0 of the smaller: a duplicate.
        var large = Region(10, 10, 210, 110);
        var small = Region(20, 20, 200, 100);

        var kept = Run(new[] { large, small }, StructureOptions.Default);

        Assert.Equal(new[] { large }, kept);
    }

    [Fact]
    public void Overlap_filter_keeps_regions_that_only_touch()
    {
        // 20px of vertical overlap on 100px-tall boxes — nowhere near the 70% bar.
        var upper = Region(10, 10, 210, 110);
        var lower = Region(10, 90, 210, 190);

        var kept = Run(new[] { upper, lower }, StructureOptions.Default);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void Overlap_filter_can_be_turned_off()
    {
        var large = Region(10, 10, 210, 110);
        var small = Region(20, 20, 200, 100);

        var kept = Run(
            new[] { large, small }, StructureOptions.Default with { FilterOverlappingRegions = false });

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void Overlap_filter_drops_a_reference_marker_the_reference_content_covers()
    {
        // The marker sits inside the content block, so the content already carries its text.
        var marker = Region(20, 210, 70, 230, StructureBlockType.Reference, label: "reference");
        var content = Region(10, 200, 400, 300, StructureBlockType.Reference, label: "reference_content");

        var kept = Run(new[] { marker, content }, StructureOptions.Default);

        Assert.Equal(new[] { content }, kept);
    }

    [Fact]
    public void Overlap_filter_keeps_a_reference_marker_no_reference_content_covers()
    {
        // Regression guard: `reference` regions used to be dropped unconditionally, on the assumption that a
        // neighbouring `reference_content` always carried their text. Nothing verified that, so a bibliography
        // the model labelled `reference` alone was deleted outright — and the 23-class PicoDet vocabulary has
        // no `reference_content` class at all, so on that model it happened to every reference region.
        var marker = Region(10, 10, 60, 30, StructureBlockType.Reference, label: "reference");
        var content = Region(10, 200, 400, 300, StructureBlockType.Reference, label: "reference_content");

        var kept = Run(new[] { marker, content }, StructureOptions.Default);

        Assert.Equal(new[] { marker, content }, kept);
    }

    [Fact]
    public void Overlap_filter_keeps_references_on_a_vocabulary_without_reference_content()
    {
        // PicoDet-S/M (PP-DocLayout-M.yml) has `reference` and no `reference_content`.
        var first = Region(10, 10, 400, 60, StructureBlockType.Reference, label: "reference");
        var second = Region(10, 80, 400, 130, StructureBlockType.Reference, label: "reference");

        var kept = Run(new[] { first, second }, StructureOptions.Default);

        Assert.Equal(new[] { first, second }, kept);
    }

    [Fact]
    public void Overlap_filter_drops_slivers_under_six_pixels()
    {
        var sliver = Region(10, 10, 14, 300);   // 4px wide
        var normal = Region(100, 10, 400, 300);

        var kept = Run(new[] { sliver, normal }, StructureOptions.Default);

        Assert.Equal(new[] { normal }, kept);
    }

    [Fact]
    public void Overlap_filter_leaves_a_figure_overlapping_a_chart_alone()
    {
        // image / table / seal / chart legitimately nest in one another, so a differently-labelled pair drawn
        // from that set survives even at full overlap.
        var image = Region(10, 10, 410, 310, StructureBlockType.Figure, label: "image");
        var chart = Region(20, 20, 400, 300, StructureBlockType.Chart, label: "chart");

        var kept = Run(new[] { image, chart }, StructureOptions.Default);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void Overlap_filter_collapses_a_text_region_into_the_table_containing_it()
    {
        var table = Region(10, 10, 410, 310, StructureBlockType.Table, label: "table");
        var text = Region(20, 20, 400, 300, StructureBlockType.Text, label: "text");

        var kept = Run(new[] { table, text }, StructureOptions.Default);

        Assert.Equal(new[] { table }, kept);
    }

    [Fact]
    public void Overlap_filter_absorbs_an_inline_formula_into_its_paragraph()
    {
        // 60% of the formula sits inside the paragraph: under the 70% duplicate bar, over the 50% one that
        // applies to inline formulas. An identical region labelled display_formula therefore survives, which
        // is what makes this a test of the inline rule rather than of the duplicate rule.
        var paragraph = Region(10, 10, 410, 110, StructureBlockType.Text, label: "text");
        var inline = Region(350, 20, 450, 100, StructureBlockType.Formula, label: "inline_formula");
        var display = Region(350, 20, 450, 100, StructureBlockType.Formula, label: "display_formula");

        Assert.Equal(new[] { paragraph }, Run(new[] { paragraph, inline }, StructureOptions.Default));
        Assert.Equal(2, Run(new[] { paragraph, display }, StructureOptions.Default).Count);
    }

    // -----------------------------------------------------------------------------------------------------
    // oversized-image drop (always on)
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void An_image_covering_the_whole_portrait_page_is_dropped()
    {
        // 1000x1400 page; the image covers ~96%, over the 93% portrait budget.
        var wholePage = Region(0, 0, 1000, 1350, StructureBlockType.Figure, label: "image");
        var text = Region(10, 10, 400, 200);

        var kept = Run(new[] { wholePage, text }, Bare);

        Assert.Equal(new[] { text }, kept);
    }

    [Fact]
    public void An_image_covering_most_of_the_page_is_kept()
    {
        // ~64% of the page: a full-bleed illustration, not a false positive.
        var illustration = Region(0, 0, 1000, 900, StructureBlockType.Figure, label: "image");
        var text = Region(10, 1000, 400, 1200);

        var kept = Run(new[] { illustration, text }, Bare);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void The_oversized_image_budget_is_tighter_on_a_landscape_page()
    {
        // Both pages are 1.4M px and both images cover 88% of their page: kept under the 93% portrait
        // budget, dropped under the 82% landscape one.
        var portraitImage = Region(0, 0, 1000, 1232, StructureBlockType.Figure, label: "image");
        var portraitText = Region(10, 1250, 400, 1350);
        var landscapeImage = Region(0, 0, 1400, 880, StructureBlockType.Figure, label: "image");
        var landscapeText = Region(10, 900, 400, 980);

        Assert.Equal(2, Run(new[] { portraitImage, portraitText }, Bare, width: 1000, height: 1400).Count);
        Assert.Equal(
            new[] { landscapeText },
            Run(new[] { landscapeImage, landscapeText }, Bare, width: 1400, height: 1000));
    }

    [Fact]
    public void A_figure_labelled_region_is_never_treated_as_an_oversized_image()
    {
        // The PicoDet vocabularies name the class "figure", and the drop only applies to "image".
        var figure = Region(0, 0, 1000, 1390, StructureBlockType.Figure, label: "figure");
        var text = Region(10, 10, 400, 200);

        Assert.Equal(2, Run(new[] { figure, text }, Bare).Count);
    }

    // -----------------------------------------------------------------------------------------------------
    // NMS (opt-in)
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Nms_is_off_by_default_and_suppresses_the_lower_score_when_enabled()
    {
        var strong = Region(10, 10, 210, 110, score: 0.9f, classId: 3);
        var weak = Region(20, 20, 220, 120, score: 0.6f, classId: 3);   // 0.75 IoU, same class

        Assert.Equal(2, Run(new[] { strong, weak }, Bare).Count);

        var kept = Run(new[] { strong, weak }, Bare with { LayoutNms = true });
        Assert.Equal(new[] { strong }, kept);
    }

    [Fact]
    public void Nms_only_suppresses_a_different_class_when_the_boxes_are_near_identical()
    {
        var strong = Region(10, 10, 210, 110, score: 0.9f, classId: 3);
        var weak = Region(20, 20, 220, 120, score: 0.6f, classId: 4);   // the same 0.75 IoU, different class

        var kept = Run(new[] { strong, weak }, Bare with { LayoutNms = true });

        Assert.Equal(2, kept.Count);
    }

    // -----------------------------------------------------------------------------------------------------
    // unclip (opt-in)
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Unclip_grows_a_region_about_its_centre_and_clamps_to_the_page()
    {
        var region = Region(100, 100, 300, 200);

        var grown = Assert.Single(Run(new[] { region }, Bare with { LayoutUnclipRatio = 1.2f }));

        // 200x100 box centred at (200, 150) grows to 240x120.
        Assert.Equal(80, grown.Bounds.MinX, 3);
        Assert.Equal(320, grown.Bounds.MaxX, 3);
        Assert.Equal(90, grown.Bounds.MinY, 3);
        Assert.Equal(210, grown.Bounds.MaxY, 3);
    }

    [Fact]
    public void Unclip_never_pushes_a_region_off_the_page()
    {
        var edge = Region(0, 0, 200, 100);

        var grown = Assert.Single(Run(new[] { edge }, Bare with { LayoutUnclipRatio = 2f }));

        Assert.Equal(0, grown.Bounds.MinX, 3);
        Assert.Equal(0, grown.Bounds.MinY, 3);
    }

    // -----------------------------------------------------------------------------------------------------
    // containment merge (opt-in)
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Merge_large_keeps_the_enclosing_region()
    {
        var outer = Region(10, 10, 510, 410, StructureBlockType.Table, label: "table");
        var inner = Region(20, 20, 400, 300, StructureBlockType.Text, label: "text");

        var kept = Run(new[] { outer, inner }, Bare with { LayoutMergeMode = LayoutMergeMode.Large });

        Assert.Equal(new[] { outer }, kept);
    }

    [Fact]
    public void Merge_small_keeps_the_inner_region()
    {
        var outer = Region(10, 10, 510, 410, StructureBlockType.Table, label: "table");
        var inner = Region(20, 20, 400, 300, StructureBlockType.Text, label: "text");

        var kept = Run(new[] { outer, inner }, Bare with { LayoutMergeMode = LayoutMergeMode.Small });

        Assert.Equal(new[] { inner }, kept);
    }

    [Fact]
    public void Merge_leaves_a_formula_inside_a_paragraph_alone()
    {
        // "formula" is protected from being absorbed by the text around it.
        var paragraph = Region(10, 10, 510, 410, StructureBlockType.Text, label: "text");
        var formula = Region(20, 20, 400, 300, StructureBlockType.Formula, label: "formula");

        var kept = Run(new[] { paragraph, formula }, Bare with { LayoutMergeMode = LayoutMergeMode.Large });

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void Merge_modes_none_and_union_keep_both_regions()
    {
        var outer = Region(10, 10, 510, 410, StructureBlockType.Table, label: "table");
        var inner = Region(20, 20, 400, 300, StructureBlockType.Text, label: "text");

        Assert.Equal(2, Run(new[] { outer, inner }, Bare).Count);
        Assert.Equal(2, Run(new[] { outer, inner }, Bare with { LayoutMergeMode = LayoutMergeMode.Union }).Count);
    }

    // -----------------------------------------------------------------------------------------------------
    // reading order predicted by the model
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Regions_are_sorted_by_the_order_index_the_model_predicted()
    {
        var third = Region(10, 10, 200, 100, orderIndex: 7);
        var first = Region(10, 200, 200, 300, orderIndex: 1);
        var second = Region(10, 400, 200, 500, orderIndex: 4);

        var sorted = Run(new[] { third, first, second }, Bare);

        Assert.Equal(new[] { first, second, third }, sorted);
    }

    [Fact]
    public void Detector_order_is_preserved_when_the_model_predicts_no_order()
    {
        // The 6-wide PicoDet / plus-L rows carry no order column, so ordering is left to the XY-cut pass.
        var a = Region(10, 10, 200, 100);
        var b = Region(10, 200, 200, 300);

        Assert.Equal(new[] { a, b }, Run(new[] { a, b }, Bare));
    }

    [Fact]
    public void A_partial_order_index_is_ignored_rather_than_half_applied()
    {
        var ordered = Region(10, 10, 200, 100, orderIndex: 9);
        var unordered = Region(10, 200, 200, 300);

        Assert.Equal(new[] { ordered, unordered }, Run(new[] { ordered, unordered }, Bare));
    }

    // -----------------------------------------------------------------------------------------------------
    // defaults
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Default_options_enable_only_the_overlap_filter()
    {
        var options = StructureOptions.Default;

        Assert.True(options.FilterOverlappingRegions);
        Assert.False(options.LayoutNms);
        Assert.Null(options.LayoutUnclipRatio);
        Assert.Equal(LayoutMergeMode.None, options.LayoutMergeMode);
        Assert.Equal(LayoutReadingOrder.Auto, options.ReadingOrder);
    }

    [Fact]
    public void The_public_analysis_options_carry_the_same_defaults()
    {
        var options = DocumentAnalysisOptions.Default;

        Assert.Equal(0.5f, DocumentAnalysisOptions.DefaultLayoutScoreThreshold);
        Assert.Equal(0.5f, options.LayoutScoreThreshold);
        Assert.True(options.FilterOverlappingRegions);
        Assert.False(options.LayoutNms);
        Assert.Null(options.LayoutUnclipRatio);
        Assert.Equal(DocumentLayoutMergeMode.None, options.LayoutMergeMode);
        Assert.Equal(DocumentReadingOrder.Auto, options.ReadingOrder);
    }

    /// <summary>
    /// The enum pairs are checked inside one test rather than as a [Theory]: the engine-side enums are
    /// internal, so they cannot appear in a public test method's signature.
    /// </summary>
    [Fact]
    public void The_public_layout_knobs_reach_the_engine()
    {
        var mapped = EasyOcrSharp.Services.EasyOcrService.ToStructureOptions(
            new DocumentAnalysisOptions
            {
                LayoutScoreThreshold = 0.25f,
                FilterOverlappingRegions = false,
                LayoutNms = true,
                LayoutUnclipRatio = 1.15f,
            },
            logger: null);

        Assert.Equal(0.25f, mapped.LayoutScoreThreshold);
        Assert.False(mapped.FilterOverlappingRegions);
        Assert.True(mapped.LayoutNms);
        Assert.Equal(1.15f, mapped.LayoutUnclipRatio);
    }

    [Fact]
    public void Every_public_merge_mode_and_reading_order_maps_onto_its_engine_counterpart()
    {
        AssertMaps(DocumentLayoutMergeMode.None, LayoutMergeMode.None, DocumentReadingOrder.Auto, LayoutReadingOrder.Auto);
        AssertMaps(DocumentLayoutMergeMode.Union, LayoutMergeMode.Union, DocumentReadingOrder.XyCut, LayoutReadingOrder.XyCut);
        AssertMaps(DocumentLayoutMergeMode.Large, LayoutMergeMode.Large, DocumentReadingOrder.Model, LayoutReadingOrder.Model);
        AssertMaps(DocumentLayoutMergeMode.Small, LayoutMergeMode.Small, DocumentReadingOrder.Auto, LayoutReadingOrder.Auto);

        static void AssertMaps(
            DocumentLayoutMergeMode merge, LayoutMergeMode expectedMerge,
            DocumentReadingOrder order, LayoutReadingOrder expectedOrder)
        {
            var mapped = EasyOcrSharp.Services.EasyOcrService.ToStructureOptions(
                new DocumentAnalysisOptions { LayoutMergeMode = merge, ReadingOrder = order }, logger: null);

            Assert.Equal(expectedMerge, mapped.LayoutMergeMode);
            Assert.Equal(expectedOrder, mapped.ReadingOrder);
        }
    }

    [Fact]
    public void An_ordinary_page_of_disjoint_regions_passes_through_untouched()
    {
        var regions = new[]
        {
            Region(50, 40, 950, 90, StructureBlockType.Header, label: "header"),
            Region(50, 120, 950, 400),
            Region(50, 420, 950, 700),
            Region(50, 720, 950, 1300, StructureBlockType.Table, label: "table"),
        };

        Assert.Equal(regions, Run(regions, StructureOptions.Default));
    }

    [Fact]
    public void An_empty_region_list_is_returned_as_is()
    {
        Assert.Empty(Run(Array.Empty<LayoutRegion>(), StructureOptions.Default));
    }
}
