using EasyOcrSharp.Models;
using EasyOcrSharp.Structure;
using EasyOcrSharp.Structure.Layout;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for the EasyOcrSharp-specific parts of layout
/// post-processing: the per-call score threshold against the real <see cref="LayoutGraph.BuildRegions"/>,
/// and the mapping of the public <c>DocumentAnalysisOptions</c> knobs onto the engine. The clean-up chain
/// itself (overlap filter, NMS, unclip, merges, ordering) is covered by the PaddleOcrNet 2.1.0 suite ported
/// to <c>Structure/LayoutPostProcessingTests.cs</c>; the 2.0.2-era expectations that used to live here were
/// superseded by that release.
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
    public void The_public_analysis_options_carry_the_same_defaults()
    {
        var options = DocumentAnalysisOptions.Default;

        Assert.Equal(0.5f, DocumentAnalysisOptions.DefaultLayoutScoreThreshold);
        Assert.Equal(0.5f, options.LayoutScoreThreshold);
        Assert.True(options.FilterOverlappingRegions);
        // On by default since the PaddleOcrNet 2.1.0 engine update (PP-StructureV3.yaml ships layout_nms: True).
        // It must be true on the public options too: the mapper passes it explicitly, so a false here would
        // silently override the engine default for every AnalyzeDocumentAsync caller.
        Assert.True(options.LayoutNms);
        Assert.True(EasyOcrSharp.Services.EasyOcrService.ToStructureOptions(null, logger: null).LayoutNms);
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

}
