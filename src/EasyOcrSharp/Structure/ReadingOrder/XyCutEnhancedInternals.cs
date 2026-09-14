namespace EasyOcrSharp.Structure.ReadingOrder;

/// <summary>Reading direction of a block or region (PaddleX "horizontal" / "vertical").</summary>
internal enum XyDirection
{
    Horizontal,
    Vertical,
}

/// <summary>Reference length used when normalizing an overlap (PaddleX mode="union" / "small" / "large").</summary>
internal enum XyOverlapMode
{
    Union,
    Small,
    Large,
}

/// <summary>
/// Mutable working copy of one layout block inside the XY-Cut++ orderer — the C# counterpart of PaddleX
/// <c>LayoutBlock</c> (layout_objects.py). Bounding boxes are mutable because the algorithm shrinks
/// overlapping boxes, negates X coordinates for vertical regions, and unions a parent with its children.
/// </summary>
internal sealed class XyCutBlock
{
    public XyCutBlock(int mapKey, int sourceIndex, string label, float x1, float y1, float x2, float y2)
    {
        MapKey = mapKey;
        SourceIndex = sourceIndex;
        Label = label;
        Bbox = new[] { x1, y1, x2, y2 };
        // get_bbox_direction(direction_ratio=1.0): horizontal when width >= height.
        Direction = x2 - x1 >= y2 - y1 ? XyDirection.Horizontal : XyDirection.Vertical;
    }

    /// <summary>Position of the block in the region block map (python <c>block.index</c>).</summary>
    public int MapKey { get; }

    /// <summary>The caller-supplied <see cref="OrderableBlock.Index"/> reported back from <c>Order</c>.</summary>
    public int SourceIndex { get; }

    /// <summary>Paddle label string; mutable because vision-footnote matching relabels blocks like python does.</summary>
    public string Label { get; set; }

    /// <summary>[x1, y1, x2, y2]; mutable working geometry.</summary>
    public float[] Bbox { get; set; }

    /// <summary>Ordering role (python <c>order_label</c>): header/footer/unordered/doc_title/paragraph_title/vision/
    /// vision_title/vision_footnote/doc_title_text/sub_paragraph_title/cross_layout/cross_reference/region/sub_region/normal_text.</summary>
    public string? OrderLabel { get; set; }

    public XyDirection Direction { get; set; }

    /// <summary>Estimated number of text lines (python fills this from OCR; here estimated from geometry).</summary>
    public int NumOfLines { get; set; } = 1;

    /// <summary>Estimated text-line height (python fills this from OCR line boxes).</summary>
    public float TextLineHeight { get; set; } = 1f;

    /// <summary>Estimated text-line width (python fills this from OCR line boxes).</summary>
    public float TextLineWidth { get; set; } = 1f;

    public List<XyCutBlock> ChildBlocks { get; private set; } = new();

    private float[]? _oriBbox;

    public float Width => Bbox[2] - Bbox[0];
    public float Height => Bbox[3] - Bbox[1];
    public float Area => Width * Height;
    public XyDirection SecondaryDirection => Direction == XyDirection.Horizontal ? XyDirection.Vertical : XyDirection.Horizontal;
    public float ShortSideLength => Direction == XyDirection.Horizontal ? Height : Width;
    public float LongSideLength => Direction == XyDirection.Horizontal ? Width : Height;
    public float StartCoordinate => Direction == XyDirection.Horizontal ? Bbox[0] : Bbox[1];
    public float EndCoordinate => Direction == XyDirection.Horizontal ? Bbox[2] : Bbox[3];
    public float SecondaryDirectionStartCoordinate => Direction == XyDirection.Horizontal ? Bbox[1] : Bbox[0];
    public float SecondaryDirectionEndCoordinate => Direction == XyDirection.Horizontal ? Bbox[3] : Bbox[2];

    public (float X, float Y) GetCentroid() => ((Bbox[0] + Bbox[2]) / 2f, (Bbox[1] + Bbox[3]) / 2f);

    /// <summary>
    /// Absorbs <paramref name="child"/> (python <c>append_child_block</c>): the parent bbox becomes the union
    /// (its pre-union bbox is remembered so <see cref="TakeChildBlocks"/> can restore it) and grandchildren
    /// are hoisted flat into this block's child list.
    /// </summary>
    public void AppendChildBlock(XyCutBlock child)
    {
        if (ChildBlocks.Count == 0) _oriBbox = (float[])Bbox.Clone();
        Bbox = new[]
        {
            Math.Min(Bbox[0], child.Bbox[0]),
            Math.Min(Bbox[1], child.Bbox[1]),
            Math.Max(Bbox[2], child.Bbox[2]),
            Math.Max(Bbox[3], child.Bbox[3]),
        };
        ChildBlocks.Add(child);
        if (child.ChildBlocks.Count > 0) ChildBlocks.AddRange(child.TakeChildBlocks());
    }

    /// <summary>Detaches and returns the children, restoring this block's pre-union bbox (python <c>get_child_blocks</c>).</summary>
    public List<XyCutBlock> TakeChildBlocks()
    {
        if (_oriBbox is not null) Bbox = _oriBbox;
        var children = ChildBlocks;
        ChildBlocks = new List<XyCutBlock>();
        return children;
    }

    /// <summary>Copy used by the cut stage so shrinking/negating boxes never corrupts the canonical block
    /// (python <c>deepcopy(xy_cut_blocks)</c>); the result maps back through <see cref="MapKey"/>.</summary>
    public XyCutBlock CloneForSort() => new(MapKey, SourceIndex, Label, Bbox[0], Bbox[1], Bbox[2], Bbox[3])
    {
        OrderLabel = OrderLabel,
        Direction = Direction,
        NumOfLines = NumOfLines,
        TextLineHeight = TextLineHeight,
        TextLineWidth = TextLineWidth,
    };
}

/// <summary>
/// The page treated as a single ordering region — the C# counterpart of PaddleX <c>LayoutRegion</c>
/// restricted to the one-level use (no PP-DocBlockLayout region model in this port).
/// </summary>
internal sealed class XyCutRegion
{
    public required float[] Bbox { get; init; }
    public XyDirection Direction { get; set; } = XyDirection.Horizontal;
    public XyDirection SecondaryDirection => Direction == XyDirection.Horizontal ? XyDirection.Vertical : XyDirection.Horizontal;
    public float TextLineHeight { get; set; } = 10f;
    public float TextLineWidth { get; set; } = 20f;

    /// <summary>Blocks in input order; a block's <see cref="XyCutBlock.MapKey"/> is its index here.</summary>
    public List<XyCutBlock> Blocks { get; } = new();

    public List<int> HeaderIdxes { get; } = new();
    public List<int> DocTitleIdxes { get; } = new();
    public List<int> ParagraphTitleIdxes { get; } = new();
    public List<int> VisionIdxes { get; } = new();
    public List<int> VisionTitleIdxes { get; } = new();
    public List<int> FooterIdxes { get; } = new();
    public List<int> UnorderedIdxes { get; } = new();
    public List<int> NormalTextIdxes { get; } = new();

