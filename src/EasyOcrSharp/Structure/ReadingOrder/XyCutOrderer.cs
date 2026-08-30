using EasyOcrSharp.Structure.Engine.Models;

using OcrLine = EasyOcrSharp.Models.OcrLine;
using OcrPoint = EasyOcrSharp.Models.OcrPoint;
using OcrBoundingBox = EasyOcrSharp.Models.OcrBoundingBox;

namespace EasyOcrSharp.Structure.ReadingOrder;

/// <summary>
/// Determines document reading order via the recursive XY-cut algorithm. The set of block bounding boxes
/// is first projected onto the X axis and split at every empty vertical gap (a column of pixels covered by
/// no block) into <i>columns</i>, taken left → right; each column is then projected onto the Y axis and
/// split at every empty horizontal gap into <i>bands</i>, taken top → bottom; each band recurses (its
/// blocks may themselves form sub-columns). Recursion stops when a leaf holds a single block, or when no
/// further clean cut exists (the leaf's blocks are then emitted top-to-bottom / left-to-right). The result
/// is the natural multi-column reading order — every block of the left column is read fully before the
/// right column begins. <see cref="Order"/> returns a permutation of the input indices in that order.
/// </summary>
internal static class XyCutOrderer
{
    /// <summary>
    /// Minimum width (px) of an empty strip in a projection profile for it to count as a cut. A 1px gap is
    /// enough to separate two blocks; using ~1px keeps tightly-packed but non-overlapping columns/rows
    /// separable while ignoring sub-pixel rounding.
    /// </summary>
    private const double MinGap = 1.0;

    /// <summary>
    /// Returns the input block indices reordered into reading order.
    /// </summary>
    /// <param name="blocks">The block bounding boxes, in arbitrary order (source-image pixel coordinates).</param>
    /// <param name="rightToLeft">
    /// When <c>true</c>, columns are emitted right-to-left and same-row blocks are ordered right-to-left —
    /// the reading order of an Arabic or Hebrew page. Defaults to <c>false</c>.
    /// </param>
    /// <returns>A permutation of <c>0..blocks.Count-1</c> giving the reading order.</returns>
    public static IReadOnlyList<int> Order(IReadOnlyList<OcrBoundingBox> blocks, bool rightToLeft = false)
    {
        if (blocks.Count == 0) return Array.Empty<int>();
        if (blocks.Count == 1) return new[] { 0 };

        // Work on the full index set; the recursion appends leaves to `result` in reading order.
        var all = new int[blocks.Count];
        for (int i = 0; i < all.Length; i++) all[i] = i;

        var result = new List<int>(blocks.Count);
        // Start by cutting on X (into columns) — a page's coarsest structure is its columns; CutX falls
        // through to CutY when the group forms a single column, so either starting axis converges, but
        // X-first matches the "left column fully then right" ordering the pipeline wants.
        CutX(blocks, all, result, rightToLeft);
        return result;
    }

    /// <summary>
    /// Projects <paramref name="group"/> onto the X axis, splits it at empty vertical gaps into columns
    /// (left → right) and recurses each column into <see cref="CutY"/>. When the group forms a single
    /// column (no vertical gap splits it) it is handed to <see cref="CutY"/> directly to attempt a
    /// horizontal cut, so the two axes alternate until the group can no longer be divided.
    /// </summary>
    private static void CutX(IReadOnlyList<OcrBoundingBox> blocks, IReadOnlyList<int> group, List<int> result, bool rightToLeft)
    {
        if (group.Count == 1)
        {
            result.Add(group[0]);
            return;
        }

        var columns = SplitByGaps(blocks, group, horizontal: false);
        if (columns.Count <= 1)
        {
            // No vertical gap divides the group → it is a single column; try to cut it horizontally.
            CutY(blocks, group, result, rightToLeft);
            return;
        }

        // Columns come out ordered left → right (by their min-X). A right-to-left page is read starting
        // from the right-most column, so walk them in reverse.
        if (rightToLeft)
        {
            for (int i = columns.Count - 1; i >= 0; i--)
                CutY(blocks, columns[i], result, rightToLeft);
        }
        else
        {
            foreach (var column in columns)
                CutY(blocks, column, result, rightToLeft);
        }
    }

