namespace EasyOcrSharp.Internal;

/// <summary>
/// 4-connected component labeling on a binary mask using two-pass union-find.
/// Returns labels[y*w+x] = component id (0 = background) plus per-component stats.
/// </summary>
internal static class ConnectedComponents
{
    public readonly record struct Stats(int MinX, int MinY, int MaxX, int MaxY, int Area);

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

        // First pass: provisional labels.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                if (mask[idx] == 0) continue;

                int up = (y > 0) ? labels[idx - width] : 0;
                int left = (x > 0) ? labels[idx - 1] : 0;

                if (up == 0 && left == 0)
                {
                    int newLabel = parents.Count;
                    parents.Add(newLabel);
                    sizes.Add(1);
                    labels[idx] = newLabel;
                }
                else if (up != 0 && left != 0 && up != left)
                {
                    Union(up, left);
                    labels[idx] = Find(up);
                }
                else
                {
                    labels[idx] = (up != 0) ? up : left;
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