    public int DirectionStartIndex => Direction == XyDirection.Horizontal ? 0 : 1;
    public int DirectionEndIndex => Direction == XyDirection.Horizontal ? 2 : 3;
    public int SecondaryDirectionStartIndex => Direction == XyDirection.Horizontal ? 1 : 0;
    public int SecondaryDirectionEndIndex => Direction == XyDirection.Horizontal ? 3 : 2;
    public float DirectionCenterCoordinate => (Bbox[DirectionStartIndex] + Bbox[DirectionEndIndex]) / 2f;
}

/// <summary>Label families from PaddleX <c>setting.py BLOCK_LABEL_MAP</c>, verbatim.</summary>
internal static class XyCutLabelSets
{
    public static readonly HashSet<string> HeaderLabels = new(StringComparer.Ordinal) { "header", "header_image" };
    public static readonly HashSet<string> DocTitleLabels = new(StringComparer.Ordinal) { "doc_title" };
    public static readonly HashSet<string> ParagraphTitleLabels = new(StringComparer.Ordinal)
        { "paragraph_title", "abstract_title", "reference_title", "content_title" };
    public static readonly HashSet<string> VisionLabels = new(StringComparer.Ordinal)
        { "image", "table", "chart", "flowchart", "figure" };
    public static readonly HashSet<string> VisionTitleLabels = new(StringComparer.Ordinal)
        { "table_title", "chart_title", "figure_title", "figure_table_chart_title" };
    public static readonly HashSet<string> UnorderedLabels = new(StringComparer.Ordinal)
        { "aside_text", "seal", "number", "formula_number" };
    public static readonly HashSet<string> TextLabels = new(StringComparer.Ordinal) { "text" };
    public static readonly HashSet<string> FooterLabels = new(StringComparer.Ordinal) { "footer", "footer_image", "footnote" };
}

/// <summary>
/// Geometry primitives of XY-Cut++ — projection profiles, profile splitting, the recursive XY/YX cuts,
/// interval merging, overlap ratios and box shrinking — ported from PaddleX
/// <c>layout_parsing/xycut_enhanced/utils.py</c> and <c>layout_parsing/utils.py</c>.
/// Kept <c>internal</c> (not private) so unit tests can target the primitives directly.
/// </summary>
internal static class XyCutGeometry
{
    /// <summary>Stable in-place sort (python's <c>list.sort</c> is stable; <see cref="List{T}.Sort(Comparison{T})"/> is not).</summary>
    public static void StableSort<T>(List<T> list, Comparison<T> comparison)
    {
        var decorated = new (T Item, int Pos)[list.Count];
        for (int i = 0; i < list.Count; i++) decorated[i] = (list[i], i);
        Array.Sort(decorated, (a, b) =>
        {
            int c = comparison(a.Item, b.Item);
            return c != 0 ? c : a.Pos.CompareTo(b.Pos);
        });
        for (int i = 0; i < decorated.Length; i++) list[i] = decorated[i].Item;
    }

    /// <summary>Projection-interval IoU along one axis (python <c>calculate_projection_overlap_ratio</c>).</summary>
    public static float CalculateProjectionOverlapRatio(
        float[] bbox1, float[] bbox2, XyDirection direction, XyOverlapMode mode = XyOverlapMode.Union)
    {
        int startIndex = direction == XyDirection.Horizontal ? 0 : 1;
        int endIndex = direction == XyDirection.Horizontal ? 2 : 3;

        float intersectionStart = Math.Max(bbox1[startIndex], bbox2[startIndex]);
        float intersectionEnd = Math.Min(bbox1[endIndex], bbox2[endIndex]);
        float overlap = intersectionEnd - intersectionStart;
        if (overlap <= 0) return 0f;

        float refWidth = mode switch
        {
            XyOverlapMode.Small => Math.Min(bbox1[endIndex] - bbox1[startIndex], bbox2[endIndex] - bbox2[startIndex]),
            XyOverlapMode.Large => Math.Max(bbox1[endIndex] - bbox1[startIndex], bbox2[endIndex] - bbox2[startIndex]),
            _ => Math.Max(bbox1[endIndex], bbox2[endIndex]) - Math.Min(bbox1[startIndex], bbox2[startIndex]),
        };
        return refWidth > 0 ? overlap / refWidth : 0f;
    }

    /// <summary>Area overlap ratio (python <c>calculate_overlap_ratio</c>).</summary>
    public static float CalculateOverlapRatio(float[] bbox1, float[] bbox2, XyOverlapMode mode = XyOverlapMode.Union)
    {
        float interWidth = Math.Max(0f, Math.Min(bbox1[2], bbox2[2]) - Math.Max(bbox1[0], bbox2[0]));
        float interHeight = Math.Max(0f, Math.Min(bbox1[3], bbox2[3]) - Math.Max(bbox1[1], bbox2[1]));
        float interArea = interWidth * interHeight;

        float area1 = Math.Max(0f, bbox1[2] - bbox1[0]) * Math.Max(0f, bbox1[3] - bbox1[1]);
        float area2 = Math.Max(0f, bbox2[2] - bbox2[0]) * Math.Max(0f, bbox2[3] - bbox2[1]);
        float refArea = mode switch
        {
            XyOverlapMode.Small => Math.Min(area1, area2),
            XyOverlapMode.Large => Math.Max(area1, area2),
            _ => area1 + area2 - interArea,
        };
        return refArea > 0 ? interArea / refArea : 0f;
    }

    /// <summary>
    /// Nearest edge distance between two boxes with directional weights [left, right, up, down]
    /// (python <c>get_nearest_edge_distance</c>). Returns 0 when the boxes overlap on both axes.
    /// </summary>
    public static float GetNearestEdgeDistance(float[] bbox1, float[] bbox2, float[]? weight = null)
    {
        weight ??= new[] { 1f, 1f, 1f, 1f };
        float horizontalIou = CalculateProjectionOverlapRatio(bbox1, bbox2, XyDirection.Horizontal);
        float verticalIou = CalculateProjectionOverlapRatio(bbox1, bbox2, XyDirection.Vertical);
        if (horizontalIou > 0 && verticalIou > 0) return 0f;

        float minXDistance = 0f, minYDistance = 0f;
        if (horizontalIou == 0)
        {
            minXDistance = Math.Min(Math.Abs(bbox1[0] - bbox2[2]), Math.Abs(bbox1[2] - bbox2[0]))
                * (bbox1[2] < bbox2[0] ? weight[0] : weight[1]);
        }

        if (verticalIou == 0)
        {
            minYDistance = Math.Min(Math.Abs(bbox1[1] - bbox2[3]), Math.Abs(bbox1[3] - bbox2[1]))
                * (bbox1[3] < bbox2[1] ? weight[2] : weight[3]);
        }

        return minXDistance + minYDistance;
    }

