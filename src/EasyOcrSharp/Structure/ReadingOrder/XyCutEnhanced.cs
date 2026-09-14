namespace EasyOcrSharp.Structure.ReadingOrder;

/// <summary>
/// One layout block handed to <see cref="XyCutEnhancedOrderer"/>.
/// </summary>
/// <param name="Index">Caller-defined identity of the block; the orderer returns these values reordered.</param>
/// <param name="X1">Left edge in page pixels.</param>
/// <param name="Y1">Top edge in page pixels.</param>
/// <param name="X2">Right edge in page pixels.</param>
/// <param name="Y2">Bottom edge in page pixels.</param>
/// <param name="Label">Raw paddle label string ("text", "paragraph_title", "doc_title", "header", "table", …).</param>
/// <param name="ModelOrder">Layout-model order-head value, or -1 when absent. Like python xycut_enhanced, the
/// orderer does NOT use it for the final ordering (kept for future diagnostics only).</param>
internal readonly record struct OrderableBlock(
    int Index, float X1, float Y1, float X2, float Y2, string Label, int ModelOrder);

/// <summary>
/// XY-Cut++ reading order — a C# port of PaddleX PP-StructureV3's <c>xycut_enhanced</c>
/// (<c>layout_parsing/xycut_enhanced/xycuts.py</c> + <c>utils.py</c>, settings from <c>setting.py</c>),
/// applied at a single level (the whole page as one region; the PP-DocBlockLayout region model is not hosted).
///
/// Pipeline, mirroring python <c>xycut_enhanced()</c>:
/// <list type="number">
/// <item>Blocks get order labels (header/doc_title/paragraph_title/vision/footer/unordered/normal_text) and
/// parents absorb their children — doc-title subtitle text, chained sub-paragraph-titles, vision titles
/// (figure/table/chart captions) and vision footnotes (<c>update_region_label</c>).</item>
/// <item>Pre-cut analysis: centered blocks whose projection interval stands alone seed cut coordinates, and
/// projection gaps ≥ 3 text-line heights cut the page into vertical stripes; gaps of 1.2–3 line heights cut
/// only when the column-gap structure differs on the two sides (<c>pre_process</c>).</item>
/// <item>Per stripe, cross-layout / cross-reference blocks spanning multiple columns are detected and pulled
/// out (<c>get_layout_structure</c>); the rest are ordered by the recursive XY (multi-column) or YX
/// (single-column) projection cut with min_gap=1 after <c>shrink_overlapping_boxes</c>.</item>
/// <item>Doc titles, cross-layout and cross-reference blocks are re-inserted by weighted distance
/// (edge=1e4, up=1, left=1e-4), reference or Manhattan insertion (<c>match_unsorted_blocks</c>).</item>
/// <item>Headers come first, footers then unordered blocks last, and absorbed children are spliced back in
/// next to their parents (<c>insert_child_blocks</c>).</item>
/// </list>
///
/// Deviations from python (documented in the individual methods too): text-line height/width and line counts
/// are estimated from block geometry because this port receives no OCR line boxes, the seg-flag lookahead in
/// weighted insertion is skipped for the same reason, and a region-labelled block's euclidean distance uses
/// its own corner rather than the min over member blocks. Output is deterministic and always a permutation of
/// the input <see cref="OrderableBlock.Index"/> values.
/// </summary>
internal static class XyCutEnhancedOrderer
{
    // XYCUT_SETTINGS["cross_layout_ref_text_block_words_num_threshold"] (setting.py:24).
    private const float CrossLayoutRefTextBlockWordsNumThreshold = 10f;

    /// <summary>Order labels excluded from the pre-cut/cut stages (python <c>pre_process</c> mask_labels).</summary>
    private static readonly HashSet<string> PreCutMaskLabels = new(StringComparer.Ordinal)
    {
        "header",
        "unordered",
        "footer",
        "vision_footnote",
        "sub_paragraph_title",
        "doc_title_text",
        "vision_title",
        "sub_region",
    };

