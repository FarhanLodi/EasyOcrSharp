using EasyOcrSharp.Models;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Ports EasyOCR's <c>group_text_box</c> (horizontal path): groups raw CRAFT detection
/// boxes into reading-order lines, merges adjacent boxes on the same line into a single
/// region, adds a small margin, and sorts top-to-bottom. This makes the engine's output
/// match EasyOCR's <c>readtext()</c> structure (one box per text line, not per word). The
/// merge thresholds are exposed via <see cref="GroupingOptions"/>.
/// </summary>
/// <remarks>
/// Grouping happens <em>before</em> recognition: a merged run of detection boxes is rectified and decoded
/// as one patch, so a merged line's CTC alignment already covers all of its words in reading order and
/// <see cref="OcrLine.Words"/> spans the whole line without any post-hoc concatenation. The one place
/// that does need stitching is paragraph merging, which happens after recognition — see
/// <c>OnnxEasyOcrEngine.ReattachWords</c>.
/// </remarks>
internal static class TextBoxGrouper
{
    /// <summary>
    /// The share of the shorter box's height two boxes must overlap vertically for the tilted-line rule to
    /// join them.
    /// </summary>
    internal const double MinTiltedLineOverlap = 0.25;

    public static IReadOnlyList<OcrPoint[]> Group(
        IReadOnlyList<OcrPoint[]> polygons,
        int imageWidth,
        int imageHeight,
        GroupingOptions? grouping = null)
    {
        grouping ??= GroupingOptions.Default;
        double ycenterThreshold = grouping.YCenterThreshold;
        double heightThreshold = grouping.HeightThreshold;
        double widthThreshold = grouping.WidthThreshold;
        double addMargin = grouping.AddMargin;
        double slopeThreshold = grouping.SlopeThreshold;

        if (polygons.Count == 0) return polygons;

        // Reduce each quad to an axis-aligned box: [xMin, xMax, yMin, yMax, xCenter, yCenter, height].
        var boxes = new List<Box>(polygons.Count);
        foreach (var poly in polygons)
        {
            double xMin = poly.Min(p => p.X), xMax = poly.Max(p => p.X);
            double yMin = poly.Min(p => p.Y), yMax = poly.Max(p => p.Y);
            boxes.Add(new Box(xMin, xMax, yMin, yMax, 0.5 * (xMin + xMax), 0.5 * (yMin + yMax), yMax - yMin));
        }

        // Sort by vertical center, then split into lines by comparable y-center.
        boxes.Sort((a, b) => a.YCenter.CompareTo(b.YCenter));

        var lines = new List<List<Box>>();
        var current = new List<Box>();
        var bHeights = new List<double>();
        var bYCenters = new List<double>();
        foreach (var box in boxes)
        {
            if (current.Count == 0)
            {
                current.Add(box);
                bHeights.Add(box.Height);
                bYCenters.Add(box.YCenter);
                continue;
            }

            double meanHeight = Mean(bHeights);
            bool sameLine = Math.Abs(Mean(bYCenters) - box.YCenter) < ycenterThreshold * meanHeight;

            // Tilted lines (EasyOCR's slope_ths): a box that fails the vertical-centre test can still join the
            // line when it lies on the line's own gently sloped course. Clean horizontal text is never split.
            if (!sameLine)
            {
                sameLine = TryFitLine(current, meanHeight, slopeThreshold, out double intercept, out double slope)
                    ? Math.Abs(box.YCenter - (intercept + slope * box.XCenter)) < ycenterThreshold * meanHeight
                    : JoinsTiltedNeighbour(box, current[^1], meanHeight, slopeThreshold);
            }

            if (sameLine)
            {
                current.Add(box);
                bHeights.Add(box.Height);
                bYCenters.Add(box.YCenter);
            }
            else
            {
                lines.Add(current);
                current = new List<Box> { box };
                bHeights = new List<double> { box.Height };
                bYCenters = new List<double> { box.YCenter };
            }
        }
        lines.Add(current);

        var merged = new List<OcrPoint[]>();
        foreach (var line in lines)
        {
            if (line.Count == 1)
            {
                merged.Add(WithMargin(line[0], addMargin, imageWidth, imageHeight));
                continue;
            }

            // Left-to-right; merge runs of adjacent boxes with comparable height and a small gap.
            line.Sort((a, b) => a.XMin.CompareTo(b.XMin));
            var run = new List<Box>();
            var runHeights = new List<double>();
            double runXMax = 0;
            foreach (var box in line)
            {
                if (run.Count == 0)
                {
                    run.Add(box);
                    runHeights.Add(box.Height);
                    runXMax = box.XMax;
                }
                else if (Math.Abs(Mean(runHeights) - box.Height) < heightThreshold * Mean(runHeights)
                         && (box.XMin - runXMax) < widthThreshold * box.Height)
                {
                    run.Add(box);
                    runHeights.Add(box.Height);
                    runXMax = box.XMax;
                }
                else
                {
                    merged.Add(MergeRun(run, addMargin, imageWidth, imageHeight));
                    run = new List<Box> { box };
                    runHeights = new List<double> { box.Height };
                    runXMax = box.XMax;
                }
            }
            if (run.Count > 0) merged.Add(MergeRun(run, addMargin, imageWidth, imageHeight));
        }

        return merged;
    }