    /// <summary>
    /// 1-D coverage histogram of the boxes along one axis (python <c>projection_by_bboxes</c>): cell i counts
    /// how many boxes cover pixel i. Coordinates are truncated to int; negative coordinates (the vertical-region
    /// right-to-left trick negates X) are folded through <c>abs</c> exactly like the python code.
    /// </summary>
    public static int[] ProjectionByBboxes(IReadOnlyList<float[]> boxes, int axis)
    {
        if (boxes.Count == 0) return Array.Empty<int>();
        int min = int.MaxValue, max = int.MinValue;
        foreach (var b in boxes)
        {
            int start = (int)b[axis], end = (int)b[axis + 2];
            min = Math.Min(min, Math.Min(start, end));
            max = Math.Max(max, Math.Max(start, end));
        }

        int maxLength = min < 0 ? -min : max;
        if (maxLength <= 0) return Array.Empty<int>();

        var projection = new int[maxLength];
        foreach (var b in boxes)
        {
            int start = Math.Abs((int)b[axis]);
            int end = Math.Abs((int)b[axis + 2]);
            if (end < start) (start, end) = (end, start);
            end = Math.Min(end, maxLength);
            for (int i = start; i < end; i++) projection[i]++;
        }

        return projection;
    }

    /// <summary>
    /// Splits a projection profile into covered segments separated by gaps wider than <paramref name="minGap"/>
    /// (python <c>split_projection_profile</c>). Returns null when nothing exceeds <paramref name="minValue"/>.
    /// Mid-profile segment ends are the last covered index (python quirk); the final end is exclusive.
    /// </summary>
    public static (int[] Starts, int[] Ends)? SplitProjectionProfile(int[] values, int minValue, int minGap)
    {
        var significant = new List<int>();
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] > minValue) significant.Add(i);
        }

        if (significant.Count == 0) return null;

        var starts = new List<int> { significant[0] };
        var ends = new List<int>();
        for (int k = 1; k < significant.Count; k++)
        {
            if (significant[k] - significant[k - 1] > minGap)
            {
                ends.Add(significant[k - 1]);
                starts.Add(significant[k]);
            }
        }

        ends.Add(significant[^1] + 1);
        return (starts.ToArray(), ends.ToArray());
    }

    /// <summary>
    /// Recursive XY cut wrapper (python <c>sort_by_xycut</c>): vertical starts with a Y projection
    /// (<see cref="RecursiveYxCut"/>), horizontal with an X projection (<see cref="RecursiveXyCut"/>).
    /// Returns positions into <paramref name="bboxes"/> in reading order; degenerate boxes the profile
    /// cannot see may be absent (callers re-append them).
    /// </summary>
    public static int[] SortByXyCut(IReadOnlyList<float[]> bboxes, XyDirection direction, int minGap = 1)
    {
        // astype(int): truncate once so interval selection compares the same whole numbers python does.
        var boxes = new List<float[]>(bboxes.Count);
        foreach (var b in bboxes) boxes.Add(new float[] { (int)b[0], (int)b[1], (int)b[2], (int)b[3] });
        var indices = new List<int>(boxes.Count);
        for (int i = 0; i < boxes.Count; i++) indices.Add(i);

        var res = new List<int>();
        if (direction == XyDirection.Vertical) RecursiveYxCut(boxes, indices, res, minGap);
        else RecursiveXyCut(boxes, indices, res, minGap);
        return res.ToArray();
    }

    /// <summary>Y-projection first, then X per band, recursing (python <c>recursive_yx_cut</c>).</summary>
    public static void RecursiveYxCut(List<float[]> boxes, List<int> indices, List<int> res, int minGap = 1)
    {
        if (boxes.Count == 0) return;

        var (sortedBoxes, sortedIndices) = SortPairsBy(boxes, indices, axis: 1);
        var yProjection = ProjectionByBboxes(sortedBoxes, 1);
        var yIntervals = SplitProjectionProfile(yProjection, 0, 1);
        if (yIntervals is null) return;

        var (yStarts, yEnds) = yIntervals.Value;
        for (int seg = 0; seg < yStarts.Length; seg++)
        {
            var (chunkBoxes, chunkIndices) = SelectInterval(sortedBoxes, sortedIndices, axis: 1, yStarts[seg], yEnds[seg], useAbs: false);
            if (chunkBoxes.Count == 0) continue;

            var (xSortedBoxes, xSortedIndices) = SortPairsBy(chunkBoxes, chunkIndices, axis: 0);
            var xProjection = ProjectionByBboxes(xSortedBoxes, 0);
            var xIntervals = SplitProjectionProfile(xProjection, 0, minGap);
            if (xIntervals is null) continue;

            var (xStarts, xEnds) = xIntervals.Value;
            if (xStarts.Length == 1)
            {
                res.AddRange(xSortedIndices);
                continue;
            }

            // Negated X coordinates (vertical regions) read right-to-left: walk the segments backwards.
            bool flip = MinStart(xSortedBoxes, 0) < 0;
            for (int k = 0; k < xStarts.Length; k++)
            {
                int seg2 = flip ? xStarts.Length - 1 - k : k;
                var (selBoxes, selIndices) = SelectInterval(xSortedBoxes, xSortedIndices, axis: 0, xStarts[seg2], xEnds[seg2], useAbs: true);
                RecursiveYxCut(selBoxes, selIndices, res);
            }
        }
    }

    /// <summary>X-projection first, then Y per column, recursing (python <c>recursive_xy_cut</c>).</summary>
    public static void RecursiveXyCut(List<float[]> boxes, List<int> indices, List<int> res, int minGap = 1)
    {
        if (boxes.Count == 0) return;

        var (sortedBoxes, sortedIndices) = SortPairsBy(boxes, indices, axis: 0);
        var xProjection = ProjectionByBboxes(sortedBoxes, 0);
        var xIntervals = SplitProjectionProfile(xProjection, 0, 1);
        if (xIntervals is null) return;

        var (xStarts, xEnds) = xIntervals.Value;
        bool flip = MinStart(sortedBoxes, 0) < 0;
        for (int k = 0; k < xStarts.Length; k++)
        {
            int seg = flip ? xStarts.Length - 1 - k : k;
            var (chunkBoxes, chunkIndices) = SelectInterval(sortedBoxes, sortedIndices, axis: 0, xStarts[seg], xEnds[seg], useAbs: true);
            if (chunkBoxes.Count == 0) continue;

            var (ySortedBoxes, ySortedIndices) = SortPairsBy(chunkBoxes, chunkIndices, axis: 1);
            var yProjection = ProjectionByBboxes(ySortedBoxes, 1);
            var yIntervals = SplitProjectionProfile(yProjection, 0, minGap);
            if (yIntervals is null) continue;

            var (yStarts, yEnds) = yIntervals.Value;
            if (yStarts.Length == 1)
            {
                res.AddRange(ySortedIndices);
                continue;
            }

            for (int seg2 = 0; seg2 < yStarts.Length; seg2++)
            {
                var (selBoxes, selIndices) = SelectInterval(ySortedBoxes, ySortedIndices, axis: 1, yStarts[seg2], yEnds[seg2], useAbs: false);
                RecursiveXyCut(selBoxes, selIndices, res);
            }
        }
    }

    private static (List<float[]> Boxes, List<int> Indices) SortPairsBy(List<float[]> boxes, List<int> indices, int axis)
    {
        var order = new List<int>(boxes.Count);
        for (int i = 0; i < boxes.Count; i++) order.Add(i);
        StableSort(order, (a, b) => boxes[a][axis].CompareTo(boxes[b][axis]));

        var outBoxes = new List<float[]>(boxes.Count);
        var outIndices = new List<int>(boxes.Count);
        foreach (var i in order)
        {
            outBoxes.Add(boxes[i]);
            outIndices.Add(indices[i]);
        }

        return (outBoxes, outIndices);
    }

    private static (List<float[]> Boxes, List<int> Indices) SelectInterval(
        List<float[]> boxes, List<int> indices, int axis, int start, int end, bool useAbs)
    {
        var outBoxes = new List<float[]>();
        var outIndices = new List<int>();
        for (int i = 0; i < boxes.Count; i++)
        {
            float v = useAbs ? Math.Abs(boxes[i][axis]) : boxes[i][axis];
            if (v >= start && v < end)
            {
                outBoxes.Add(boxes[i]);
                outIndices.Add(indices[i]);
            }
        }

        return (outBoxes, outIndices);
    }

    private static float MinStart(List<float[]> boxes, int axis)
    {
        float min = float.PositiveInfinity;
        foreach (var b in boxes) min = Math.Min(min, b[axis]);
        return min;
    }

    /// <summary>
    /// Merges the boxes' projection intervals along one direction and returns the maximal disjoint intervals,
    /// optionally with the box count of each (python <c>calculate_discontinuous_projection</c>).
    /// </summary>
    public static List<(float Start, float End)> CalculateDiscontinuousProjection(
        IReadOnlyList<float[]> boxes, XyDirection direction, out List<int> numList)
    {
        numList = new List<int>();
        var merged = new List<(float Start, float End)>();
        if (boxes.Count == 0) return merged;

        var intervals = new List<(float Start, float End)>(boxes.Count);
        foreach (var b in boxes)
        {
            intervals.Add(direction == XyDirection.Horizontal ? (b[0], b[2]) : (b[1], b[3]));
        }

        StableSort(intervals, (a, b) => a.Start.CompareTo(b.Start));

        var (currentStart, currentEnd) = intervals[0];
        int num = 1;
        for (int i = 1; i < intervals.Count; i++)
        {
            var (start, end) = intervals[i];
            if (start <= currentEnd)
            {
                num++;
                currentEnd = Math.Max(currentEnd, end);
            }
            else
            {
                numList.Add(num);
                merged.Add((currentStart, currentEnd));
                num = 1;
                (currentStart, currentEnd) = (start, end);
            }
        }

        numList.Add(num);
        merged.Add((currentStart, currentEnd));
        return merged;
    }

    /// <summary>Overload discarding the per-interval box counts.</summary>
    public static List<(float Start, float End)> CalculateDiscontinuousProjection(IReadOnlyList<float[]> boxes, XyDirection direction)
        => CalculateDiscontinuousProjection(boxes, direction, out _);

    /// <summary>
    /// Nudges consecutive blocks apart along <paramref name="direction"/> when they barely overlap or touch,
    /// so the projection profile shows a clean 2px gap between them (python <c>shrink_overlapping_boxes</c>).
    /// Mutates the block bboxes in place; call on clones only.
    /// </summary>
    public static List<XyCutBlock> ShrinkOverlappingBoxes(
        List<XyCutBlock> boxes, XyDirection direction, float minThreshold = 0f, float maxThreshold = 0.1f)
    {
        if (boxes.Count == 0) return boxes;
        var currentBlock = boxes[0];
        for (int i = 1; i < boxes.Count; i++)
        {
            var block = boxes[i];
            float x1 = currentBlock.Bbox[0], y1 = currentBlock.Bbox[1], x2 = currentBlock.Bbox[2], y2 = currentBlock.Bbox[3];
            float x1Prime = block.Bbox[0], y1Prime = block.Bbox[1], x2Prime = block.Bbox[2], y2Prime = block.Bbox[3];
            float cutIou = CalculateProjectionOverlapRatio(currentBlock.Bbox, block.Bbox, direction);
            float matchIou = CalculateProjectionOverlapRatio(
                currentBlock.Bbox, block.Bbox,
                direction == XyDirection.Vertical ? XyDirection.Horizontal : XyDirection.Vertical);
            if (direction == XyDirection.Vertical)
            {
                if ((matchIou > 0 && cutIou > minThreshold && cutIou < maxThreshold)
                    || y2 == y1Prime
                    || Math.Abs(y2 - y1Prime) <= 3)
                {
                    float overlapYMin = Math.Max(y1, y1Prime);
                    float overlapYMax = Math.Min(y2, y2Prime);
                    int splitY = (int)((overlapYMin + overlapYMax) / 2f);
                    overlapYMin = splitY - 1;
                    overlapYMax = splitY + 1;
                    if (y1 < y1Prime)
                    {
                        currentBlock.Bbox = new[] { x1, y1, x2, overlapYMin };
                        block.Bbox = new[] { x1Prime, overlapYMax, x2Prime, y2Prime };
                    }
                    else
                    {
                        currentBlock.Bbox = new[] { x1, overlapYMin, x2, y2 };
                        block.Bbox = new[] { x1Prime, y1Prime, x2Prime, overlapYMax };
                    }
                }
            }
            else
            {
                if ((matchIou > 0 && cutIou > minThreshold && cutIou < maxThreshold)
                    || x2 == x1Prime
                    || Math.Abs(x2 - x1Prime) <= 3)
                {
                    float overlapXMin = Math.Max(x1, x1Prime);
                    float overlapXMax = Math.Min(x2, x2Prime);
                    int splitX = (int)((overlapXMin + overlapXMax) / 2f);
                    overlapXMin = splitX - 1;
                    overlapXMax = splitX + 1;
                    if (x1 < x1Prime)
                    {
                        currentBlock.Bbox = new[] { x1, y1, overlapXMin, y2 };
                        block.Bbox = new[] { overlapXMax, y1Prime, x2Prime, y2Prime };
                    }
                    else
                    {
                        currentBlock.Bbox = new[] { overlapXMin, y1, x2, y2 };
                        block.Bbox = new[] { x1Prime, y1Prime, overlapXMax, y2Prime };
                    }
                }
            }

            currentBlock = block;
        }

        return boxes;
    }

    /// <summary>
    /// Flat local-minima plateaus of a projection profile — the candidate column gaps
    /// (python <c>find_local_minima_flat_regions</c>; like it, drops the first plateau and
    /// returns null when at most one was found).
    /// </summary>
    public static List<(int Start, int End)>? FindLocalMinimaFlatRegions(int[] arr)
    {
        int n = arr.Length;
        if (n == 0) return null;

        var regions = new List<(int Start, int End)>();
        int start = 0;
        for (int i = 1; i < n; i++)
        {
            if (arr[i] != arr[i - 1])
            {
                if ((start == 0 || arr[start - 1] > arr[start]) && arr[i] > arr[start])
                {
                    regions.Add((start, i - 1));
                }

                start = i;
            }
        }

        if (regions.Count <= 1) return null;
        regions.RemoveAt(0);
        return regions;
    }

    /// <summary>
    /// Sorts blocks into page order buckets by line height/width (python <c>sort_normal_blocks</c>);
    /// used for headers, footers, unordered blocks and the unsorted-insertion queue.
    /// </summary>
    public static List<XyCutBlock> SortNormalBlocks(
        List<XyCutBlock> blocks, float textLineHeight, float textLineWidth, XyDirection regionDirection)
    {
        float lh = Math.Max(1f, textLineHeight);
        float lw = Math.Max(1f, textLineWidth);
        if (regionDirection == XyDirection.Horizontal)
        {
            StableSort(blocks, (a, b) =>
            {
                int c = MathF.Floor(a.Bbox[1] / lh).CompareTo(MathF.Floor(b.Bbox[1] / lh));
                if (c != 0) return c;
                c = MathF.Floor(a.Bbox[0] / lw).CompareTo(MathF.Floor(b.Bbox[0] / lw));
                if (c != 0) return c;
                return CentroidSquared(a).CompareTo(CentroidSquared(b));
            });
        }
        else
        {
            StableSort(blocks, (a, b) =>
            {
                int c = MathF.Floor(-a.Bbox[2] / lw).CompareTo(MathF.Floor(-b.Bbox[2] / lw));
                if (c != 0) return c;
                c = MathF.Floor(a.Bbox[1] / lh).CompareTo(MathF.Floor(b.Bbox[1] / lh));
                if (c != 0) return c;
                return NegCentroidSquared(a).CompareTo(NegCentroidSquared(b));
            });
        }

        return blocks;

        static double CentroidSquared(XyCutBlock block)
        {
            var (cx, cy) = block.GetCentroid();
            return (double)cx * cx + (double)cy * cy;
        }

        // python key: -cx**2 + cy**2 (unary minus binds after the power).
        static double NegCentroidSquared(XyCutBlock block)
        {
            var (cx, cy) = block.GetCentroid();
            return -((double)cx * cx) + (double)cy * cy;
        }
    }

    /// <summary>
    /// Partitions blocks into groups at the given cut coordinates along <paramref name="cutDirection"/>,
    /// excluding masked order labels (python <c>get_cut_blocks</c>).
    /// </summary>
    public static List<List<XyCutBlock>> GetCutBlocks(
        List<XyCutBlock> blocks, XyDirection cutDirection, List<float> cutCoordinates, HashSet<string> maskLabels)
    {
        var cutList = new List<List<XyCutBlock>>();
        int cutAxis = cutDirection == XyDirection.Horizontal ? 0 : 1;
        StableSort(blocks, (a, b) => a.Bbox[cutAxis + 2].CompareTo(b.Bbox[cutAxis + 2]));

        var coordinates = new List<float>(cutCoordinates) { float.PositiveInfinity };
        coordinates = coordinates.Distinct().ToList();
        coordinates.Sort();

        int cutIdx = 0;
        foreach (var cutCoordinate in coordinates)
        {
            var group = new List<XyCutBlock>();
            int blockIdx = cutIdx;
            while (blockIdx < blocks.Count)
            {
                var block = blocks[blockIdx];
                if (block.Bbox[cutAxis + 2] > cutCoordinate) break;
                if (block.OrderLabel is null || !maskLabels.Contains(block.OrderLabel)) group.Add(block);
                blockIdx++;
            }

            cutIdx = blockIdx;
            if (group.Count > 0) cutList.Add(group);
        }

        return cutList;
    }

    /// <summary>Blocks fully inside [start, end] along a direction (python <c>get_blocks_by_direction_interval</c>).</summary>
    public static List<XyCutBlock> GetBlocksByDirectionInterval(
        List<XyCutBlock> blocks, float start, float end, XyDirection direction)
    {
        int axis = direction == XyDirection.Horizontal ? 0 : 1;
        StableSort(blocks, (a, b) => a.Bbox[axis + 2].CompareTo(b.Bbox[axis + 2]));

        var intervalBlocks = new List<XyCutBlock>();
        foreach (var block in blocks)
        {
            if (block.Bbox[axis] >= start && block.Bbox[axis + 2] <= end) intervalBlocks.Add(block);
        }

        return intervalBlocks;
    }

    /// <summary>
    /// Neighbours of a block along a direction whose projections overlap it by more than the threshold,
    /// split into those before it and after it (python <c>get_nearest_blocks</c>).
    /// </summary>
    public static (List<XyCutBlock> Prev, List<XyCutBlock> Post) GetNearestBlocks(
        XyCutBlock block, List<XyCutBlock> refBlocks, float overlapThreshold, XyDirection direction)
    {
        var prevBlocks = new List<XyCutBlock>();
        var postBlocks = new List<XyCutBlock>();
        int sortIndex = direction == XyDirection.Horizontal ? 1 : 0;
        foreach (var refBlock in refBlocks)
        {
            if (refBlock.MapKey == block.MapKey) continue;
            float overlapRatio = CalculateProjectionOverlapRatio(block.Bbox, refBlock.Bbox, direction, XyOverlapMode.Small);
            if (overlapRatio > overlapThreshold)
            {
                if (refBlock.Bbox[sortIndex] <= block.Bbox[sortIndex]) prevBlocks.Add(refBlock);
                else postBlocks.Add(refBlock);
            }
        }

        StableSort(prevBlocks, (a, b) => b.Bbox[sortIndex].CompareTo(a.Bbox[sortIndex]));
        StableSort(postBlocks, (a, b) => a.Bbox[sortIndex].CompareTo(b.Bbox[sortIndex]));
        return (prevBlocks, postBlocks);
    }
}

