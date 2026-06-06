using System.Text;

namespace EasyOcrSharp.Tests;

/// <summary>Shared helpers for locating test fixtures and comparing OCR output loosely.</summary>
internal static class TestAssets
{
    public static string? Image(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", name);
        return File.Exists(path) ? path : null;
    }

    public static string? Pdf(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "pdf", name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Finds the first PDF in <c>assets/pdf/</c> whose file name contains any of the given keywords
    /// (case-insensitive). Lets fixtures be named descriptively (e.g. <c>invoice_778899.pdf</c>) rather
    /// than an exact fixed name. Returns null if none match.
    /// </summary>
    public static string? PdfMatching(params string[] keywords)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "assets", "pdf");
        if (!Directory.Exists(dir)) return null;

        foreach (var file in Directory.EnumerateFiles(dir, "*.pdf"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                return file;
            }
        }
        return null;
    }

    /// <summary>Upper-cases and strips everything but letters/digits, so assertions tolerate OCR spacing/punctuation noise.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }
}
