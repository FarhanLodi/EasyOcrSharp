namespace EasyOcrSharp.Structure.Engine.Geometry;

/// <summary>
/// 8-connected component labeling on a binary mask using two-pass union-find (diagonally-touching
/// pixels join one region, matching cv2.findContours contour/region semantics).
/// Returns labels[y*w+x] = component id (0 = background) plus per-component stats.
/// <para>
/// Component ids are dense and assigned in raster order of each component's first pixel. The union-find
/// forest and the per-component statistics live in flat, growable <see cref="int"/> arrays (no per-pixel
/// dictionary lookups or record copies), which keeps the second pass a tight array scan.
/// </para>
/// </summary>
internal static class ConnectedComponents
{
    /// <summary>
    /// Axis-aligned extent and pixel count of one labeled component.
    /// </summary>
    /// <param name="MinX">Leftmost column containing the component.</param>
    /// <param name="MinY">Topmost row containing the component.</param>
    /// <param name="MaxX">Rightmost column containing the component.</param>
    /// <param name="MaxY">Bottommost row containing the component.</param>
    /// <param name="Area">Number of pixels in the component.</param>
    internal readonly record struct Stats(int MinX, int MinY, int MaxX, int MaxY, int Area);

    /// <summary>
    /// Labels the 8-connected foreground components of <paramref name="mask"/> (non-zero = foreground).
    /// </summary>
    /// <param name="mask">Row-major binary mask of length <c>width*height</c>.</param>
    /// <param name="width">Mask width.</param>
    /// <param name="height">Mask height.</param>
    /// <returns>The dense label map and per-label stats (index 0 is a background placeholder).</returns>
    public static (int[] Labels, Stats[] Components) Label(ReadOnlySpan<byte> mask, int width, int height)
    {
        var labels = new int[width * height];
        var parents = new int[256];
        var sizes = new int[256];
        int count = 1; // index 0 = background

        // First pass: provisional labels over the four already-visited 8-neighbors
        // (left, up-left, up, up-right).
        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = rowBase + x;
                if (mask[idx] == 0) continue;

                int left = (x > 0) ? labels[idx - 1] : 0;
                int up = (y > 0) ? labels[idx - width] : 0;
                int upLeft = (x > 0 && y > 0) ? labels[idx - width - 1] : 0;
                int upRight = (x < width - 1 && y > 0) ? labels[idx - width + 1] : 0;

                if (left == 0 && up == 0 && upLeft == 0 && upRight == 0)
                {
                    if (count == parents.Length)
                    {
                        Array.Resize(ref parents, count * 2);
                        Array.Resize(ref sizes, count * 2);
                    }
                    parents[count] = count;
                    sizes[count] = 1;
                    labels[idx] = count;
                    count++;
                }
                else
                {
                    int assigned = left != 0 ? left : up != 0 ? up : upLeft != 0 ? upLeft : upRight;
                    if (left != 0 && left != assigned) Union(parents, sizes, assigned, left);
                    if (up != 0 && up != assigned) Union(parents, sizes, assigned, up);
                    if (upLeft != 0 && upLeft != assigned) Union(parents, sizes, assigned, upLeft);
                    if (upRight != 0 && upRight != assigned) Union(parents, sizes, assigned, upRight);
                    labels[idx] = Find(parents, assigned);
                }
            }
        }

        // Second pass: resolve provisional labels to dense ids (first-seen raster order) and gather stats
        // in parallel arrays. rootToDense[root] == 0 means "not yet assigned".
        var rootToDense = new int[count];
        var minX = new int[count];
        var minY = new int[count];
        var maxX = new int[count];
        var maxY = new int[count];
        var area = new int[count];
        int dense = 1;

        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = rowBase + x;
                int lbl = labels[idx];
                if (lbl == 0) continue;

                int root = Find(parents, lbl);
                int d = rootToDense[root];
                if (d == 0)
                {
                    d = dense++;
                    rootToDense[root] = d;
                    minX[d] = x;
                    minY[d] = y;
                    maxX[d] = x;
                    maxY[d] = y;
                }
                else
                {
                    // Raster order: y never decreases, so only MinX / MaxX / MaxY can move.
                    if (x < minX[d]) minX[d] = x;
                    if (x > maxX[d]) maxX[d] = x;
                    maxY[d] = y;
                }

                area[d]++;
                labels[idx] = d;
            }
        }

        var stats = new Stats[dense];
        for (int i = 1; i < dense; i++)
        {
            stats[i] = new Stats(minX[i], minY[i], maxX[i], maxY[i], area[i]);
        }

        return (labels, stats);
    }

    private static int Find(int[] parents, int x)
    {
        while (parents[x] != x)
        {
            parents[x] = parents[parents[x]]; // path compression
            x = parents[x];
        }
        return x;
    }

    private static void Union(int[] parents, int[] sizes, int a, int b)
    {
        a = Find(parents, a);
        b = Find(parents, b);
        if (a == b) return;
        if (sizes[a] < sizes[b]) (a, b) = (b, a);
        parents[b] = a;
        sizes[a] += sizes[b];
    }
}