/// <summary>
/// Insertion strategies that place titles, visions, cross-layout and reference blocks back into an already
/// sorted flow — ported from PaddleX <c>xycut_enhanced/utils.py</c> (weighted_distance_insert and friends).
/// </summary>
internal static class XyCutInserts
{
    // XYCUT_SETTINGS["distance_weight_map"] (setting.py:19-23). The python code looks up a
    // "left_edge_weight" key that the map does not define, so its 1e-4 default applies.
    private const double EdgeWeight = 10_000d;
    private const double UpEdgeWeight = 1d;
    private const double LeftEdgeWeight = 0.0001d;

    // XYCUT_SETTINGS["edge_distance_compare_tolerance_len"].
    private const float EdgeDistanceCompareToleranceLen = 2f;

    /// <summary>Directional edge weights [left, right, up, down] by order label (python <c>_get_weights</c>).</summary>
    public static float[] GetWeights(string? orderLabel, XyDirection direction) => orderLabel switch
    {
        "doc_title" => direction == XyDirection.Horizontal
            ? new[] { 1f, 0.1f, 0.1f, 1f } // left-down
            : new[] { 0.2f, 0.1f, 1f, 1f }, // right-left
        "paragraph_title" or "table_title" or "abstract" or "image" or "seal" or "chart" or "figure"
            => new[] { 1f, 1f, 0.1f, 1f }, // down
        _ => new[] { 1f, 1f, 1f, 0.1f }, // up
    };

