using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.Layout;

/// <summary>
/// The clean-up chain that runs over the layout detections <i>after</i> they have been score-thresholded.
/// The detectors emit a fixed top-k of candidate boxes with no NMS, so the same area of the page is routinely
/// proposed several times under different labels; these passes turn that raw candidate set into the region
/// list callers actually want. The effect is slight at the default 0.5 score threshold — few duplicates score
/// that high — and grows as <see cref="StructureOptions.LayoutScoreThreshold"/> is lowered.
/// <para>
/// Applied in order: (1) optional NMS, (2) the always-on oversized-image drop, (3) optional containment
/// merge, (4) sort by the model's predicted reading order, (5) optional unclip, (6) the overlapping-region
/// filter (on by default). Each step is gated by the matching <see cref="StructureOptions"/> knob; the
/// defaults are no NMS, no unclip, no merge, and overlap filtering on.
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

    /// <summary>Overlap (relative to the smaller region) above which two regions are considered duplicates.</summary>
    private const double DuplicateOverlapRatio = 0.7;

    /// <summary>Overlap (relative to the smaller region) above which an <c>inline_formula</c> region is absorbed.</summary>
    private const double InlineFormulaOverlapRatio = 0.5;

    /// <summary>Regions narrower or shorter than this (px) are dropped by the overlap filter.</summary>
    private const double MinRegionSide = 6;

    /// <summary>Labels that survive an overlap with a differently-labelled region instead of being merged away.</summary>
    private static readonly HashSet<string> StandaloneLabels = new(StringComparer.Ordinal)
        { "image", "table", "seal", "chart" };

    /// <summary>
    /// Runs the enabled clean-up passes over <paramref name="regions"/> (already score-thresholded by the
    /// detector) and returns the surviving regions, sorted by the model's predicted reading order when it
    /// supplies one. The input list is never mutated.
    /// </summary>
    /// <param name="regions">The thresholded detections, in detector order.</param>
    /// <param name="options">Supplies the NMS / unclip / merge / overlap-filtering knobs.</param>
    /// <param name="pageWidth">Width of the page the detector ran on, in pixels.</param>
    /// <param name="pageHeight">Height of the page the detector ran on, in pixels.</param>
    public static IReadOnlyList<LayoutRegion> Apply(
        IReadOnlyList<LayoutRegion> regions, StructureOptions options, int pageWidth, int pageHeight)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(options);
        if (regions.Count == 0) return regions;

        var kept = new List<LayoutRegion>(regions);

        if (options.LayoutNms)
        {
            kept = SuppressByScore(kept);
        }

        kept = DropOversizedImages(kept, pageWidth, pageHeight);

        if (options.LayoutMergeMode is LayoutMergeMode.Large or LayoutMergeMode.Small)
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
    /// Resolves nested regions: <see cref="LayoutMergeMode.Large"/> drops every region contained by another
    /// (keeping the enclosing block), <see cref="LayoutMergeMode.Small"/> keeps the regions that contain
    /// nothing, plus those that are themselves contained (keeping the inner blocks). "Contained" means at
    /// least 90% of the inner region's own area falls inside the outer one. When the model's vocabulary has a
    /// <c>formula</c> class, a formula region is never treated as contained by a non-formula region, so
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
    /// Removes the duplicates the NMS-free top-k leaves behind. In order: <c>reference</c> regions are dropped
    /// outright (their text is carried by the neighbouring <c>reference_content</c> block); slivers under
    /// 6&#160;px in either dimension go; then every pair overlapping by more than 70% of the smaller region
    /// collapses to the larger one. Two exceptions: an <c>inline_formula</c> overlapping anything by more than
    /// 50% is absorbed by it, and a differently-labelled pair whose labels are all drawn from
    /// <c>image / table / seal / chart</c> — or which does not involve a <c>table</c> at all — is left alone,
    /// since a figure legitimately sits inside a chart, a seal on a table, and so on.
    /// </summary>
    private static List<LayoutRegion> DropOverlapping(List<LayoutRegion> regions)
    {
        // A `reference` marker is redundant only when a `reference_content` region actually overlaps it and
        // carries the same text. Dropping every `reference` unconditionally deleted whole bibliographies —
        // always on the 23-class PicoDet vocabulary, which has no `reference_content` class at all.
        var referenceContent = regions.Where(r => LabelKey(r) == "reference_content").ToList();
        var candidates = regions
            .Where(r => LabelKey(r) != "reference" ||
                        !referenceContent.Any(c => OverlapRatioOfSmaller(r.Bounds, c.Bounds) > DuplicateOverlapRatio))
            .ToList();
        var dropped = new bool[candidates.Count];

        for (int i = 0; i < candidates.Count; i++)
        {
            var a = candidates[i].Bounds;
            if (a.Width < MinRegionSide || a.Height < MinRegionSide)
            {
                dropped[i] = true;
            }

            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (dropped[i] || dropped[j]) continue;

                var b = candidates[j].Bounds;
                double overlap = OverlapRatioOfSmaller(a, b);

                string labelA = LabelKey(candidates[i]);
                string labelB = LabelKey(candidates[j]);

                if (labelA == "inline_formula" || labelB == "inline_formula")
                {
                    if (overlap > InlineFormulaOverlapRatio)
                    {
                        if (labelA == "inline_formula") dropped[i] = true;
                        if (labelB == "inline_formula") dropped[j] = true;
                        continue;
                    }
                }

                if (overlap <= DuplicateOverlapRatio) continue;

                if (labelA != labelB &&
                    (StandaloneLabels.Contains(labelA) || StandaloneLabels.Contains(labelB)))
                {
                    bool involvesTable = labelA == "table" || labelB == "table";
                    bool bothStandalone = StandaloneLabels.Contains(labelA) && StandaloneLabels.Contains(labelB);
                    if (!involvesTable || bothStandalone) continue;
                }

                if (Area(a) >= Area(b)) dropped[j] = true;
                else dropped[i] = true;
            }
        }

        var kept = new List<LayoutRegion>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            if (!dropped[i]) kept.Add(candidates[i]);
        }
        return kept;
    }

    // =================================================================================================
    // geometry + label helpers
    // =================================================================================================

    /// <summary>
    /// The label this region is filtered by: the model's own <see cref="LayoutRegion.RawLabel"/> when the
    /// label sidecar supplied one — so <c>reference</c> vs <c>reference_content</c> and <c>inline_formula</c>
    /// vs <c>display_formula</c> stay distinguishable — falling back to a canonical name for the mapped
    /// <see cref="StructureBlockType"/> when it did not.
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
        StructureBlockType.Figure => "image",
        StructureBlockType.Table => "table",
        StructureBlockType.Seal => "seal",
        StructureBlockType.Chart => "chart",
        StructureBlockType.Reference => "reference",
        StructureBlockType.Formula => "formula",
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
