using System.Text;
using EasyOcrSharp.Models;
using EasyImageSharp;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Builds the degraded variants of the bundled corpus pages that the detection/input options target, and the
/// order-insensitive scores used to compare a variant's OCR against the clean page's. Shared by
/// <see cref="DetectionInputIntegrationTests"/> and <see cref="DetectionInputBenchmarkTests"/>.
/// </summary>
internal static class DegradedImages
{
    /// <summary>The page at half resolution (small text — what <see cref="DetectionOptions.MinTextHeight"/> targets).</summary>
    public static Image<Rgb24> HalfScale(Image<Rgb24> page)
        => page.Clone(c => c.Resize(Math.Max(1, page.Width / 2), Math.Max(1, page.Height / 2)));

    /// <summary>
    /// A faded, unevenly lit scan (what <see cref="DetectionOptions.EnhanceContrast"/> targets): contrast cut to
    /// 30 % towards the paper, then a left-to-right illumination falloff of up to 22 %.
    /// </summary>
    public static Image<Rgb24> Faded(Image<Rgb24> page)
    {
        var faded = page.Clone();
        int width = faded.Width;
        faded.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    double light = 0.78 + (0.22 * x / Math.Max(1, width - 1));
                    row[x] = new Rgb24(Fade(row[x].R, light), Fade(row[x].G, light), Fade(row[x].B, light));
                }
            }
        });
        return faded;

        static byte Fade(byte v, double light) => (byte)Math.Clamp((255 - ((255 - v) * 0.3)) * light, 0, 255);
    }

    /// <summary>
    /// A 204×98 DPI fax rendition of a square-pixel page (what <see cref="PreprocessingOptions.CorrectNonSquarePixels"/>
    /// targets): the same width, the height scaled by 98/204, and resolution metadata declaring it.
    /// </summary>
    public static Image<Rgb24> Fax(Image<Rgb24> page)
    {
        int height = Math.Max(1, (int)Math.Round(page.Height * 98.0 / 204.0));
        var fax = page.Clone(c => c.Resize(page.Width, height, KnownResamplers.Triangle));
        fax.Metadata.SetResolution(204, 98, PixelResolutionUnit.PixelsPerInch);
        return fax;
    }

    /// <summary>The page rotated clockwise by <paramref name="degrees"/> onto a white, enlarged canvas (what <see cref="PreprocessingOptions.Deskew"/> targets).</summary>
    public static Image<Rgb24> Tilted(Image<Rgb24> page, float degrees)
        => page.Clone(c => c.Rotate(degrees, KnownResamplers.Bicubic, Color.White));

    /// <summary>The page cropped below its last inked row (plus <paramref name="margin"/>), dropping the blank lower part.</summary>
    public static Image<Rgb24> CropToContent(Image<Rgb24> page, int margin = 24)
    {
        int last = 0;
        page.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    if (row[x].R < 128) { last = y; break; }
                }
            }
        });
        int height = Math.Clamp(last + margin, 1, page.Height);
        return page.Clone(c => c.Crop(new Rectangle(0, 0, page.Width, height)));
    }

    /// <summary>Stacks pieces top to bottom on a white page as wide as the widest piece (a long receipt).</summary>
    public static Image<Rgb24> StackVertically(IReadOnlyList<Image<Rgb24>> pieces)
    {
        int width = pieces.Max(p => p.Width);
        int height = pieces.Sum(p => p.Height);
        var tall = new Image<Rgb24>(width, height, new Rgb24(255, 255, 255));
        int offset = 0;
        foreach (var piece in pieces)
        {
            int top = offset;
            piece.ProcessPixelRows(tall, (src, dst) =>
            {
                for (int y = 0; y < src.Height; y++)
                {
                    src.GetRowSpan(y).CopyTo(dst.GetRowSpan(top + y));
                }
            });
            offset += piece.Height;
        }
        return tall;
    }

    /// <summary>Places pieces left to right on a white page as tall as the tallest piece (a wide banner or drawing).</summary>
    public static Image<Rgb24> StackHorizontally(IReadOnlyList<Image<Rgb24>> pieces)
    {
        int width = pieces.Sum(p => p.Width);
        int height = pieces.Max(p => p.Height);
        var wide = new Image<Rgb24>(width, height, new Rgb24(255, 255, 255));
        int offset = 0;
        foreach (var piece in pieces)
        {
            int left = offset;
            piece.ProcessPixelRows(wide, (src, dst) =>
            {
                for (int y = 0; y < src.Height; y++)
                {
                    src.GetRowSpan(y).CopyTo(dst.GetRowSpan(y).Slice(left));
                }
            });
            offset += piece.Width;
        }
        return wide;
    }

    /// <summary>
    /// Multiset token recall of <paramref name="hypothesis"/> against <paramref name="reference"/>: the share of the
    /// reference's words (case-folded, split at anything that is not a letter or digit, 3+ characters) that the
    /// hypothesis also contains. Order-insensitive, so a different reading order does not count as an error.
    /// </summary>
    public static double TokenRecall(string reference, string hypothesis)
    {
        var want = Tokens(reference);
        if (want.Count == 0) return 1;
        var have = Tokens(hypothesis);
        int hit = 0, total = 0;
        foreach (var (token, count) in want)
        {
            total += count;
            hit += Math.Min(count, have.GetValueOrDefault(token));
        }
        return (double)hit / total;
    }

    /// <summary>Whitespace-insensitive, case-insensitive containment (the corpus benchmark's string check).</summary>
    public static bool Has(string text, string needle)
        => Strip(text).Contains(Strip(needle), StringComparison.OrdinalIgnoreCase);

    /// <summary>The known strings of the corpus pages (see <c>OcrDatasetBenchmarkTests</c>).</summary>
    public static readonly string[] KnownStrings =
    {
        "OCR TEST", "quick brown fox", "test.user@example.com", "PaddleOCRNet", "abcdefghijklmnopqrstuvwxyz", "0123456789",
    };

    /// <summary>How many <see cref="KnownStrings"/> the reference contains and the hypothesis also recovers.</summary>
    public static (int Recovered, int Present) KnownStringHits(string reference, string hypothesis)
    {
        int present = 0, recovered = 0;
        foreach (var s in KnownStrings)
        {
            if (!Has(reference, s)) continue;
            present++;
            if (Has(hypothesis, s)) recovered++;
        }
        return (recovered, present);
    }

    /// <summary>Whether two results carry identical text, confidences and geometry, line by line.</summary>
    public static bool SameLines(OcrResult a, OcrResult b)
    {
        if (a.FullText != b.FullText || a.Lines.Count != b.Lines.Count) return false;
        for (int i = 0; i < a.Lines.Count; i++)
        {
            var x = a.Lines[i];
            var y = b.Lines[i];
            if (x.Text != y.Text || x.Confidence != y.Confidence || x.BoundingBox != y.BoundingBox) return false;
            if (!x.BoundingPolygon.SequenceEqual(y.BoundingPolygon)) return false;
        }
        return true;
    }

    /// <summary>Mean line confidence (0 for an empty result).</summary>
    public static double MeanConfidence(OcrResult result)
        => result.Lines.Count == 0 ? 0 : result.Lines.Average(l => l.Confidence);

    /// <summary>True when every line box lies inside the image (with a small slack for rounding).</summary>
    public static bool BoxesInside(OcrResult result, int width, int height, double slack = 0.5)
        => result.Lines.All(l => l.BoundingBox.MinX >= -slack && l.BoundingBox.MinY >= -slack
                              && l.BoundingBox.MaxX <= width + slack && l.BoundingBox.MaxY <= height + slack);

    private static Dictionary<string, int> Tokens(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length >= 3)
            {
                var token = sb.ToString();
                counts[token] = counts.GetValueOrDefault(token) + 1;
            }
            sb.Clear();
        }

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else Flush();
        }
        Flush();
        return counts;
    }

    private static string Strip(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
}