    /// <summary>
    /// Least-squares course <c>y = intercept + slope·x</c> through the centres of the boxes on a line, with the
    /// slope clamped to ±<paramref name="slopeThreshold"/>. False when the boxes span less than one line height
    /// horizontally, which leaves no course to fit.
    /// </summary>
    /// <remarks>
    /// The course is what a tilted line's next word is tested against. Testing against the previous box in
    /// y-centre order instead — what this used to do — compares a box with whichever word of the line sits
    /// lowest, often at the far end of the page, and joined consecutive lines whenever they were less than one
    /// line height apart: on a wide page the first word of the next line chained on ("=2" at 0.09 confidence on
    /// the benchmark's plain-text pages, fixed in 3.1.1 by bounding the drop), in tightly set text the last
    /// word of the next line did (Monthly_Report_With_Image.pdf: lines 33 px apart with 31 px boxes), and on a
    /// page rotated by 1.5° the next line's right end overlapped this line's left end
    /// (005_plain_text.png). In every case the line's course passes a full line spacing away from the
    /// intruding box.
    /// </remarks>
    private static bool TryFitLine(List<Box> line, double meanHeight, double slopeThreshold, out double intercept, out double slope)
    {
        intercept = slope = 0;
        if (line.Count < 2) return false;

        double meanX = 0, meanY = 0;
        foreach (var b in line)
        {
            meanX += b.XCenter;
            meanY += b.YCenter;
        }
        meanX /= line.Count;
        meanY /= line.Count;

        double sxx = 0, sxy = 0;
        foreach (var b in line)
        {
            double dx = b.XCenter - meanX;
            sxx += dx * dx;
            sxy += dx * (b.YCenter - meanY);
        }
        if (sxx / line.Count < meanHeight * meanHeight) return false;

        slope = Math.Clamp(sxy / sxx, -slopeThreshold, slopeThreshold);
        intercept = meanY - slope * meanX;
        return true;
    }

    /// <summary>
    /// The tilted-line test for a line too short to fit a course: <paramref name="box"/> joins when it is
    /// horizontally separated from the line's last box, the connecting slope is gentle, it is less than one
    /// line height lower, and the two still share at least <see cref="MinTiltedLineOverlap"/> of the shorter
    /// box's height — neighbouring words of a tilted line do (20 px of drift on 30 px boxes leaves a third),
    /// words on consecutive lines share at most a sliver of ascender or descender.
    /// </summary>
    private static bool JoinsTiltedNeighbour(Box box, Box last, double meanHeight, double slopeThreshold)
    {
        double dx = Math.Abs(box.XCenter - last.XCenter);
        double dy = Math.Abs(box.YCenter - last.YCenter);
        double overlap = Math.Min(box.YMax, last.YMax) - Math.Max(box.YMin, last.YMin);
        return dx > meanHeight && dy < meanHeight && dy <= slopeThreshold * dx
            && overlap >= MinTiltedLineOverlap * Math.Min(box.Height, last.Height);
    }

