using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Layout;

/// <summary>
/// The clean-up chain that runs over the layout detections <i>after</i> they have been score-thresholded.
/// The detectors emit a fixed top-k of candidate boxes with no NMS, so the same area of the page is routinely
/// proposed several times under different labels; these passes turn that raw candidate set into the region
/// list callers actually want, mirroring the PP-StructureV3 post-processing
/// (PaddleX <c>object_detection/processors.py</c> <c>DetPostProcess</c> + <c>layout_parsing/utils.py</c>
/// <c>remove_overlap_blocks</c>).
/// <para>
/// Applied in order: (0) the per-class score thresholds (PP-StructureV3's dict-typed <c>threshold</c>),
/// (1) optional NMS (on by default — <c>layout_nms: True</c> in the pipeline config), (2) the always-on
/// oversized-image drop, (3) the containment merge — per-class by default (<c>layout_merge_bboxes_mode</c>
/// dict), (4) sort by the model's predicted reading order, (5) optional unclip, (6) the overlapping-region
/// filter (on by default). Each step is gated by the matching <see cref="StructureOptions"/> knob.
/// </para>
/// <para>
/// Boxes keep the sub-pixel corners the detector produced rather than being rounded to whole pixels: overlap
/// ratios shift by a negligible amount and the crops handed to the table/formula recognizers stay exact. Only
/// the box output is consumed — the segmentation masks some layout graphs also emit are ignored, so regions
/// stay axis-aligned rectangles. Filtering decisions key off <see cref="LayoutRegion.RawLabel"/>, the model's
/// own label name, because <see cref="StructureBlockType"/> deliberately collapses distinctions the filters
/// depend on; when a region has no raw label the mapped type stands in — see <see cref="LabelKey"/>.
/// </para>
/// </summary>
internal static class LayoutPostProcessor
{
    /// <summary>IoU above which NMS suppresses a lower-scoring region of the <b>same</b> class.</summary>
    private const double NmsIouSameClass = 0.6;

    /// <summary>IoU above which NMS suppresses a lower-scoring region of a <b>different</b> class.</summary>
    private const double NmsIouDifferentClass = 0.98;

    /// <summary>Fraction of the page above which an <c>image</c> region is treated as a false positive, on a portrait page.</summary>
    private const double OversizedImageAreaPortrait = 0.93;

    /// <summary>Fraction of the page above which an <c>image</c> region is treated as a false positive, on a landscape page.</summary>
    private const double OversizedImageAreaLandscape = 0.82;

    /// <summary>Intersection-over-own-area at which one region counts as contained by another.</summary>
    private const double ContainmentRatio = 0.9;

    /// <summary>
    /// Overlap (relative to the smaller region) above which the overlap filter drops one of the pair.
    /// PP-StructureV3 calls <c>remove_overlap_blocks</c> with <c>threshold=0.5</c>
    /// (<c>pipeline_v2.py</c>).
    /// </summary>
    private const double DuplicateOverlapRatio = 0.5;

    /// <summary>
    /// A lone <c>paragraph_title</c> is promoted to <c>doc_title</c> when its area exceeds this fraction of
    /// the largest block's area (PaddleX <c>title_conversion_area_ratio_threshold</c>, <c>setting.py</c>).
    /// </summary>
    private const double TitleConversionAreaRatioThreshold = 0.3;

    /// <summary>
    /// The PP-StructureV3 per-class confidence floors (<c>PP-StructureV3.yaml</c> <c>threshold</c> dict),
    /// keyed by paddle label name — only the classes whose yaml floor differs from the yaml's 0.5 global
    /// are listed, so every other class keeps following <see cref="StructureOptions.LayoutScoreThreshold"/>.
    /// The yaml is written against the PP-DocLayout_plus-L vocabulary; its <c>formula</c> entry is also
    /// applied to the PP-DocLayoutV3 vocabulary's <c>display_formula</c> / <c>inline_formula</c> split of
    /// the same class. Used when <see cref="StructureOptions.LayoutClassThresholds"/> is null.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, float> DefaultClassThresholds =
        new Dictionary<string, float>(StringComparer.Ordinal)
        {
            ["paragraph_title"] = 0.3f,
            ["text"] = 0.4f,
            ["formula"] = 0.3f,
            ["display_formula"] = 0.3f,
            ["inline_formula"] = 0.3f,
            ["seal"] = 0.45f,
        };