    /// <summary>
    /// Returns the blocks' <see cref="OrderableBlock.Index"/> values reordered into reading order.
    /// Every input Index appears exactly once.
    /// </summary>
    public static int[] Order(IReadOnlyList<OrderableBlock> blocks, float pageWidth, float pageHeight)
    {
        if (blocks.Count == 0) return Array.Empty<int>();
        if (blocks.Count == 1) return new[] { blocks[0].Index };

        var region = BuildRegion(blocks, pageWidth, pageHeight);
        var ordered = OrderRegion(region);

        // Guarantee a permutation: dedupe defensively and re-append anything a degenerate box made the
        // projection cut drop (python can silently lose such blocks; we must not).
        var seen = new HashSet<int>();
        var result = new List<int>(blocks.Count);
        foreach (var block in ordered)
        {
            if (seen.Add(block.MapKey)) result.Add(block.SourceIndex);
        }

        foreach (var block in region.Blocks)
        {
            if (seen.Add(block.MapKey)) result.Add(block.SourceIndex);
        }

        return result.ToArray();
    }

    /// <summary>
    /// Builds the page-level region: classifies blocks into the label-family index lists and estimates the
    /// text-line metrics python takes from OCR line boxes (a documented simplification — the smallest
    /// secondary extents of normal text blocks approximate a single line's height).
    /// </summary>
    internal static XyCutRegion BuildRegion(IReadOnlyList<OrderableBlock> blocks, float pageWidth, float pageHeight)
    {
        var region = new XyCutRegion { Bbox = new[] { 0f, 0f, pageWidth, pageHeight } };

        int horizontalNormalTextCount = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            var src = blocks[i];
            var block = new XyCutBlock(i, src.Index, src.Label ?? string.Empty, src.X1, src.Y1, src.X2, src.Y2);
            region.Blocks.Add(block);

            // init_region_info_from_layout: family lists are keyed on the raw label.
            if (XyCutLabelSets.HeaderLabels.Contains(block.Label)) region.HeaderIdxes.Add(i);
            else if (XyCutLabelSets.DocTitleLabels.Contains(block.Label)) region.DocTitleIdxes.Add(i);
            else if (XyCutLabelSets.ParagraphTitleLabels.Contains(block.Label)) region.ParagraphTitleIdxes.Add(i);
            else if (XyCutLabelSets.VisionLabels.Contains(block.Label)) region.VisionIdxes.Add(i);
            else if (XyCutLabelSets.VisionTitleLabels.Contains(block.Label)) region.VisionTitleIdxes.Add(i);
            else if (XyCutLabelSets.FooterLabels.Contains(block.Label)) region.FooterIdxes.Add(i);
            else if (XyCutLabelSets.UnorderedLabels.Contains(block.Label)) region.UnorderedIdxes.Add(i);
            else
            {
                region.NormalTextIdxes.Add(i);
                if (block.Direction == XyDirection.Horizontal) horizontalNormalTextCount++;
            }
        }

        region.Direction = horizontalNormalTextCount >= region.NormalTextIdxes.Count * 0.5
            ? XyDirection.Horizontal
            : XyDirection.Vertical;

        // --- Text-line metric estimation (python reads these off the OCR result; we only have boxes). ---
        var lineHeightCandidates = new List<float>();
        var lineWidthCandidates = new List<float>();
        foreach (var idx in region.NormalTextIdxes)
        {
            var block = region.Blocks[idx];
            float secondaryExtent = block.Direction == XyDirection.Horizontal ? block.Height : block.Width;
            float directionExtent = block.Direction == XyDirection.Horizontal ? block.Width : block.Height;
            if (secondaryExtent > 0) lineHeightCandidates.Add(secondaryExtent);
            if (directionExtent > 0) lineWidthCandidates.Add(directionExtent);
        }

        if (lineHeightCandidates.Count > 0)
        {
            // The shortest text blocks are single lines; average everything within 1.5x of the shortest.
            lineHeightCandidates.Sort();
            float smallest = Math.Max(4f, lineHeightCandidates[0]);
            float sum = 0f;
            int count = 0;
            foreach (var extent in lineHeightCandidates)
            {
                if (extent <= smallest * 1.5f)
                {
                    sum += extent;
                    count++;
                }
            }

            region.TextLineHeight = count > 0 ? sum / count : smallest;
        }

        if (lineWidthCandidates.Count > 0)
        {
            float sum = 0f;
            foreach (var extent in lineWidthCandidates) sum += extent;
            region.TextLineWidth = sum / lineWidthCandidates.Count;
        }