    /// <summary>
    /// Weighted-distance insertion (python <c>weighted_distance_insert</c>): the block lands next to the sorted
    /// block minimizing edge-distance * 1e4 + up-edge * 1 + left-edge * 1e-4, before or after it depending on
    /// which of the two comes first in reading order.
    /// Simplification vs python: the seg-flag lookahead for vision/vision-title blocks (which nudges the
    /// insertion point past a continuation paragraph) needs OCR segment coordinates this port does not have,
    /// so the insertion index is used as computed.
    /// </summary>
    public static List<XyCutBlock> WeightedDistanceInsert(XyCutBlock block, List<XyCutBlock> sortedBlocks, XyCutRegion region)
    {
        float toleranceLen = EdgeDistanceCompareToleranceLen;
        float x1 = block.Bbox[0], y1 = block.Bbox[1], x2 = block.Bbox[2];
        double minWeightedDistance = double.PositiveInfinity;
        double minUpEdgeDistance = double.PositiveInfinity;
        int nearestSortedBlockIndex = 0;

        for (int sortedBlockIdx = 0; sortedBlockIdx < sortedBlocks.Count; sortedBlockIdx++)
        {
            var sortedBlock = sortedBlocks[sortedBlockIdx];
            float x1Prime = sortedBlock.Bbox[0], y1Prime = sortedBlock.Bbox[1];
            float x2Prime = sortedBlock.Bbox[2], y2Prime = sortedBlock.Bbox[3];

            var weight = GetWeights(block.OrderLabel, block.Direction);
            double edgeDistance = XyCutGeometry.GetNearestEdgeDistance(block.Bbox, sortedBlock.Bbox, weight);

            if (XyCutLabelSets.DocTitleLabels.Contains(block.Label))
            {
                float disperse = Math.Max(1f, region.TextLineWidth);
                toleranceLen = Math.Max(toleranceLen, disperse);
            }

            if (block.Label == "abstract")
            {
                toleranceLen *= 2;
                edgeDistance = Math.Max(0.1, edgeDistance) * 10;
            }

            double upEdgeDistance = region.Direction == XyDirection.Horizontal ? y1Prime : -x2Prime;
            double leftEdgeDistance = region.Direction == XyDirection.Horizontal ? x1Prime : y1Prime;
            bool isBelowSortedBlock = region.Direction == XyDirection.Horizontal ? y2Prime < y1 : x1Prime > x2;

            bool orderedLabel = !XyCutLabelSets.UnorderedLabels.Contains(block.Label)
                || XyCutLabelSets.DocTitleLabels.Contains(block.Label)
                || XyCutLabelSets.ParagraphTitleLabels.Contains(block.Label)
                || XyCutLabelSets.VisionLabels.Contains(block.Label);
            if (orderedLabel && isBelowSortedBlock)
            {
                upEdgeDistance = -upEdgeDistance;
                leftEdgeDistance = -leftEdgeDistance;
            }

            if (Math.Abs(minUpEdgeDistance - upEdgeDistance) <= toleranceLen) upEdgeDistance = minUpEdgeDistance;

            double weightedDistance =
                edgeDistance * EdgeWeight + upEdgeDistance * UpEdgeWeight + leftEdgeDistance * LeftEdgeWeight;

            minUpEdgeDistance = Math.Min(upEdgeDistance, minUpEdgeDistance);

            if (weightedDistance < minWeightedDistance)
            {
                nearestSortedBlockIndex = sortedBlockIdx;
                minWeightedDistance = weightedDistance;

                // Decide before/after: compare positions along the flow, falling back to origin distance.
                double sortedDistance, blockDistance;
                if (Math.Abs(Math.Floor(y1 / 2f) - Math.Floor(y1Prime / 2f)) > 0)
                {
                    sortedDistance = y1Prime;
                    blockDistance = y1;
                }
                else if (region.Direction == XyDirection.Horizontal)
                {
                    if (Math.Abs(Math.Floor(x1 / 2f) - Math.Floor(x2 / 2f)) > 0)
                    {
                        sortedDistance = x1Prime;
                        blockDistance = x1;
                    }
                    else
                    {
                        var (scx, scy) = sortedBlock.GetCentroid();
                        var (bcx, bcy) = block.GetCentroid();
                        sortedDistance = (double)scx * scx + (double)scy * scy;
                        blockDistance = (double)bcx * bcx + (double)bcy * bcy;
                    }
                }
                else
                {
                    if (Math.Abs(x1 - x2) > 0)
                    {
                        sortedDistance = -x2Prime;
                        blockDistance = -x2;
                    }
                    else
                    {
                        var (scx, scy) = sortedBlock.GetCentroid();
                        var (bcx, bcy) = block.GetCentroid();
                        sortedDistance = (double)scx * scx + (double)scy * scy;
                        blockDistance = (double)bcx * bcx + (double)bcy * bcy;
                    }
                }

                if (blockDistance > sortedDistance) nearestSortedBlockIndex = sortedBlockIdx + 1;
            }
        }

        sortedBlocks.Insert(Math.Min(nearestSortedBlockIndex, sortedBlocks.Count), block);
        return sortedBlocks;
    }