    /// <summary>
    /// The PP-StructureV3 per-class containment-merge modes (<c>PP-StructureV3.yaml</c>
    /// <c>layout_merge_bboxes_mode</c> dict), keyed by paddle label name: <c>large</c> for
    /// <c>paragraph_title</c>, <c>image</c>, <c>formula</c> (plus the PP-DocLayoutV3
    /// <c>display_formula</c> / <c>inline_formula</c> split) and <c>chart</c>; every class absent from the
    /// dictionary is <c>union</c> (nesting left alone). Used when both
    /// <see cref="StructureOptions.LayoutClassMergeModes"/> is null and
    /// <see cref="StructureOptions.LayoutMergeMode"/> is <see cref="LayoutMergeMode.None"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, LayoutMergeMode> DefaultClassMergeModes =
        new Dictionary<string, LayoutMergeMode>(StringComparer.Ordinal)
        {
            ["paragraph_title"] = LayoutMergeMode.Large,
            ["image"] = LayoutMergeMode.Large,
            ["formula"] = LayoutMergeMode.Large,
            ["display_formula"] = LayoutMergeMode.Large,
            ["inline_formula"] = LayoutMergeMode.Large,
            ["chart"] = LayoutMergeMode.Large,
        };

    /// <summary>
    /// Runs the enabled clean-up passes over <paramref name="regions"/> (already floored at
    /// <see cref="DetectionScoreFloor"/> by the detector) and returns the surviving regions, sorted by the
    /// model's predicted reading order when it supplies one. The input list is never mutated.
    /// </summary>
    /// <param name="regions">The detections, in detector order.</param>
    /// <param name="options">Supplies the threshold / NMS / unclip / merge / overlap-filtering knobs.</param>
    /// <param name="pageWidth">Width of the page the detector ran on, in pixels.</param>
    /// <param name="pageHeight">Height of the page the detector ran on, in pixels.</param>
    public static IReadOnlyList<LayoutRegion> Apply(
        IReadOnlyList<LayoutRegion> regions, StructureOptions options, int pageWidth, int pageHeight)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(options);
        if (regions.Count == 0) return regions;

        var kept = ApplyScoreThresholds(new List<LayoutRegion>(regions), options);

        if (options.LayoutNms)
        {
            kept = SuppressByScore(kept);
        }

        kept = DropOversizedImages(kept, pageWidth, pageHeight);

        var classMergeModes = EffectiveClassMergeModes(options);
        if (classMergeModes is not null)
        {
            kept = MergeContainedPerClass(kept, classMergeModes);
        }
        else if (options.LayoutMergeMode is LayoutMergeMode.Large or LayoutMergeMode.Small)
        {
            kept = MergeContained(kept, options.LayoutMergeMode);
        }

        // Sort by the model's order column here, before the overlap filter: that filter's pairwise sweep is
        // order-sensitive (a region dropped early short-circuits its remaining pairs), so the sequence the
        // regions are in when it runs is part of its result.
        //
        // Skipped when the caller asked for XY-cut explicitly. Running it unconditionally meant `XyCut` still
        // had the regions permuted by the model's predicted order first, which changes both the overlap
        // sweep's outcome and XyCutOrderer's tie-breaks -- so results were not, as documented, identical
        // across layout models.
        if (options.ReadingOrder != LayoutReadingOrder.XyCut)
        {
            kept = SortByModelOrder(kept);
        }

        if (options.LayoutUnclipRatio is > 0)
        {
            kept = Unclip(kept, options.LayoutUnclipRatio.Value, pageWidth, pageHeight);
        }

        if (options.FilterOverlappingRegions)
        {
            kept = DropOverlapping(kept);
        }

