namespace EasyOcrSharp.Models;

/// <summary>
/// Tuning for how raw CRAFT detection boxes are merged into reading-order lines, and how lines are
/// merged into paragraphs — a port of EasyOCR's box-merge parameters (<c>group_text_box</c> /
/// <c>get_paragraph</c>). The defaults reproduce EasyOcrSharp's existing behaviour; only change
/// these when text lines are over- or under-merged. EasyOCR's stock value is noted on each property
/// where it differs.
/// </summary>
public sealed record GroupingOptions
{
    /// <summary>
    /// Max slope (rise/run) of the line connecting two boxes for them to still count as the same
    /// (gently sloped) line. Applied additively — it only ever <i>joins</i> boxes the vertical-centre
    /// test would otherwise split, so raising it tolerates more tilt. EasyOCR's <c>slope_ths</c>.
    /// Default 0.1.
    /// </summary>
    public double SlopeThreshold { get; init; } = 0.1;

    /// <summary>
    /// Vertical-centre tolerance (as a fraction of the running mean box height) for grouping boxes
    /// onto one line. EasyOCR's <c>ycenter_ths</c>. Default 0.5.
    /// </summary>
    public double YCenterThreshold { get; init; } = 0.5;

    /// <summary>
    /// Height-similarity tolerance for merging neighbouring boxes on a line. EasyOCR's
    /// <c>height_ths</c>. Default 0.5.
    /// </summary>
    public double HeightThreshold { get; init; } = 0.5;

    /// <summary>
    /// Max horizontal gap (as a multiple of box height) between two boxes merged on the same line.
    /// EasyOCR's <c>width_ths</c> (stock default 0.5). Default 1.0.
    /// </summary>
    public double WidthThreshold { get; init; } = 1.0;

    /// <summary>
    /// Margin added around each merged box, as a fraction of its shorter side. EasyOCR's
    /// <c>add_margin</c> (stock default 0.1). Default 0.05.
    /// </summary>
    public double AddMargin { get; init; } = 0.05;

    /// <summary>
    /// Horizontal join distance for paragraph grouping, as a multiple of line height: lines whose
    /// horizontal gap is within this are placed in the same paragraph. EasyOCR's <c>x_ths</c>.
    /// Default 1.0. Only used when <see cref="TextGrouping.Paragraph"/> is selected.
    /// </summary>
    public double ParagraphXThreshold { get; init; } = 1.0;

    /// <summary>
    /// Vertical join distance for paragraph grouping, as a multiple of line height. EasyOCR's
    /// <c>y_ths</c> (stock default 0.5). Default 1.0. Only used when
    /// <see cref="TextGrouping.Paragraph"/> is selected.
    /// </summary>
    public double ParagraphYThreshold { get; init; } = 1.0;

    /// <summary>The default grouping thresholds.</summary>
    public static GroupingOptions Default { get; } = new();
}