    private static OcrPoint[] MergeRun(List<Box> run, double addMargin, int imgW, int imgH)
    {
        double xMin = run.Min(b => b.XMin);
        double xMax = run.Max(b => b.XMax);
        double yMin = run.Min(b => b.YMin);
        double yMax = run.Max(b => b.YMax);

        double meanHeight = run.Average(b => b.Height);
        if (TryFitLine(run, meanHeight, MaxSlantedRunSlope, out _, out double slope)
            && Math.Abs(slope) * (xMax - xMin) > MinSlantedRunDrift * meanHeight)
        {
            return SlantedRun(run, slope, xMin, xMax, addMargin, imgW, imgH);
        }

        return WithMargin(new Box(xMin, xMax, yMin, yMax, 0, 0, yMax - yMin), addMargin, imgW, imgH);
    }

    /// <summary>
    /// How much a run's fitted course must drift across its width, as a share of its mean box height, before
    /// it is cut along the course (<see cref="SlantedRun"/>) instead of as an axis-aligned rectangle — the
    /// point at which the rectangle is twice the text's height and reaches into the neighbouring lines. Below
    /// it the plain crop is kept: rectifying resamples the strip, and on pages skewed by under a degree (the
    /// synthetic corpus is at about 0.8°) that alone turned some commas into semicolons.
    /// </summary>
    internal const double MinSlantedRunDrift = 1.0;

    /// <summary>The steepest course a slanted run follows — EasyOCR's horizontal-box limit (<c>slope_ths</c>).</summary>
    private const double MaxSlantedRunSlope = 0.1;

    /// <summary>
    /// The region for a run of boxes on a tilted line: a parallelogram whose top and bottom edges follow the
    /// run's course <c>y = c + slope·x</c> and enclose every box, with the usual margin. An axis-aligned
    /// rectangle around a long tilted line grows by <c>|slope|·width</c> — 35 px on a 1300 px line tilted
    /// 1.5°, against 30 px text — and takes in the ends of the lines above and below, which the recognizer
    /// reads as noise (005_plain_text.png's dense block). The recognizer rectifies the parallelogram into a
    /// straight strip.
    /// </summary>
    private static OcrPoint[] SlantedRun(List<Box> run, double slope, double xMin, double xMax, double addMargin, int imgW, int imgH)
    {
        double top = double.PositiveInfinity, bottom = double.NegativeInfinity;
        foreach (var b in run)
        {
            // The corner offsets from the course; a box's extreme corners depend on the sign of the slope.
            double hi = Math.Min(b.YMin - slope * b.XMin, b.YMin - slope * b.XMax);
            double lo = Math.Max(b.YMax - slope * b.XMin, b.YMax - slope * b.XMax);
            top = Math.Min(top, hi);
            bottom = Math.Max(bottom, lo);
        }

        int margin = (int)(addMargin * Math.Min(xMax - xMin, bottom - top));
        double x0 = Math.Max(0, xMin - margin), x1 = Math.Min(imgW, xMax + margin);
        top -= margin;
        bottom += margin;

        OcrPoint At(double x, double offset) => new(x, Math.Clamp(offset + slope * x, 0, imgH));
        return new[] { At(x0, top), At(x1, top), At(x1, bottom), At(x0, bottom) };
    }

    private static OcrPoint[] WithMargin(Box b, double addMargin, int imgW, int imgH)
    {
        double width = b.XMax - b.XMin;
        double height = b.YMax - b.YMin;
        int margin = (int)(addMargin * Math.Min(width, height));

        double x0 = Math.Max(0, b.XMin - margin);
        double y0 = Math.Max(0, b.YMin - margin);
        double x1 = Math.Min(imgW, b.XMax + margin);
        double y1 = Math.Min(imgH, b.YMax + margin);

        return new[]
        {
            new OcrPoint(x0, y0),
            new OcrPoint(x1, y0),
            new OcrPoint(x1, y1),
            new OcrPoint(x0, y1),
        };
    }

    private static double Mean(List<double> values)
    {
        double sum = 0;
        foreach (var v in values) sum += v;
        return values.Count > 0 ? sum / values.Count : 0;
    }

    private readonly record struct Box(double XMin, double XMax, double YMin, double YMax, double XCenter, double YCenter, double Height);
}