        foreach (var block in region.Blocks)
        {
            block.TextLineHeight = region.TextLineHeight;
            block.TextLineWidth = block.Direction == XyDirection.Horizontal
                ? Math.Max(1f, block.Width)
                : Math.Max(1f, block.Height);
            float secondaryExtent = block.Direction == XyDirection.Horizontal ? block.Height : block.Width;
            block.NumOfLines = Math.Max(1, (int)MathF.Round(secondaryExtent / Math.Max(1f, region.TextLineHeight)));
        }

        return region;
    }

    /// <summary>Python <c>xycut_enhanced(region)</c>: the full ordering pass over one region.</summary>
    private static List<XyCutBlock> OrderRegion(XyCutRegion region)
    {
        var preCutList = PreProcess(region);
        var finalOrderResList = new List<XyCutBlock>(region.Blocks.Count);

        var headerBlocks = CollectBlocks(region, region.HeaderIdxes);
        var unorderedBlocks = CollectBlocks(region, region.UnorderedIdxes);
        var footerBlocks = CollectBlocks(region, region.FooterIdxes);

        XyCutGeometry.SortNormalBlocks(headerBlocks, region.TextLineHeight, region.TextLineWidth, region.Direction);
        XyCutGeometry.SortNormalBlocks(footerBlocks, region.TextLineHeight, region.TextLineWidth, region.Direction);
        XyCutGeometry.SortNormalBlocks(unorderedBlocks, region.TextLineHeight, region.TextLineWidth, region.Direction);
        finalOrderResList.AddRange(headerBlocks);

        var unsortedBlocks = new List<XyCutBlock>();
        var sortedBlocksByPreCuts = new List<XyCutBlock>();
        foreach (var preCutBlocks in preCutList)
        {
            var sortedBlocks = new List<XyCutBlock>();
            var docTitleBlocks = new List<XyCutBlock>();
            var xyCutBlocks = new List<XyCutBlock>();

            if (preCutBlocks.Count > 0 && preCutBlocks[0].Label == "region")
            {
                var regionBoxes = preCutBlocks.ConvertAll(b => b.Bbox);
                var discontinuousRegions = XyCutGeometry.CalculateDiscontinuousProjection(regionBoxes, region.Direction);
                if (discontinuousRegions.Count == 1)
                {
                    GetLayoutStructure(preCutBlocks, region);
                }
            }
            else
            {
                GetLayoutStructure(preCutBlocks, region);
            }

            foreach (var block in preCutBlocks)
            {
                if (block.OrderLabel is not ("cross_layout" or "cross_reference" or "doc_title" or "unordered"))
                {
                    xyCutBlocks.Add(block);
                }
                else if (block.Label == "doc_title")
                {
                    docTitleBlocks.Add(block);
                }
                else
                {
                    unsortedBlocks.Add(block);
                }
            }

            if (xyCutBlocks.Count > 0)
            {
                int maxTextLines = 1;
                foreach (var block in xyCutBlocks) maxTextLines = Math.Max(maxTextLines, block.NumOfLines);
                var discontinuous = XyCutGeometry.CalculateDiscontinuousProjection(
                    xyCutBlocks.ConvertAll(b => b.Bbox), region.Direction);

                // Work on copies: shrinking and the vertical right-to-left negation must not corrupt the
                // canonical blocks (python deepcopy).
                var blocksToSort = xyCutBlocks.ConvertAll(b => b.CloneForSort());
                if (region.Direction == XyDirection.Vertical)
                {
                    foreach (var block in blocksToSort)
                    {
                        block.Bbox = new[] { -block.Bbox[0], block.Bbox[1], -block.Bbox[2], block.Bbox[3] };
                    }
                }

                int[] sortedIndexes;
                if (discontinuous.Count == 1 || maxTextLines == 1)
                {
                    // Single column (or all single-line): bucket rows by half a line height, cut secondary-first.
                    float bucket = Math.Max(1f, MathF.Floor(region.TextLineHeight / 2f));
                    int secStart = region.SecondaryDirectionStartIndex;
                    int dirStart = region.DirectionStartIndex;
                    XyCutGeometry.StableSort(blocksToSort, (a, b) =>
                    {
                        int c = MathF.Floor(a.Bbox[secStart] / bucket).CompareTo(MathF.Floor(b.Bbox[secStart] / bucket));
                        return c != 0 ? c : a.Bbox[dirStart].CompareTo(b.Bbox[dirStart]);
                    });
                    XyCutGeometry.ShrinkOverlappingBoxes(blocksToSort, region.SecondaryDirection);
                    sortedIndexes = XyCutGeometry.SortByXyCut(
                        blocksToSort.ConvertAll(b => b.Bbox), region.SecondaryDirection, minGap: 1);
                }
                else
                {
                    // Multi-column: bucket columns by half a line width, cut along the region direction first.
                    float bucket = Math.Max(1f, MathF.Floor(region.TextLineWidth / 2f));
                    int secStart = region.SecondaryDirectionStartIndex;
                    int dirStart = region.DirectionStartIndex;
                    XyCutGeometry.StableSort(blocksToSort, (a, b) =>
                    {
                        int c = MathF.Floor(a.Bbox[dirStart] / bucket).CompareTo(MathF.Floor(b.Bbox[dirStart] / bucket));
                        return c != 0 ? c : a.Bbox[secStart].CompareTo(b.Bbox[secStart]);
                    });
                    XyCutGeometry.ShrinkOverlappingBoxes(blocksToSort, region.SecondaryDirection);
                    sortedIndexes = XyCutGeometry.SortByXyCut(
                        blocksToSort.ConvertAll(b => b.Bbox), region.Direction, minGap: 1);
                }

                var emitted = new HashSet<int>();
                foreach (var i in sortedIndexes)
                {
                    if (emitted.Add(i)) sortedBlocks.Add(region.Blocks[blocksToSort[i].MapKey]);
                }

                // Safety net: a zero-extent box invisible to the projection profile would otherwise vanish.
                for (int i = 0; i < blocksToSort.Count; i++)
                {
                    if (emitted.Add(i)) sortedBlocks.Add(region.Blocks[blocksToSort[i].MapKey]);
                }
            }

            sortedBlocks = MatchUnsortedBlocks(sortedBlocks, docTitleBlocks, region);

            if (unsortedBlocks.Count > 0 && unsortedBlocks[0].Label == "region")
            {
                sortedBlocks = MatchUnsortedBlocks(sortedBlocks, unsortedBlocks, region);
                unsortedBlocks = new List<XyCutBlock>();
            }

            sortedBlocksByPreCuts.AddRange(sortedBlocks);
        }

        var finalSortedBlocks = MatchUnsortedBlocks(sortedBlocksByPreCuts, unsortedBlocks, region);

        finalOrderResList.AddRange(finalSortedBlocks);
        finalOrderResList.AddRange(footerBlocks);
        finalOrderResList.AddRange(unorderedBlocks);

        // insert_child_blocks over the growing list: children are spliced in right after (or before,
        // per the local sort) their parent; children have no grandchildren left at this point.
        for (int blockIdx = 0; blockIdx < finalOrderResList.Count; blockIdx++)
        {
            XyCutInserts.InsertChildBlocks(finalOrderResList[blockIdx], blockIdx, finalOrderResList, region);
        }

        return finalOrderResList;
    }

    private static List<XyCutBlock> CollectBlocks(XyCutRegion region, List<int> idxes)
    {
        var list = new List<XyCutBlock>(idxes.Count);
        foreach (var idx in idxes) list.Add(region.Blocks[idx]);
        return list;
    }

    /// <summary>
    /// Python <c>pre_process(region)</c>: assigns order labels + children, finds centered blocks and
    /// projection gaps along the secondary direction, and cuts the page into ordered stripes of blocks.
    /// </summary>
    internal static List<List<XyCutBlock>> PreProcess(XyCutRegion region)
    {
        var blocks = region.Blocks;
        var preCutBlockKeys = new List<int>();
        foreach (var block in blocks)
        {
            if (block.OrderLabel is null || !PreCutMaskLabels.Contains(block.OrderLabel))
            {
                UpdateRegionLabel(block, region);
            }

            // Centered-block test: python uses integer floor division for the tolerance.
            float toleranceLen = block.Direction == XyDirection.Horizontal
                ? MathF.Floor(block.LongSideLength / 5f)
                : MathF.Floor(block.ShortSideLength / 10f);
            float blockCenter = (block.Bbox[region.DirectionStartIndex] + block.Bbox[region.DirectionEndIndex]) / 2f;
            if (Math.Abs(blockCenter - region.DirectionCenterCoordinate) <= toleranceLen)
            {
                preCutBlockKeys.Add(block.MapKey);
            }
        }

        var preCutList = new List<List<XyCutBlock>>();
        var cutDirection = region.SecondaryDirection;
        var cutCoordinates = new List<float>();
        var discontinuous = new List<(float Start, float End)>();

        var unmaskedBoxes = new List<float[]>();
        foreach (var block in blocks)
        {
            if (block.OrderLabel is null || !PreCutMaskLabels.Contains(block.OrderLabel))
            {
                unmaskedBoxes.Add(block.Bbox);
            }
        }

        if (unmaskedBoxes.Count == 0) return preCutList;

        if (preCutBlockKeys.Count > 0)
        {
            discontinuous = XyCutGeometry.CalculateDiscontinuousProjection(unmaskedBoxes, cutDirection, out var numList);
            foreach (var key in preCutBlockKeys)
            {
                var block = blocks[key];
                if ((block.OrderLabel is null || !PreCutMaskLabels.Contains(block.OrderLabel))
                    && block.SecondaryDirection == cutDirection)
                {
                    // A centered block whose secondary interval is a merged interval containing only itself
                    // marks a full-width divider (e.g. a centered title): cut above and below it.
                    float start = block.SecondaryDirectionStartCoordinate;
                    float end = block.SecondaryDirectionEndCoordinate;
                    int pos = discontinuous.FindIndex(t => t.Start == start && t.End == end);
                    if (pos >= 0 && numList[pos] == 1)
                    {
                        cutCoordinates.Add(start);
                        cutCoordinates.Add(end);
                    }
                }
            }
        }

        var secondaryCheckBoxes = new List<float[]>();
        foreach (var block in blocks)
        {
            if (block.OrderLabel is null
                || (!PreCutMaskLabels.Contains(block.OrderLabel) && block.OrderLabel != "vision"))
            {
                secondaryCheckBoxes.Add(block.Bbox);
            }
        }

        bool regionMode = blocks[0].Label == "region";
        if (secondaryCheckBoxes.Count > 0 || regionMode)
        {
            var secondaryDiscontinuous = XyCutGeometry.CalculateDiscontinuousProjection(secondaryCheckBoxes, region.Direction);
            if (secondaryDiscontinuous.Count == 1 || regionMode)
            {
                if (discontinuous.Count == 0)
                {
                    discontinuous = XyCutGeometry.CalculateDiscontinuousProjection(unmaskedBoxes, cutDirection);
                }

                var currentInterval = discontinuous[0];
                float preCutCoordinate = 0f;
                foreach (var coordinate in cutCoordinates)
                {
                    if (coordinate < currentInterval.End) preCutCoordinate = Math.Max(preCutCoordinate, coordinate);
                }

                preCutCoordinate = Math.Max(currentInterval.Start, preCutCoordinate);
                for (int k = 1; k < discontinuous.Count; k++)
                {
                    var interval = discontinuous[k];
                    float gapLen = interval.Start - currentInterval.End;
                    if (gapLen >= region.TextLineHeight * 3f || regionMode)
                    {
                        cutCoordinates.Add(currentInterval.End);
                    }
                    else if (gapLen > region.TextLineHeight * 1.2f)
                    {
                        // Ambiguous gap: cut only when the column-gap structure differs before vs after it.
                        var preBlocks = XyCutGeometry.GetBlocksByDirectionInterval(
                            new List<XyCutBlock>(blocks), preCutCoordinate, currentInterval.End, cutDirection);
                        var postBlocks = XyCutGeometry.GetBlocksByDirectionInterval(
                            new List<XyCutBlock>(blocks), currentInterval.End, interval.End, cutDirection);
                        int projectionIndex = cutDirection == XyDirection.Horizontal ? 1 : 0;
                        if (preBlocks.Count > 0 && postBlocks.Count > 0)
                        {
                            var preProjection = XyCutGeometry.ProjectionByBboxes(preBlocks.ConvertAll(b => b.Bbox), projectionIndex);
                            var postProjection = XyCutGeometry.ProjectionByBboxes(postBlocks.ConvertAll(b => b.Bbox), projectionIndex);
                            var preIntervals = XyCutGeometry.FindLocalMinimaFlatRegions(preProjection);
                            var postIntervals = XyCutGeometry.FindLocalMinimaFlatRegions(postProjection);

                            var gapBoxes = new List<float[]>();
                            int preCount = 0, postCount = 0;
                            if (preIntervals is not null)
                            {
                                foreach (var (start, end) in preIntervals)
                                {
                                    var bbox = new float[4];
                                    bbox[projectionIndex] = start;
                                    bbox[projectionIndex + 2] = end;
                                    gapBoxes.Add(bbox);
                                    preCount++;
                                }
                            }

                            if (postIntervals is not null)
                            {
                                foreach (var (start, end) in postIntervals)
                                {
                                    var bbox = new float[4];
                                    bbox[projectionIndex] = start;
                                    bbox[projectionIndex + 2] = end;
                                    gapBoxes.Add(bbox);
                                    postCount++;
                                }
                            }

                            int maxGapBoxesNum = Math.Max(preCount, postCount);
                            if (maxGapBoxesNum > 0)
                            {
                                var discontinuousIntervals = XyCutGeometry.CalculateDiscontinuousProjection(gapBoxes, region.Direction);
                                if (discontinuousIntervals.Count != maxGapBoxesNum)
                                {
                                    preCutCoordinate = currentInterval.End;
                                    cutCoordinates.Add(currentInterval.End);
                                }
                            }
                        }
                    }

                    currentInterval = interval;
                }
            }
        }

        var cutList = XyCutGeometry.GetCutBlocks(
            new List<XyCutBlock>(blocks), cutDirection, cutCoordinates, PreCutMaskLabels);
        preCutList.AddRange(cutList);
        if (region.Direction == XyDirection.Vertical) preCutList.Reverse();
        return preCutList;
    }

    /// <summary>
    /// Python <c>update_region_label</c>: assigns the block's order label from its paddle label and lets
    /// doc titles, paragraph titles, visions and regions absorb their child blocks.
    /// </summary>
    internal static void UpdateRegionLabel(XyCutBlock block, XyCutRegion region)
    {
        if (XyCutLabelSets.HeaderLabels.Contains(block.Label)) block.OrderLabel = "header";
        else if (XyCutLabelSets.DocTitleLabels.Contains(block.Label)) block.OrderLabel = "doc_title";
        else if (XyCutLabelSets.ParagraphTitleLabels.Contains(block.Label) && block.OrderLabel is null) block.OrderLabel = "paragraph_title";
        else if (XyCutLabelSets.VisionLabels.Contains(block.Label))
        {
            block.OrderLabel = "vision";
            block.NumOfLines = 1;
            block.Direction = region.Direction;
        }
        else if (XyCutLabelSets.FooterLabels.Contains(block.Label)) block.OrderLabel = "footer";
        else if (XyCutLabelSets.UnorderedLabels.Contains(block.Label)) block.OrderLabel = "unordered";
        else if (block.Label == "region") block.OrderLabel = "region";
        else block.OrderLabel = "normal_text";

        switch (block.OrderLabel)
        {
            case "doc_title":
                XyCutChildMatcher.UpdateDocTitleChildBlocks(block, region);
                break;
            case "paragraph_title":
                XyCutChildMatcher.UpdateParagraphTitleChildBlocks(block, region);
                break;
            case "vision":
                XyCutChildMatcher.UpdateVisionChildBlocks(block, region);
                break;
            case "region":
                XyCutChildMatcher.UpdateRegionChildBlocks(block, region);
                break;
        }
    }

    /// <summary>
    /// Python <c>get_layout_structure</c>: marks blocks that span several columns as cross_layout (or
    /// cross_reference for reference blocks) so they are pulled out of the projection cut and re-inserted
    /// by weighted distance afterwards. Sorts <paramref name="blocks"/> by (x1, width) in place like python.
    /// </summary>
    internal static void GetLayoutStructure(List<XyCutBlock> blocks, XyCutRegion region)
    {
        XyCutGeometry.StableSort(blocks, (a, b) =>
        {
            int c = a.Bbox[0].CompareTo(b.Bbox[0]);
            return c != 0 ? c : a.Width.CompareTo(b.Width);
        });

        static bool IsMasked(XyCutBlock block) =>
            block.OrderLabel is "doc_title" or "cross_layout" or "cross_reference";

        for (int blockIdx = 0; blockIdx < blocks.Count; blockIdx++)
        {
            var block = blocks[blockIdx];
            if (IsMasked(block)) continue;

            for (int refIdx = 0; refIdx < blocks.Count; refIdx++)
            {
                var refBlock = blocks[refIdx];
                if (blockIdx == refIdx || IsMasked(refBlock)) continue;

                float bboxIou = XyCutGeometry.CalculateOverlapRatio(block.Bbox, refBlock.Bbox);
                if (bboxIou > 0)
                {
                    if (refBlock.OrderLabel == "vision")
                    {
                        refBlock.OrderLabel = "cross_layout";
                        break;
                    }

                    if (bboxIou > 0.1f && block.Area < refBlock.Area)
                    {
                        block.OrderLabel = "cross_layout";
                        break;
                    }
                }

                float matchProjectionIou = XyCutGeometry.CalculateProjectionOverlapRatio(
                    block.Bbox, refBlock.Bbox, region.Direction);
                if (matchProjectionIou <= 0) continue;

                for (int secondRefIdx = 0; secondRefIdx < blocks.Count; secondRefIdx++)
                {
                    var secondRefBlock = blocks[secondRefIdx];
                    if (secondRefIdx == blockIdx || secondRefIdx == refIdx || IsMasked(secondRefBlock)) continue;

                    float secondBboxIou = XyCutGeometry.CalculateOverlapRatio(block.Bbox, secondRefBlock.Bbox);
                    if (secondBboxIou > 0.1f)
                    {
                        if (secondRefBlock.OrderLabel == "vision")
                        {
                            secondRefBlock.OrderLabel = "cross_layout";
                            break;
                        }

                        if (block.OrderLabel == "vision" || block.Area < secondRefBlock.Area)
                        {
                            block.OrderLabel = "cross_layout";
                            break;
                        }
                    }

                    float secondMatchProjectionIou = XyCutGeometry.CalculateProjectionOverlapRatio(
                        block.Bbox, secondRefBlock.Bbox, region.Direction);
                    float refMatchProjectionIou = XyCutGeometry.CalculateProjectionOverlapRatio(
                        refBlock.Bbox, secondRefBlock.Bbox, region.Direction);
                    float secondaryDirectionRefMatchProjectionIou = XyCutGeometry.CalculateProjectionOverlapRatio(
                        refBlock.Bbox, secondRefBlock.Bbox, region.SecondaryDirection);

                    // block spans two stacked neighbours that do not overlap each other along the region
                    // direction → it crosses the column boundary between them.
                    if (secondMatchProjectionIou > 0
                        && refMatchProjectionIou == 0
                        && secondaryDirectionRefMatchProjectionIou > 0)
                    {
                        bool longTextNeighbours = refBlock.OrderLabel == "normal_text"
                            && secondRefBlock.OrderLabel == "normal_text"
                            && refBlock.LongSideLength > refBlock.TextLineHeight * CrossLayoutRefTextBlockWordsNumThreshold
                            && secondRefBlock.LongSideLength > secondRefBlock.TextLineHeight * CrossLayoutRefTextBlockWordsNumThreshold;
                        if (block.OrderLabel is "vision" or "region" || longTextNeighbours)
                        {
                            block.OrderLabel = block.Label == "reference" ? "cross_reference" : "cross_layout";
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Python <c>match_unsorted_blocks</c>: re-inserts pulled-out blocks (doc titles, cross-layout,
    /// cross-reference, unordered leftovers, sub-regions) into the sorted flow using the insertion
    /// strategy their order label demands.
    /// </summary>
    internal static List<XyCutBlock> MatchUnsortedBlocks(
        List<XyCutBlock> sortedBlocks, List<XyCutBlock> unsortedBlocks, XyCutRegion region)
    {
        var queue = new List<XyCutBlock>(unsortedBlocks);
        XyCutGeometry.SortNormalBlocks(queue, region.TextLineHeight, region.TextLineWidth, region.Direction);
        for (int idx = 0; idx < queue.Count; idx++)
        {
            var block = queue[idx];
            var orderLabel = block.Label != "region" ? block.OrderLabel : "region";
            if (idx == 0 && orderLabel == "doc_title")
            {
                sortedBlocks.Insert(0, block);
                continue;
            }

            sortedBlocks = orderLabel switch
            {
                "cross_layout" or "paragraph_title" or "doc_title" or "vision_title" or "vision"
                    => XyCutInserts.WeightedDistanceInsert(block, sortedBlocks, region),
                "cross_reference" => XyCutInserts.ReferenceInsert(block, sortedBlocks),
                "region" => XyCutInserts.EuclideanInsert(block, sortedBlocks, region),
                _ => XyCutInserts.ManhattanInsert(block, sortedBlocks),
            };
        }

        return sortedBlocks;
    }
}