    /// <summary>
    /// Places a cross-column reference block after the sorted block that best "precedes" it
    /// (python <c>reference_insert</c>, including its quirk of reusing the last computed distance
    /// for sorted blocks that are not above the reference).
    /// </summary>
    public static List<XyCutBlock> ReferenceInsert(XyCutBlock block, List<XyCutBlock> sortedBlocks)
    {
        double minDistance = double.PositiveInfinity;
        double distance = double.PositiveInfinity;
        int nearestSortedBlockIndex = 0;
        for (int idx = 0; idx < sortedBlocks.Count; idx++)
        {
            var sortedBlock = sortedBlocks[idx];
            if (sortedBlock.Bbox[3] <= block.Bbox[1])
            {
                distance = -(sortedBlock.Bbox[2] * 10d + sortedBlock.Bbox[3]);
            }

            if (distance < minDistance)
            {
                minDistance = distance;
                nearestSortedBlockIndex = idx;
            }
        }

        sortedBlocks.Insert(Math.Min(nearestSortedBlockIndex + 1, sortedBlocks.Count), block);
        return sortedBlocks;
    }

    /// <summary>Nearest top-left-corner Manhattan distance insertion (python <c>manhattan_insert</c>).</summary>
    public static List<XyCutBlock> ManhattanInsert(XyCutBlock block, List<XyCutBlock> sortedBlocks)
    {
        double minDistance = double.PositiveInfinity;
        int nearestSortedBlockIndex = 0;
        for (int idx = 0; idx < sortedBlocks.Count; idx++)
        {
            var sortedBlock = sortedBlocks[idx];
            double distance = Math.Abs(block.Bbox[0] - sortedBlock.Bbox[0]) + Math.Abs(block.Bbox[1] - sortedBlock.Bbox[1]);
            if (distance < minDistance)
            {
                minDistance = distance;
                nearestSortedBlockIndex = idx;
            }
        }

        sortedBlocks.Insert(Math.Min(nearestSortedBlockIndex + 1, sortedBlocks.Count), block);
        return sortedBlocks;
    }

