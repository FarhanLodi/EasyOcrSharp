using System.Text;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Extraction;

/// <summary>
/// Options for <see cref="LayoutTextExtensions.ToLayoutText(OcrResult, LayoutTextOptions?)"/>.
/// </summary>
public sealed record LayoutTextOptions
{
    /// <summary>
    /// Maximum number of blank lines inserted for a vertical gap between rows (one blank line per full
    /// median line height of empty space). Default 1; 0 never inserts blank lines.
    /// </summary>
    public int MaxBlankLines { get; init; } = 1;

    /// <summary>
    /// Width in pixels of one character cell. <see langword="null"/> (the default) estimates it as the
    /// median of each line's width divided by its display width.
    /// </summary>
    public double? CharacterWidth { get; init; }

    /// <summary>
    /// Fraction (0–1] of the smaller line height two lines must overlap vertically to share a row. Default
    /// 0.5, which tolerates slight skew.
    /// </summary>
    public double RowOverlapThreshold { get; init; } = 0.5;

    /// <summary>
    /// Anchor predominantly right-to-left (Arabic, Hebrew) lines at the right edge of their box, so
    /// right-to-left paragraphs stay right-aligned. Default <see langword="true"/>.
    /// </summary>
    public bool RightAlignRtlLines { get; init; } = true;

    /// <summary>The default options.</summary>
    public static LayoutTextOptions Default { get; } = new();

    internal void Validate()
    {
        if (MaxBlankLines < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxBlankLines), MaxBlankLines, "MaxBlankLines must not be negative.");
        if (CharacterWidth is { } width && (!double.IsFinite(width) || width <= 0))
            throw new ArgumentOutOfRangeException(nameof(CharacterWidth), width, "CharacterWidth must be a positive number.");
        if (double.IsNaN(RowOverlapThreshold) || RowOverlapThreshold <= 0 || RowOverlapThreshold > 1)
            throw new ArgumentOutOfRangeException(nameof(RowOverlapThreshold), RowOverlapThreshold, "RowOverlapThreshold must be in (0, 1].");
    }
}

/// <summary>
/// Renders an <see cref="OcrResult"/> as monospaced text that keeps the page's spatial layout, in the
/// spirit of <c>pdftotext -layout</c>: columns stay side by side and receipt prices stay to the right of
/// their items. Useful for receipts, forms and LLM prompts, where <see cref="OcrResult.FullText"/>'s one
/// line per detection loses the table shape.
/// </summary>
public static class LayoutTextExtensions
{
    /// <summary>
    /// Renders the result as layout-preserving plain text.
    /// <para>
    /// A character cell width is estimated (median of line width ÷ display width, where CJK and other
    /// full-width characters count as two columns). Lines are grouped into rows by vertical overlap, each
    /// line is placed at column <c>round(x ÷ cell)</c> (right-to-left lines are anchored at their right
    /// edge), the common left margin is removed, and vertical gaps become up to
    /// <see cref="LayoutTextOptions.MaxBlankLines"/> blank lines. Lines in a row never overlap: a line that
    /// would collide is pushed right to leave one space.
    /// </para>
    /// </summary>
    /// <param name="result">The OCR result.</param>
    /// <param name="options">Rendering options; <see langword="null"/> uses <see cref="LayoutTextOptions.Default"/>.</param>
    /// <returns>
    /// The rendered text with <c>\n</c> line breaks and no trailing spaces; empty for no text. Lines without
    /// any geometry are emitted one per row in their given order.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option value is out of range.</exception>
    public static string ToLayoutText(this OcrResult result, LayoutTextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        options ??= LayoutTextOptions.Default;
        options.Validate();

        var texts = new List<string>(result.Lines.Count);
        var boxes = new List<OcrBoundingBox>(result.Lines.Count);
        var widths = new List<int>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            // Paragraph grouping joins lines with '\n'; inside a layout row that would break the grid.
            string text = (line.Text ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (text.Length == 0) continue;
            texts.Add(text);
            boxes.Add(TextGeometry.BoxOf(line));
            widths.Add(TextGeometry.DisplayWidth(text));
        }

        if (texts.Count == 0) return string.Empty;
        if (boxes.All(b => b.IsEmpty)) return string.Join('\n', texts);

        double cell = options.CharacterWidth ?? EstimateCellWidth(boxes, widths);
        var heights = boxes.Where(b => b.Height > 0).Select(b => b.Height).ToList();
        double lineHeight = heights.Count > 0 ? TextGeometry.Median(heights) : 0;

        var columns = new int[texts.Count];
        for (int i = 0; i < texts.Count; i++)
        {
            bool anchorRight = options.RightAlignRtlLines && TextGeometry.IsPredominantlyRtl(texts[i]);
            columns[i] = anchorRight
                ? (int)Math.Round(boxes[i].MaxX / cell) - widths[i]
                : (int)Math.Round(boxes[i].MinX / cell);
        }
        int origin = columns.Min();

        var output = new StringBuilder();
        var row = new StringBuilder();
        double previousBottom = 0;
        var rows = TextGeometry.GroupRows(boxes, options.RowOverlapThreshold);
        for (int r = 0; r < rows.Count; r++)
        {
            var members = rows[r];
            double top = members.Min(i => boxes[i].MinY);
            double bottom = members.Max(i => boxes[i].MaxY);

            if (r > 0)
            {
                output.Append('\n');
                if (options.MaxBlankLines > 0 && lineHeight > 0)
                {
                    int blanks = (int)Math.Floor((top - previousBottom) / lineHeight);
                    output.Append('\n', Math.Clamp(blanks, 0, options.MaxBlankLines));
                }
            }

            row.Clear();
            int cursor = 0;
            foreach (int i in members.OrderBy(i => columns[i]).ThenBy(i => boxes[i].MinX))
            {
                int column = Math.Max(0, columns[i] - origin);
                if (row.Length > 0) column = Math.Max(column, cursor + 1);
                row.Append(' ', column - cursor).Append(texts[i]);
                cursor = column + widths[i];
            }

            output.Append(row.ToString().TrimEnd());
            previousBottom = Math.Max(previousBottom, bottom);
        }

        return output.ToString();
    }

    private static double EstimateCellWidth(List<OcrBoundingBox> boxes, List<int> widths)
    {
        var samples = new List<double>(boxes.Count);
        for (int i = 0; i < boxes.Count; i++)
        {
            if (widths[i] > 0 && boxes[i].Width > 0) samples.Add(boxes[i].Width / widths[i]);
        }

        double median = TextGeometry.Median(samples);
        return double.IsFinite(median) && median > 0 ? median : 1.0;
    }
}