        return kept;
    }

    // =================================================================================================
    // (0) per-class score thresholds
    // =================================================================================================

    /// <summary>
    /// The score floor the engine should hand to <see cref="ILayoutDetector.Detect"/>: the minimum of
    /// <see cref="StructureOptions.LayoutScoreThreshold"/> and every per-class floor in effect. Passing this
    /// (rather than the global threshold) keeps the detector from discarding candidates that a lower
    /// per-class floor — e.g. the PP-StructureV3 default 0.3 for <c>paragraph_title</c> — would keep;
    /// <see cref="Apply"/> then re-thresholds each region at its exact class floor.
    /// </summary>
    public static float DetectionScoreFloor(StructureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        float floor = options.LayoutScoreThreshold;
        foreach (float threshold in EffectiveClassThresholds(options).Values)
        {
            floor = Math.Min(floor, threshold);
        }
        return floor;
    }

    /// <summary>
    /// The per-class confidence floors in effect for <paramref name="options"/>:
    /// <see cref="StructureOptions.LayoutClassThresholds"/> when the caller supplied one (keys normalized
    /// via <see cref="LayoutLabelMap.Normalize"/>), otherwise the built-in
    /// <see cref="DefaultClassThresholds"/>. Classes absent from the returned dictionary use
    /// <see cref="StructureOptions.LayoutScoreThreshold"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, float> EffectiveClassThresholds(StructureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var source = options.LayoutClassThresholds;
        if (source is null) return DefaultClassThresholds;

        var normalized = new Dictionary<string, float>(source.Count, StringComparer.Ordinal);
        foreach (var kvp in source)
        {
            normalized[LayoutLabelMap.Normalize(kvp.Key)] = kvp.Value;
        }
        return normalized;
    }

    /// <summary>
    /// The per-class containment-merge modes in effect for <paramref name="options"/>:
    /// <see cref="StructureOptions.LayoutClassMergeModes"/> when the caller supplied one (keys normalized),
    /// the built-in <see cref="DefaultClassMergeModes"/> when it is null and
    /// <see cref="StructureOptions.LayoutMergeMode"/> is <see cref="LayoutMergeMode.None"/>, and
    /// <c>null</c> when an explicit uniform <see cref="StructureOptions.LayoutMergeMode"/> should apply
    /// instead.
    /// </summary>
    public static IReadOnlyDictionary<string, LayoutMergeMode>? EffectiveClassMergeModes(
        StructureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var source = options.LayoutClassMergeModes;
        if (source is null)
        {
            return options.LayoutMergeMode == LayoutMergeMode.None ? DefaultClassMergeModes : null;
        }

        var normalized = new Dictionary<string, LayoutMergeMode>(source.Count, StringComparer.Ordinal);
        foreach (var kvp in source)
        {
            normalized[LayoutLabelMap.Normalize(kvp.Key)] = kvp.Value;
        }
        return normalized;
    }

    /// <summary>
    /// Re-thresholds each region at its class's own confidence floor — the per-class dictionary first,
    /// <see cref="StructureOptions.LayoutScoreThreshold"/> for classes it does not name (PaddleX's
    /// dict-threshold branch in <c>DetPostProcess.apply</c>, score kept when strictly greater).
    /// </summary>
    private static List<LayoutRegion> ApplyScoreThresholds(List<LayoutRegion> regions, StructureOptions options)
    {
        var thresholds = EffectiveClassThresholds(options);
        var kept = new List<LayoutRegion>(regions.Count);
        foreach (var region in regions)
        {
            float floor = thresholds.TryGetValue(LabelKey(region), out float perClass)
                ? perClass
                : options.LayoutScoreThreshold;
            if (region.Score > floor)
            {
                kept.Add(region);
            }
        }
        return kept;
    }

    // =================================================================================================
    // (1) non-maximum suppression
    // =================================================================================================

    /// <summary>
    /// Greedy score-ordered suppression: walks the regions from the highest score down, keeping each and
    /// discarding every lower-scoring region that overlaps it by more than the IoU threshold for the pair —
    /// 0.6 when both carry the same class id, 0.98 when they differ, so cross-class duplicates are only
    /// removed when they are near-identical.
    /// </summary>
    private static List<LayoutRegion> SuppressByScore(List<LayoutRegion> regions)
    {
        var pending = Enumerable.Range(0, regions.Count)
            .OrderByDescending(i => regions[i].Score)
            .ToList();
        var selected = new List<LayoutRegion>(regions.Count);

        while (pending.Count > 0)
        {
            var current = regions[pending[0]];
            selected.Add(current);

            var survivors = new List<int>(pending.Count - 1);
            for (int k = 1; k < pending.Count; k++)
            {
                var candidate = regions[pending[k]];
                double threshold = candidate.RawClassId == current.RawClassId
                    ? NmsIouSameClass
                    : NmsIouDifferentClass;
                if (IntersectionOverUnion(current.Bounds, candidate.Bounds) < threshold)
                {
                    survivors.Add(pending[k]);
                }
            }
            pending = survivors;
        }

        return selected;
    }

    // =================================================================================================
    // (2) oversized-image drop
    // =================================================================================================

    /// <summary>
    /// Drops <c>image</c> regions that cover essentially the whole page — the detector's "this entire scan is
    /// one photograph" false positive. The area budget is 82% on a landscape page and 93% otherwise. Skipped
    /// when there is a single region, and reverted wholesale if it would empty the list. Only regions the
    /// model itself labelled <c>image</c> are eligible, so the PicoDet vocabularies, which name the class
    /// <c>figure</c>, are left untouched.
    /// </summary>
    private static List<LayoutRegion> DropOversizedImages(List<LayoutRegion> regions, int pageWidth, int pageHeight)
    {
        if (regions.Count <= 1) return regions;

        double areaThreshold = pageWidth > pageHeight ? OversizedImageAreaLandscape : OversizedImageAreaPortrait;
        double pageArea = (double)pageWidth * pageHeight;

        var kept = new List<LayoutRegion>(regions.Count);
        foreach (var region in regions)
        {
            if (LabelKey(region) != "image")
            {
                kept.Add(region);
                continue;
            }

            double width = Math.Min(pageWidth, region.Bounds.MaxX) - Math.Max(0, region.Bounds.MinX);
            double height = Math.Min(pageHeight, region.Bounds.MaxY) - Math.Max(0, region.Bounds.MinY);
            if (width * height <= areaThreshold * pageArea)
            {
                kept.Add(region);
            }
        }

        return kept.Count == 0 ? regions : kept;
    }

    // =================================================================================================
    // (3) containment merge
    // =================================================================================================

    /// <summary>
    /// Resolves nested regions uniformly: <see cref="LayoutMergeMode.Large"/> drops every region contained by
    /// another (keeping the enclosing block), <see cref="LayoutMergeMode.Small"/> keeps the regions that
    /// contain nothing, plus those that are themselves contained (keeping the inner blocks). "Contained" means
    /// at least 90% of the inner region's own area falls inside the outer one. When the model's vocabulary has
    /// a <c>formula</c> class, a formula region is never treated as contained by a non-formula region, so
    /// formulas are not swallowed by the paragraph around them.
    /// </summary>
    private static List<LayoutRegion> MergeContained(List<LayoutRegion> regions, LayoutMergeMode mode)
    {
        int n = regions.Count;
        var containsOther = new bool[n];
        var containedByOther = new bool[n];
        bool protectFormulas = regions.Any(r => IsFormulaLabel(LabelKey(r)));

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                if (protectFormulas && IsFormulaLabel(LabelKey(regions[i])) && !IsFormulaLabel(LabelKey(regions[j])))
                {
                    continue;
                }

                // Containment has to be a strict order, not a symmetric relation. Two near-identical
                // duplicates each cover >= ContainmentRatio of the other, so without this tie-break both
                // would be marked contained and Large would drop *both* — losing the region entirely.
                // The larger box is the container; equal areas fall back to the lower index.
                double areaI = Area(regions[i].Bounds);
                double areaJ = Area(regions[j].Bounds);
                if (areaJ < areaI || (areaJ == areaI && j > i)) continue;

                if (IsContained(regions[i].Bounds, regions[j].Bounds))
                {
                    containedByOther[i] = true;
                    containsOther[j] = true;
                }
            }
        }

        var kept = new List<LayoutRegion>(n);
        for (int i = 0; i < n; i++)
        {
            bool keep = mode == LayoutMergeMode.Large
                ? !containedByOther[i]
                : !containsOther[i] || containedByOther[i];
            if (keep) kept.Add(regions[i]);
        }
        return kept;
    }

    /// <summary>
    /// The per-class containment merge, mirroring PaddleX's dict-typed <c>layout_merge_bboxes_mode</c>
    /// (<c>DetPostProcess.apply</c> + <c>check_containment</c> with a category filter): for each class mapped
    /// to <see cref="LayoutMergeMode.Large"/>, every region contained inside a region <i>of that class</i> is
    /// dropped; for each class mapped to <see cref="LayoutMergeMode.Small"/>, a region containing a region of
    /// that class is dropped unless it is itself contained. Classes absent from the dictionary (or mapped to
    /// <see cref="LayoutMergeMode.Union"/> / <see cref="LayoutMergeMode.None"/>) leave nesting alone. Each
    /// class's containment sweep runs over the full input set and the drop masks are ANDed, exactly as
    /// Python accumulates its <c>keep_mask</c>. The formula protection of <see cref="MergeContained"/>
    /// applies here too (only when the vocabulary's label is literally <c>formula</c>, matching
    /// <c>labels.index("formula")</c>).
    /// </summary>
    private static List<LayoutRegion> MergeContainedPerClass(
        List<LayoutRegion> regions, IReadOnlyDictionary<string, LayoutMergeMode> modes)
    {
        int n = regions.Count;
        if (n == 0) return regions;

        var keep = new bool[n];
        Array.Fill(keep, true);
        bool protectFormulas = regions.Any(r => LabelKey(r) == "formula");

        foreach (var (label, mode) in modes)
        {
            if (mode is not (LayoutMergeMode.Large or LayoutMergeMode.Small)) continue;

            var containsOther = new bool[n];
            var containedByOther = new bool[n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    if (protectFormulas
                        && LabelKey(regions[i]) == "formula" && LabelKey(regions[j]) != "formula")
                    {
                        continue;
                    }

                    // "large" keys off the OUTER region's class, "small" off the INNER region's class.
                    string categoryLabel = mode == LayoutMergeMode.Large
                        ? LabelKey(regions[j])
                        : LabelKey(regions[i]);
                    if (categoryLabel != label) continue;

                    if (IsContained(regions[i].Bounds, regions[j].Bounds))
                    {
                        containedByOther[i] = true;
                        containsOther[j] = true;
                    }
                }
            }

            for (int i = 0; i < n; i++)
            {
                keep[i] &= mode == LayoutMergeMode.Large
                    ? !containedByOther[i]
                    : !containsOther[i] || containedByOther[i];
            }
        }

        var kept = new List<LayoutRegion>(n);
        for (int i = 0; i < n; i++)
        {
            if (keep[i]) kept.Add(regions[i]);
        }
        return kept;
    }

    // =================================================================================================
    // (4) reading order from the model
    // =================================================================================================

    /// <summary>
    /// Sorts the regions by <see cref="LayoutRegion.OrderIndex"/> when <b>every</b> region carries one (the
    /// 7-wide PP-DocLayoutV3 rows); otherwise returns them untouched for the XY-cut orderer to sequence.
    /// </summary>
    private static List<LayoutRegion> SortByModelOrder(List<LayoutRegion> regions)
    {
        if (regions.Count < 2 || regions.Any(r => r.OrderIndex is null)) return regions;
        return regions.OrderBy(r => r.OrderIndex!.Value).ToList();
    }

    // =================================================================================================
    // (5) unclip
    // =================================================================================================

    /// <summary>
    /// Grows every region about its own centre by <paramref name="ratio"/> (1.0 leaves it unchanged, 1.1 adds
    /// 10% to each side), then re-clamps to the page and drops anything that collapsed. Useful when tight
    /// boxes clip glyph ascenders/descenders out of the crops handed to the recognizers.
    /// </summary>
    private static List<LayoutRegion> Unclip(List<LayoutRegion> regions, float ratio, int pageWidth, int pageHeight)
    {
        var expanded = new List<LayoutRegion>(regions.Count);
        foreach (var region in regions)
        {
            var box = region.Bounds;
            double halfWidth = box.Width * ratio / 2;
            double halfHeight = box.Height * ratio / 2;

            double minX = Math.Clamp(box.CenterX - halfWidth, 0, pageWidth);
            double minY = Math.Clamp(box.CenterY - halfHeight, 0, pageHeight);
            double maxX = Math.Clamp(box.CenterX + halfWidth, 0, pageWidth);
            double maxY = Math.Clamp(box.CenterY + halfHeight, 0, pageHeight);
            if (maxX <= minX || maxY <= minY) continue;

            expanded.Add(region with { Bounds = new OcrBoundingBox(minX, minY, maxX, maxY) });
        }
        return expanded;
    }

    // =================================================================================================
    // (6) overlapping-region filter
    // =================================================================================================

    /// <summary>
    /// PaddleX's <c>remove_overlap_blocks</c> (<c>layout_parsing/utils.py</c>) as PP-StructureV3 runs it
    /// (<c>threshold=0.5, smaller=True</c>): sweeping the pairs in order and skipping pairs where either
    /// region is already dropped, a pair overlapping by <i>more than</i> 50% of the smaller region's area
    /// loses one member — the <c>image</c> region when exactly one of the pair is labelled <c>image</c>
    /// (regardless of size), otherwise the smaller region (the earlier one on an exact area tie).
    /// </summary>
    private static List<LayoutRegion> DropOverlapping(List<LayoutRegion> regions)
    {
        var dropped = new bool[regions.Count];

        for (int i = 0; i < regions.Count; i++)
        {
            for (int j = i + 1; j < regions.Count; j++)
            {
                if (dropped[i] || dropped[j]) continue;

                var a = regions[i].Bounds;
                var b = regions[j].Bounds;
                if (OverlapRatioOfSmaller(a, b) <= DuplicateOverlapRatio) continue;

                bool aIsImage = LabelKey(regions[i]) == "image";
                bool bIsImage = LabelKey(regions[j]) == "image";
                if (aIsImage != bIsImage)
                {
                    // Exactly one of the pair is an image: the image loses, whatever its size — a text or
                    // table block detected on top of a picture wins over the picture.
                    dropped[aIsImage ? i : j] = true;
                }
                else
                {
                    dropped[Area(a) <= Area(b) ? i : j] = true;
                }
            }
        }

        var kept = new List<LayoutRegion>(regions.Count);
        for (int i = 0; i < regions.Count; i++)
        {
            if (!dropped[i]) kept.Add(regions[i]);
        }
        return kept;
    }

    // =================================================================================================
    // label post-fixes
    // =================================================================================================

    /// <summary>
    /// The PP-StructureV3 label corrections (<c>pipeline_v2.py</c>, <c>standardized_data</c>) the engine
    /// applies after the overlap filter: (a) any <c>footnote</c> whose bottom edge sits <i>above</i> the
    /// bottom of the lowest <c>text</c> block is relabelled to <c>text</c> — real footnotes live at the page
    /// bottom; (b) when the page has no <c>doc_title</c> and exactly one <c>paragraph_title</c> whose area
    /// exceeds 30% of the largest block's area, that title is promoted to <c>doc_title</c>. Returns the input
    /// list unchanged (same instance) when neither fix fires; relabelled regions get both their
    /// <see cref="LayoutRegion.RawLabel"/> and mapped <see cref="LayoutRegion.Type"/> rewritten.
    /// </summary>
    /// <param name="blocks">The post-processed regions, in reading order.</param>
    /// <param name="pageWidth">Width of the page, in pixels (reserved — the current fixes are geometric
    /// relative to the blocks themselves).</param>
    /// <param name="pageHeight">Height of the page, in pixels (reserved, as above).</param>
    public static IReadOnlyList<LayoutRegion> ApplyLabelPostFixes(
        IReadOnlyList<LayoutRegion> blocks, int pageWidth, int pageHeight)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count == 0) return blocks;

        double bottomTextYMax = 0;
        double maxBlockArea = 0;
        int docTitleCount = 0;
        var footnoteIndexes = new List<int>();
        var paragraphTitleIndexes = new List<int>();

        for (int i = 0; i < blocks.Count; i++)
        {
            var region = blocks[i];
            maxBlockArea = Math.Max(maxBlockArea, Area(region.Bounds));
            switch (LabelKey(region))
            {
                case "footnote":
                    footnoteIndexes.Add(i);
                    break;
                case "paragraph_title":
                    paragraphTitleIndexes.Add(i);
                    break;
                case "text":
                    bottomTextYMax = Math.Max(bottomTextYMax, region.Bounds.MaxY);
                    break;
                case "doc_title":
                    docTitleCount++;
                    break;
            }
        }

        LayoutRegion[]? updated = null;

        // (a) footnotes above the lowest text block's bottom are body text, not footnotes.
        foreach (int index in footnoteIndexes)
        {
            if (blocks[index].Bounds.MaxY < bottomTextYMax)
            {
                updated ??= blocks.ToArray();
                updated[index] = updated[index] with
                {
                    RawLabel = "text",
                    Type = StructureBlockType.Text,
                };
            }
        }

        // (b) a lone, large paragraph_title on a page with no doc_title is the document title.
        if (docTitleCount == 0 && paragraphTitleIndexes.Count == 1)
        {
            int index = paragraphTitleIndexes[0];
            if (Area(blocks[index].Bounds) > maxBlockArea * TitleConversionAreaRatioThreshold)
            {
                updated ??= blocks.ToArray();
                updated[index] = updated[index] with
                {
                    RawLabel = "doc_title",
                    Type = StructureBlockType.DocTitle,
                };
            }
        }

        return updated ?? blocks;
    }

    // =================================================================================================
    // geometry + label helpers
    // =================================================================================================

    /// <summary>
    /// The label this region is filtered by: the model's own <see cref="LayoutRegion.RawLabel"/> when the
    /// label sidecar supplied one — so <c>reference</c> vs <c>reference_content</c> and <c>inline_formula</c>
    /// vs <c>display_formula</c> stay distinguishable — falling back to the canonical paddle name for the
    /// mapped <see cref="StructureBlockType"/> when it did not.
    /// </summary>
    /// <summary>
    /// Whether <paramref name="label"/> names a formula class. The vocabularies disagree: the 23-class
    /// PicoDet list says <c>formula</c>, while PP-DocLayoutV3 splits it into <c>display_formula</c> and
    /// <c>inline_formula</c> and never emits a bare <c>formula</c>. Matching only the bare name silently
    /// disabled formula protection on the default (V3) model.
    /// </summary>
    private static bool IsFormulaLabel(string label) =>
        label is "formula" or "display_formula" or "inline_formula" or "interline_equation";

    private static string LabelKey(LayoutRegion region) => region.RawLabel ?? region.Type switch
    {
        StructureBlockType.Text => "text",
        StructureBlockType.Title => "paragraph_title",
        StructureBlockType.DocTitle => "doc_title",
        StructureBlockType.Figure => "image",
        StructureBlockType.FigureCaption => "figure_title",
        StructureBlockType.Table => "table",
        StructureBlockType.TableCaption => "table_title",
        StructureBlockType.Seal => "seal",
        StructureBlockType.Chart => "chart",
        StructureBlockType.Reference => "reference",
        StructureBlockType.Formula => "formula",
        StructureBlockType.FormulaNumber => "formula_number",
        StructureBlockType.Header => "header",
        StructureBlockType.Footer => "footer",
        StructureBlockType.Footnote => "footnote",
        StructureBlockType.PageNumber => "number",
        StructureBlockType.Abstract => "abstract",
        StructureBlockType.Algorithm => "algorithm",
        StructureBlockType.Aside => "aside_text",
        _ => "other",
    };

    /// <summary>Region area in square pixels.</summary>
    private static double Area(OcrBoundingBox box) => box.Width * box.Height;

    /// <summary>
    /// Intersection over union, counting each side inclusively (<c>max - min + 1</c>) so the NMS thresholds
    /// keep their conventional meaning.
    /// </summary>
    private static double IntersectionOverUnion(OcrBoundingBox a, OcrBoundingBox b)
    {
        double interWidth = Math.Max(0, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX) + 1);
        double interHeight = Math.Max(0, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY) + 1);
        double intersection = interWidth * interHeight;

        double union = (a.MaxX - a.MinX + 1) * (a.MaxY - a.MinY + 1)
                     + (b.MaxX - b.MinX + 1) * (b.MaxY - b.MinY + 1)
                     - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    /// <summary>Area of the overlap between two regions.</summary>
    private static double IntersectionArea(OcrBoundingBox a, OcrBoundingBox b)
    {
        double width = Math.Max(0, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX));
        double height = Math.Max(0, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));
        return width * height;
    }

    /// <summary>
    /// Overlap measured against the <b>smaller</b> of the two regions: 1.0 when the smaller lies entirely
    /// inside the larger, regardless of how much larger that one is.
    /// </summary>
    private static double OverlapRatioOfSmaller(OcrBoundingBox a, OcrBoundingBox b)
    {
        double reference = Math.Min(Area(a), Area(b));
        return reference <= 0 ? 0 : IntersectionArea(a, b) / reference;
    }

    /// <summary>Whether at least 90% of <paramref name="inner"/>'s own area falls inside <paramref name="outer"/>.</summary>
    private static bool IsContained(OcrBoundingBox inner, OcrBoundingBox outer)
    {
        double area = Area(inner);
        return area > 0 && IntersectionArea(inner, outer) / area >= ContainmentRatio;
    }
}