    /// <summary>
    /// Inserts a region-labelled block before the first sorted block farther from the page origin
    /// (python <c>euclidean_insert</c>). Simplification: python uses the LayoutRegion's min member-block
    /// distance; without member blocks this port uses the block's own leading corner.
    /// </summary>
    public static List<XyCutBlock> EuclideanInsert(XyCutBlock block, List<XyCutBlock> sortedBlocks, XyCutRegion region)
    {
        int nearestSortedBlockIndex = sortedBlocks.Count;
        double blockDistance = EuclideanDistance(block, region);
        for (int idx = 0; idx < sortedBlocks.Count; idx++)
        {
            if (EuclideanDistance(sortedBlocks[idx], region) > blockDistance)
            {
                nearestSortedBlockIndex = idx;
                break;
            }
        }

        sortedBlocks.Insert(nearestSortedBlockIndex, block);
        return sortedBlocks;
    }

    /// <summary>Distance of the block's reading-start corner from the region's reading origin.</summary>
    public static double EuclideanDistance(XyCutBlock block, XyCutRegion region)
    {
        if (region.Direction == XyDirection.Horizontal)
        {
            return Math.Sqrt((double)block.Bbox[0] * block.Bbox[0] + (double)block.Bbox[1] * block.Bbox[1]);
        }

        double dx = region.Bbox[2] - block.Bbox[2];
        return Math.Sqrt(dx * dx + (double)block.Bbox[1] * block.Bbox[1]);
    }

    /// <summary>
    /// Splices a parent's absorbed children back into the final order right around the parent
    /// (python <c>insert_child_blocks</c>); the parent's original bbox is restored in the process.
    /// </summary>
    public static void InsertChildBlocks(XyCutBlock block, int blockIdx, List<XyCutBlock> sortedBlocks, XyCutRegion region)
    {
        if (block.ChildBlocks.Count == 0) return;
        var subBlocks = block.TakeChildBlocks();
        subBlocks.Add(block);
        SortChildBlocks(subBlocks, subBlocks[0].Direction, region);
        sortedBlocks[blockIdx] = subBlocks[0];
        for (int i = 1; i < subBlocks.Count; i++)
        {
            blockIdx++;
            sortedBlocks.Insert(blockIdx, subBlocks[i]);
        }
    }

    /// <summary>Orders a parent-plus-children cluster locally (python <c>sort_child_blocks</c>).</summary>
    public static void SortChildBlocks(List<XyCutBlock> blocks, XyDirection direction, XyCutRegion region)
    {
        if (blocks.Count == 0) return;
        if (blocks[0].Label != "region")
        {
            if (direction == XyDirection.Horizontal)
            {
                XyCutGeometry.StableSort(blocks, (a, b) =>
                {
                    int c = a.Bbox[1].CompareTo(b.Bbox[1]);
                    if (c != 0) return c;
                    c = a.Bbox[0].CompareTo(b.Bbox[0]);
                    if (c != 0) return c;
                    return CentroidSquared(a).CompareTo(CentroidSquared(b));
                });
            }
            else
            {
                XyCutGeometry.StableSort(blocks, (a, b) =>
                {
                    int c = (-a.Bbox[2]).CompareTo(-b.Bbox[2]);
                    if (c != 0) return c;
                    c = a.Bbox[1].CompareTo(b.Bbox[1]);
                    if (c != 0) return c;
                    return NegCentroidSquared(a).CompareTo(NegCentroidSquared(b));
                });
            }
        }
        else
        {
            XyCutGeometry.StableSort(blocks, (a, b) => EuclideanDistance(a, region).CompareTo(EuclideanDistance(b, region)));
        }

        static double CentroidSquared(XyCutBlock block)
        {
            var (cx, cy) = block.GetCentroid();
            return (double)cx * cx + (double)cy * cy;
        }

        // python key: -cx**2 + cy**2 (unary minus binds after the power).
        static double NegCentroidSquared(XyCutBlock block)
        {
            var (cx, cy) = block.GetCentroid();
            return -((double)cx * cx) + (double)cy * cy;
        }
    }
}

/// <summary>
/// Parent/child matching — doc-title subtitle text, adjacent sub-paragraph titles, vision titles and vision
/// footnotes are absorbed into their parent block (union bbox) so they travel with it through the cuts and
/// are re-emitted next to it afterwards. Ported from PaddleX <c>xycut_enhanced/utils.py:742-1061</c>.
/// </summary>
internal static class XyCutChildMatcher
{
    // XYCUT_SETTINGS["child_block_overlap_ratio_threshold"].
    private const float ChildBlockOverlapRatioThreshold = 0.1f;

    /// <summary>Matches short text lines hugging a doc title as its children (python <c>update_doc_title_child_blocks</c>).</summary>
    public static void UpdateDocTitleChildBlocks(XyCutBlock block, XyCutRegion region)
    {
        var refBlocks = new List<XyCutBlock>(region.NormalTextIdxes.Count);
        foreach (var idx in region.NormalTextIdxes) refBlocks.Add(region.Blocks[idx]);

        var (prevBlocks, postBlocks) = XyCutGeometry.GetNearestBlocks(
            block, refBlocks, ChildBlockOverlapRatioThreshold, block.Direction);
        var candidates = new List<XyCutBlock>(2);
        if (prevBlocks.Count > 0) candidates.Add(prevBlocks[0]);
        if (postBlocks.Count > 0) candidates.Add(postBlocks[0]);

        foreach (var refBlock in candidates)
        {
            bool withSameDirection = refBlock.Direction == block.Direction;
            bool shortSideCondition = refBlock.ShortSideLength < block.ShortSideLength * 0.8f;
            bool longSideCondition = refBlock.LongSideLength < block.LongSideLength
                || refBlock.LongSideLength > 1.5f * block.LongSideLength;
            float nearestEdgeDistance = XyCutGeometry.GetNearestEdgeDistance(block.Bbox, refBlock.Bbox);

            if (withSameDirection
                && XyCutLabelSets.TextLabels.Contains(refBlock.Label)
                && shortSideCondition
                && longSideCondition
                && refBlock.NumOfLines < 3
                && nearestEdgeDistance < refBlock.TextLineHeight * 2)
            {
                refBlock.OrderLabel = "doc_title_text";
                block.AppendChildBlock(refBlock);
                region.NormalTextIdxes.Remove(refBlock.MapKey);
            }
        }

        foreach (var refBlock in refBlocks)
        {
            if (refBlock.OrderLabel == "doc_title_text") continue;
            bool withSameDirection = refBlock.Direction == block.Direction;
            float overlapRatio = XyCutGeometry.CalculateOverlapRatio(block.Bbox, refBlock.Bbox, XyOverlapMode.Small);
            if (overlapRatio > 0.9f && withSameDirection)
            {
                refBlock.OrderLabel = "doc_title_text";
                block.AppendChildBlock(refBlock);
                region.NormalTextIdxes.Remove(refBlock.MapKey);
            }
        }
    }