    /// <summary>
    /// Projects <paramref name="group"/> onto the Y axis, splits it at empty horizontal gaps into bands
    /// (top → bottom) and recurses each band into <see cref="CutX"/> (a band may split into sub-columns).
    /// When no horizontal gap divides the group, recursion has bottomed out: the group is emitted directly
    /// in top-to-bottom / left-to-right order.
    /// </summary>
    private static void CutY(IReadOnlyList<OcrBoundingBox> blocks, IReadOnlyList<int> group, List<int> result, bool rightToLeft)
    {
        if (group.Count == 1)
        {
            result.Add(group[0]);
            return;
        }

        var bands = SplitByGaps(blocks, group, horizontal: true);
        if (bands.Count <= 1)
        {
            // Neither axis can divide this group (it is a single column AND a single band) → leaf.
            // Emit its blocks in reading order so overlapping/nested boxes still come out sensibly.
            EmitLeaf(blocks, group, result, rightToLeft);
            return;
        }

        // Bands already come out ordered top → bottom (by their min-Y). Recurse each on the X axis so a
        // band that spans multiple columns is itself column-ordered.
        foreach (var band in bands)
            CutX(blocks, band, result, rightToLeft);
    }

    /// <summary>
    /// Splits <paramref name="group"/> into maximal sub-groups separated by empty strips in the projection
    /// onto one axis: the X axis (<paramref name="horizontal"/> = false) yields columns ordered by min-X;
    /// the Y axis (<paramref name="horizontal"/> = true) yields bands ordered by min-Y. A "gap" is a span
    /// of at least <see cref="MinGap"/> along the axis that no block in the group covers. Returns one
    /// sub-group when the projection is solid (no qualifying gap).
    /// </summary>
    /// <param name="blocks">All blocks on the page, indexed by <paramref name="group"/>.</param>
    /// <param name="group">Indices into <paramref name="blocks"/> forming the group being cut.</param>
    /// <param name="horizontal">
    /// When true, project onto Y and cut at empty horizontal gaps (→ top/bottom bands); when false, project
    /// onto X and cut at empty vertical gaps (→ left/right columns).
    /// </param>
    private static List<List<int>> SplitByGaps(
        IReadOnlyList<OcrBoundingBox> blocks, IReadOnlyList<int> group, bool horizontal)
    {
        // Project every block to its [start, end] interval along the cut axis and sort intervals by start.
        var intervals = new List<(double Start, double End, int Index)>(group.Count);
        foreach (var idx in group)
        {
            var b = blocks[idx];
            var (start, end) = horizontal ? (b.MinY, b.MaxY) : (b.MinX, b.MaxX);
            // Guard against inverted/degenerate boxes so the sweep below always advances coverage forward.
            if (end < start) (start, end) = (end, start);
            intervals.Add((start, end, idx));
        }
        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));

        // Sweep the sorted intervals, accumulating one open run; when the next interval starts more than
        // MinGap past the running coverage end, the run is closed and a new one begins. This is the 1-D
        // analogue of finding empty strips in the projection profile.
        var result = new List<List<int>>();
        var current = new List<int> { intervals[0].Index };
        double coverageEnd = intervals[0].End;

        for (int i = 1; i < intervals.Count; i++)
        {
            var iv = intervals[i];
            if (iv.Start - coverageEnd > MinGap)
            {
                // Empty strip wide enough to be a cut → close the current run, open a new one.
                result.Add(current);
                current = new List<int>();
            }
            current.Add(iv.Index);
            if (iv.End > coverageEnd) coverageEnd = iv.End;
        }
        result.Add(current);
        return result;
    }

    /// <summary>
    /// Emits a recursion leaf — a group that neither a vertical nor a horizontal gap can divide (mutually
    /// overlapping boxes). They are ordered top-to-bottom then left-to-right so the output is still a
    /// sensible local reading order rather than the arbitrary input order.
    /// </summary>
    private static void EmitLeaf(IReadOnlyList<OcrBoundingBox> blocks, IReadOnlyList<int> group, List<int> result, bool rightToLeft)
    {
        var ordered = new List<int>(group);
        ordered.Sort((x, y) =>
        {
            int c = blocks[x].MinY.CompareTo(blocks[y].MinY);
            if (c != 0) return c;
            return rightToLeft
                ? blocks[y].MaxX.CompareTo(blocks[x].MaxX)
                : blocks[x].MinX.CompareTo(blocks[y].MinX);
        });
        result.AddRange(ordered);
    }
}
