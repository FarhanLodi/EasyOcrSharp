namespace EasyOcrSharp.Structure.Engine.Geometry;

/// <summary>
/// 8-connected component labeling on a binary mask using two-pass union-find (diagonally-touching
/// pixels join one region, matching cv2.findContours contour/region semantics).
/// Returns labels[y*w+x] = component id (0 = background) plus per-component stats.
/// </summary>
internal static class ConnectedComponents
{
    internal readonly record struct Stats(int MinX, int MinY, int MaxX, int MaxY, int Area);

    public static (int[] Labels, Stats[] Components) Label(ReadOnlySpan<byte> mask, int width, int height)
    {
        var labels = new int[width * height];
        var parents = new List<int> { 0 }; // index 0 = background
        var sizes = new List<int> { 0 };

        int Find(int x)
        {
            while (parents[x] != x)
            {
                parents[x] = parents[parents[x]]; // path compression
                x = parents[x];
            }
            return x;
        }

        void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a == b) return;
            if (sizes[a] < sizes[b]) (a, b) = (b, a);
            parents[b] = a;
            sizes[a] += sizes[b];
        }

        // First pass: provisional labels over the four already-visited 8-neighbors
        // (left, up-left, up, up-right).
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                if (mask[idx] == 0) continue;

                int left = (x > 0) ? labels[idx - 1] : 0;
                int up = (y > 0) ? labels[idx - width] : 0;
                int upLeft = (x > 0 && y > 0) ? labels[idx - width - 1] : 0;
                int upRight = (x < width - 1 && y > 0) ? labels[idx - width + 1] : 0;

                if (left == 0 && up == 0 && upLeft == 0 && upRight == 0)
                {
                    int newLabel = parents.Count;
                    parents.Add(newLabel);
                    sizes.Add(1);
                    labels[idx] = newLabel;
                }
                else
                {
                    int assigned = left != 0 ? left : up != 0 ? up : upLeft != 0 ? upLeft : upRight;
                    if (left != 0 && left != assigned) Union(assigned, left);
                    if (up != 0 && up != assigned) Union(assigned, up);
                    if (upLeft != 0 && upLeft != assigned) Union(assigned, upLeft);
                    if (upRight != 0 && upRight != assigned) Union(assigned, upRight);
                    labels[idx] = Find(assigned);
                }
            }
        }

        // Second pass: resolve to canonical labels & compute stats.
        // Map canonical-root → dense index.
        var rootToDense = new Dictionary<int, int>();
        var statsList = new List<Stats> { new(0, 0, 0, 0, 0) }; // background placeholder

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                int lbl = labels[idx];
                if (lbl == 0) continue;

                int root = Find(lbl);
                if (!rootToDense.TryGetValue(root, out int dense))
                {
                    dense = statsList.Count;
                    rootToDense[root] = dense;
                    statsList.Add(new Stats(x, y, x, y, 0));
                }

                var s = statsList[dense];
                statsList[dense] = new Stats(
                    Math.Min(s.MinX, x),
                    Math.Min(s.MinY, y),
                    Math.Max(s.MaxX, x),
                    Math.Max(s.MaxY, y),
                    s.Area + 1);

                labels[idx] = dense;
            }
        }

        return (labels, statsList.ToArray());
    }
}