    /// <summary>Chains adjacent aligned paragraph titles under the first one (python <c>update_paragraph_title_child_blocks</c>).</summary>
    public static void UpdateParagraphTitleChildBlocks(XyCutBlock block, XyCutRegion region)
    {
        if (block.OrderLabel == "sub_paragraph_title") return;

        var refBlocks = new List<XyCutBlock>();
        foreach (var idx in region.ParagraphTitleIdxes) refBlocks.Add(region.Blocks[idx]);
        foreach (var idx in region.NormalTextIdxes) refBlocks.Add(region.Blocks[idx]);

        var (prevBlocks, postBlocks) = XyCutGeometry.GetNearestBlocks(
            block, refBlocks, ChildBlockOverlapRatioThreshold, block.Direction);
        foreach (var neighborList in new[] { prevBlocks, postBlocks })
        {
            foreach (var refBlock in neighborList)
            {
                if (!XyCutLabelSets.ParagraphTitleLabels.Contains(refBlock.Label)) break;
                float minTextLineHeight = Math.Min(block.TextLineHeight, refBlock.TextLineHeight);
                float nearestEdgeDistance = XyCutGeometry.GetNearestEdgeDistance(block.Bbox, refBlock.Bbox);
                bool withSameDirection = refBlock.Direction == block.Direction;
                bool withSameStart = Math.Abs(refBlock.StartCoordinate - block.StartCoordinate) < minTextLineHeight * 2;
                if (withSameDirection && withSameStart && nearestEdgeDistance <= minTextLineHeight * 1.5f)
                {
                    refBlock.OrderLabel = "sub_paragraph_title";
                    block.AppendChildBlock(refBlock);
                    region.ParagraphTitleIdxes.Remove(refBlock.MapKey);
                }
            }
        }
    }

    /// <summary>
    /// Attaches vision titles (figure/table/chart captions) and a single vision footnote to a vision block
    /// (python <c>update_vision_child_blocks</c>).
    /// </summary>
    public static void UpdateVisionChildBlocks(XyCutBlock block, XyCutRegion region)
    {
        var refBlocks = new List<XyCutBlock>();
        foreach (var idx in region.NormalTextIdxes) refBlocks.Add(region.Blocks[idx]);
        foreach (var idx in region.VisionTitleIdxes) refBlocks.Add(region.Blocks[idx]);

        bool hasVisionFootnote = false;
        bool hasVisionTitle = false;
        foreach (var direction in new[] { block.Direction, block.SecondaryDirection })
        {
            var (prevBlocks, postBlocks) = XyCutGeometry.GetNearestBlocks(
                block, refBlocks, ChildBlockOverlapRatioThreshold, direction);
            foreach (var refBlock in prevBlocks)
            {
                if (!XyCutLabelSets.TextLabels.Contains(refBlock.Label)
                    && !XyCutLabelSets.VisionTitleLabels.Contains(refBlock.Label))
                {
                    break;
                }

                float nearestEdgeDistance = XyCutGeometry.GetNearestEdgeDistance(block.Bbox, refBlock.Bbox);
                if (XyCutLabelSets.VisionTitleLabels.Contains(refBlock.Label)
                    && nearestEdgeDistance <= refBlock.TextLineHeight * 2)
                {
                    hasVisionTitle = true;
                    refBlock.OrderLabel = "vision_title";
                    block.AppendChildBlock(refBlock);
                    region.VisionTitleIdxes.Remove(refBlock.MapKey);
                }

                if (XyCutLabelSets.TextLabels.Contains(refBlock.Label))
                {
                    if (!hasVisionFootnote
                        && refBlock.Direction == block.Direction
                        && refBlock.LongSideLength < block.LongSideLength
                        && nearestEdgeDistance <= refBlock.TextLineHeight * 2
                        && IsVisionFootnoteShaped(block, refBlock))
                    {
                        hasVisionFootnote = true;
                        refBlock.OrderLabel = "vision_footnote";
                        block.AppendChildBlock(refBlock);
                        region.NormalTextIdxes.Remove(refBlock.MapKey);
                    }

                    break;
                }
            }

            foreach (var refBlock in postBlocks)
            {
                if (hasVisionFootnote && XyCutLabelSets.TextLabels.Contains(refBlock.Label)) break;
                float nearestEdgeDistance = XyCutGeometry.GetNearestEdgeDistance(block.Bbox, refBlock.Bbox);
                if (XyCutLabelSets.VisionTitleLabels.Contains(refBlock.Label)
                    && nearestEdgeDistance <= refBlock.TextLineHeight * 2)
                {
                    hasVisionTitle = true;
                    refBlock.OrderLabel = "vision_title";
                    block.AppendChildBlock(refBlock);
                    region.VisionTitleIdxes.Remove(refBlock.MapKey);
                }

                if (XyCutLabelSets.TextLabels.Contains(refBlock.Label))
                {
                    if (!hasVisionFootnote
                        && refBlock.Direction == block.Direction
                        && refBlock.LongSideLength < block.LongSideLength
                        && nearestEdgeDistance <= refBlock.TextLineHeight * 2
                        && IsVisionFootnoteShaped(block, refBlock))
                    {
                        hasVisionFootnote = true;
                        refBlock.Label = "vision_footnote";
                        refBlock.OrderLabel = "vision_footnote";
                        block.AppendChildBlock(refBlock);
                        region.NormalTextIdxes.Remove(refBlock.MapKey);
                    }

                    break;
                }
            }

            if (hasVisionTitle) break;
        }

        foreach (var refBlock in refBlocks)
        {
            if (!region.NormalTextIdxes.Contains(refBlock.MapKey)) continue;
            float overlapRatio = XyCutGeometry.CalculateOverlapRatio(block.Bbox, refBlock.Bbox, XyOverlapMode.Small);
            if (overlapRatio > 0.9f)
            {
                refBlock.Label = "vision_footnote";
                refBlock.OrderLabel = "vision_footnote";
                block.AppendChildBlock(refBlock);
                region.NormalTextIdxes.Remove(refBlock.MapKey);
            }
        }
    }

    /// <summary>The three footnote-shape alternatives shared by the prev/post vision loops.</summary>
    private static bool IsVisionFootnoteShaped(XyCutBlock block, XyCutBlock refBlock)
    {
        var blockCenter = block.GetCentroid();
        var refCenter = refBlock.GetCentroid();
        return (refBlock.ShortSideLength < block.ShortSideLength
                && refBlock.LongSideLength < 0.5f * block.LongSideLength
                && Math.Abs(blockCenter.X - refCenter.X) < 10)
            || (block.Bbox[0] - refBlock.Bbox[0] < 10 && refBlock.NumOfLines == 1)
            || (block.Bbox[2] - refBlock.Bbox[2] < 10 && refBlock.NumOfLines == 1);
    }

    /// <summary>Absorbs blocks overlapped by a larger region block (python <c>update_region_child_blocks</c>).</summary>
    public static void UpdateRegionChildBlocks(XyCutBlock block, XyCutRegion region)
    {
        foreach (var refBlock in region.Blocks)
        {
            if (refBlock.MapKey == block.MapKey) continue;
            float bboxIou = XyCutGeometry.CalculateOverlapRatio(block.Bbox, refBlock.Bbox);
            if (bboxIou > 0 && block.Area > refBlock.Area && refBlock.OrderLabel != "sub_region")
            {
                refBlock.OrderLabel = "sub_region";
                block.AppendChildBlock(refBlock);
                region.NormalTextIdxes.Remove(refBlock.MapKey);
            }
        }
    }
}
