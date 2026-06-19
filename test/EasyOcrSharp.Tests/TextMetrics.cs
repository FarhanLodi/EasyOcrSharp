namespace EasyOcrSharp.Tests;

/// <summary>
/// Character- and word-error-rate metrics (Levenshtein edit distance over characters / over whitespace
/// tokens) used by the OCR accuracy regression harness. CER = edits(reference, hypothesis) / |reference|.
/// </summary>
internal static class TextMetrics
{
    public static double CharacterErrorRate(string reference, string hypothesis)
    {
        reference ??= string.Empty;
        hypothesis ??= string.Empty;
        if (reference.Length == 0) return hypothesis.Length == 0 ? 0.0 : 1.0;
        return (double)Levenshtein(reference.AsSpan(), hypothesis.AsSpan()) / reference.Length;
    }

    public static double WordErrorRate(string reference, string hypothesis)
    {
        var r = Tokenize(reference);
        var h = Tokenize(hypothesis);
        if (r.Length == 0) return h.Length == 0 ? 0.0 : 1.0;
        return (double)Levenshtein(r, h) / r.Length;
    }

    private static string[] Tokenize(string? s)
        => (s ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static int Levenshtein(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    private static int Levenshtein(string[] a, string[] b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
