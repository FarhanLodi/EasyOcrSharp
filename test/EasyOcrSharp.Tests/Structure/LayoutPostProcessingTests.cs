using EasyOcrSharp.Structure.Engine.Models;
using EasyOcrSharp.Structure;
using EasyOcrSharp.Structure.Layout;
using Xunit;

using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for <see cref="LayoutPostProcessor"/> — the clean-up
/// chain that runs over the layout detections after score-thresholding: the overlapping-region filter, the
/// oversized-image drop, optional NMS, optional unclip, the containment merge modes, and the sort by the
/// model's own predicted reading order.
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
    private static StructureOptions Bare =>
        StructureOptions.Default with { FilterOverlappingRegions = false, LayoutNms = false };

    private static IReadOnlyList<LayoutRegion> Run(
        IEnumerable<LayoutRegion> regions, StructureOptions options, int width = 1000, int height = 1400) =>
        LayoutPostProcessor.Apply(regions.ToList(), options, width, height);

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
        // 20px of vertical overlap on 100px-tall boxes — nowhere near the 50% bar.
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

        // NMS (also on by default) would suppress the same duplicate, so it is disabled too to show the
        // overlap-filter knob acting on its own.
        var kept = Run(new[] { large, small }, Bare);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void Reference_regions_get_no_special_treatment()
    {
        // The pre-parity port unconditionally dropped small "reference" markers; PaddleX's
        // remove_overlap_blocks has no such rule, so disjoint reference regions all survive.
        var marker = Region(10, 10, 60, 30, StructureBlockType.Reference, label: "reference");
        var content = Region(10, 200, 400, 300, StructureBlockType.Reference, label: "reference_content");

        var kept = Run(new[] { marker, content }, StructureOptions.Default);

        Assert.Equal(new[] { marker, content }, kept);
    }

    [Fact]
    public void Slim_regions_survive_the_overlap_filter()
    {
        // The old 6px sliver rule is gone (no PaddleX counterpart): a 4px-wide separator-like region that
        // overlaps nothing is kept.
        var sliver = Region(10, 10, 14, 300);   // 4px wide
        var normal = Region(100, 10, 400, 300);

        var kept = Run(new[] { sliver, normal }, StructureOptions.Default);

        Assert.Equal(new[] { sliver, normal }, kept);
    }

    [Fact]
    public void Overlap_filter_drops_the_image_of_a_mixed_pair_regardless_of_size()
    {
        // remove_overlap_blocks' one label rule: when exactly one of an overlapping pair is 'image', the
        // image loses even though it is the LARGER region here (the generic rule would drop the table).
        // Geometry: intersection is 210x280 = 58800 px², 52.5% of the smaller (table) area — over the 0.5
        // bar but under the 90% containment bar, so the per-class merge stage leaves the pair alone.
        var image = Region(10, 10, 410, 310, StructureBlockType.Figure, label: "image");
        var table = Region(200, 20, 600, 300, StructureBlockType.Table, label: "table");

        var kept = Run(new[] { image, table }, StructureOptions.Default);

        Assert.Equal(new[] { table }, kept);
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
    public void Overlap_filter_treats_inline_and_display_formulas_alike()
    {
        // The old port gave inline_formula a lower absorption bar than display_formula; PaddleX's
        // remove_overlap_blocks knows no formula labels, so at 60% overlap both variants lose to the
        // (larger) paragraph under the ordinary smaller-region rule.
        var paragraph = Region(10, 10, 410, 110, StructureBlockType.Text, label: "text");
        var inline = Region(350, 20, 450, 100, StructureBlockType.Formula, label: "inline_formula");
        var display = Region(350, 20, 450, 100, StructureBlockType.Formula, label: "display_formula");

        Assert.Equal(new[] { paragraph }, Run(new[] { paragraph, inline }, StructureOptions.Default));
        Assert.Equal(new[] { paragraph }, Run(new[] { paragraph, display }, StructureOptions.Default));
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
    // NMS (on by default, PP-StructureV3 layout_nms: True)
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Nms_is_on_by_default_and_suppresses_the_lower_score()
    {
        var strong = Region(10, 10, 210, 110, score: 0.9f, classId: 3);
        var weak = Region(20, 20, 220, 120, score: 0.6f, classId: 3);   // 0.75 IoU, same class

        var kept = Run(
            new[] { strong, weak }, StructureOptions.Default with { FilterOverlappingRegions = false });
        Assert.Equal(new[] { strong }, kept);

        Assert.Equal(2, Run(new[] { strong, weak }, Bare).Count);   // Bare turns it off
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
    public void Default_options_match_the_PP_StructureV3_pipeline_config()
    {
        var options = StructureOptions.Default;

        Assert.True(options.FilterOverlappingRegions);
        Assert.True(options.LayoutNms);                             // layout_nms: True
        Assert.Null(options.LayoutUnclipRatio);
        Assert.Equal(LayoutMergeMode.None, options.LayoutMergeMode); // per-class defaults apply
        Assert.Null(options.LayoutClassThresholds);                  // per-class defaults apply
        Assert.Null(options.LayoutClassMergeModes);
        Assert.Equal(LayoutReadingOrder.Auto, options.ReadingOrder); // resolves to XY-Cut++
        Assert.True(options.UseTableOrientationClassification);
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

    // -----------------------------------------------------------------------------------------------------
    // per-class score thresholds (PP-StructureV3 threshold dict)
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Per_class_thresholds_keep_low_scoring_titles_but_drop_low_scoring_text()
    {
        // Default floors: paragraph_title 0.3, text 0.4 — a 0.35 score passes the first and fails the
        // second (strictly-greater comparison, like PaddleX's dict-threshold branch).
        var title = Region(10, 10, 200, 60, StructureBlockType.Title, score: 0.35f, label: "paragraph_title");
        var text = Region(10, 100, 200, 160, score: 0.35f, label: "text");

        var kept = Run(new[] { title, text }, Bare);

        Assert.Equal(new[] { title }, kept);
    }

    [Fact]
    public void Custom_class_thresholds_replace_the_defaults_entirely()
    {
        // A user dictionary REPLACES the built-in per-class floors: text drops to 0.1, while
        // paragraph_title (absent from the dictionary) falls back to the global 0.5.
        var title = Region(10, 10, 200, 60, StructureBlockType.Title, score: 0.35f, label: "paragraph_title");
        var text = Region(10, 100, 200, 160, score: 0.35f, label: "text");
        var options = Bare with
        {
            LayoutClassThresholds = new Dictionary<string, float> { ["text"] = 0.1f },
        };

        var kept = Run(new[] { title, text }, options);

        Assert.Equal(new[] { text }, kept);
    }

    [Fact]
    public void An_empty_threshold_dictionary_disables_the_per_class_defaults()
    {
        var title = Region(10, 10, 200, 60, StructureBlockType.Title, score: 0.35f, label: "paragraph_title");
        var options = Bare with { LayoutClassThresholds = new Dictionary<string, float>() };

        Assert.Empty(Run(new[] { title }, options));                          // falls to the global 0.5
        Assert.Equal(0.5f, LayoutPostProcessor.DetectionScoreFloor(options)); // and the detector floor too
    }

    [Fact]
    public void Detection_score_floor_is_the_minimum_of_global_and_per_class_floors()
    {
        // With the built-in defaults active the lowest floor is paragraph_title/formula at 0.3, so the
        // detector must be fed 0.3 or candidates in the 0.3–0.5 band never reach the per-class filter.
        Assert.Equal(0.3f, LayoutPostProcessor.DetectionScoreFloor(StructureOptions.Default));
    }

    // -----------------------------------------------------------------------------------------------------
    // overlap-filter 0.5 bar (strict) and the exact-tie drop
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Overlap_filter_bar_is_strictly_greater_than_half_the_smaller_area()
    {
        var options = StructureOptions.Default with { LayoutNms = false };
        var a = Region(0, 0, 100, 100);

        // Exactly 50% of the smaller region: NOT dropped (python: overlap_ratio > threshold).
        var half = Region(50, 0, 150, 100);
        Assert.Equal(2, Run(new[] { a, half }, options).Count);

        // 51%: dropped — and on an exact area tie python drops the EARLIER block (area1 <= area2).
        var justOver = Region(49, 0, 149, 100);
        Assert.Equal(new[] { justOver }, Run(new[] { a, justOver }, options));
    }

    // -----------------------------------------------------------------------------------------------------
    // label post-fixes (pipeline_v2.py standardized_data)
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_footnote_above_the_lowest_text_block_is_relabelled_to_text()
    {
        var text = Region(100, 100, 900, 1000, label: "text");
        var midFootnote = Region(100, 500, 900, 540, StructureBlockType.Footnote, label: "footnote");
        var realFootnote = Region(100, 1100, 900, 1140, StructureBlockType.Footnote, label: "footnote");

        var fixedUp = LayoutPostProcessor.ApplyLabelPostFixes(
            new[] { text, midFootnote, realFootnote }, 1000, 1400);

        Assert.Equal("text", fixedUp[1].RawLabel);
        Assert.Equal(StructureBlockType.Text, fixedUp[1].Type);
        // The footnote genuinely at the page bottom keeps its label.
        Assert.Equal("footnote", fixedUp[2].RawLabel);
        Assert.Equal(StructureBlockType.Footnote, fixedUp[2].Type);
    }

    [Fact]
    public void A_lone_large_paragraph_title_is_promoted_to_doc_title()
    {
        // No doc_title on the page, exactly one paragraph_title, and its area (120000) exceeds 30% of the
        // largest block's area (240000 * 0.3 = 72000): promoted.
        var title = Region(100, 50, 900, 200, StructureBlockType.Title, label: "paragraph_title");
        var text = Region(100, 300, 900, 600, label: "text");

        var fixedUp = LayoutPostProcessor.ApplyLabelPostFixes(new[] { title, text }, 1000, 1400);

        Assert.Equal("doc_title", fixedUp[0].RawLabel);
        Assert.Equal(StructureBlockType.DocTitle, fixedUp[0].Type);
    }

    [Fact]
    public void Title_promotion_requires_a_lone_large_title_and_no_existing_doc_title()
    {
        var bigTitle = Region(100, 50, 900, 200, StructureBlockType.Title, label: "paragraph_title");
        var text = Region(100, 300, 900, 600, label: "text");

        // Two paragraph_titles: neither is promoted (and the same instance comes back untouched).
        var secondTitle = Region(100, 700, 900, 850, StructureBlockType.Title, label: "paragraph_title");
        var twoTitles = new[] { bigTitle, secondTitle, text };
        Assert.Same(twoTitles, LayoutPostProcessor.ApplyLabelPostFixes(twoTitles, 1000, 1400));

        // An existing doc_title blocks the promotion.
        var docTitle = Region(100, 0, 900, 40, StructureBlockType.DocTitle, label: "doc_title");
        var withDocTitle = new[] { docTitle, bigTitle, text };
        Assert.Same(withDocTitle, LayoutPostProcessor.ApplyLabelPostFixes(withDocTitle, 1000, 1400));

        // A small title (area 24000 <= 72000) stays a paragraph_title.
        var smallTitle = Region(100, 50, 500, 110, StructureBlockType.Title, label: "paragraph_title");
        var withSmallTitle = new[] { smallTitle, text };
        Assert.Same(withSmallTitle, LayoutPostProcessor.ApplyLabelPostFixes(withSmallTitle, 1000, 1400));
    }
}
