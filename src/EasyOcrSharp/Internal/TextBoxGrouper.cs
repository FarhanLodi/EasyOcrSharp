using EasyOcrSharp.Models;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Ports EasyOCR's <c>group_text_box</c> (horizontal path): groups raw CRAFT detection
/// boxes into reading-order lines, merges adjacent boxes on the same line into a single
/// region, adds a small margin, and sorts top-to-bottom. This makes the engine's output
/// match EasyOCR's <c>readtext()</c> structure (one box per text line, not per word).
/// </summary>
internal static class TextBoxGrouper
{
    public static IReadOnlyList<OcrPoint[]> Group(
        IReadOnlyList<OcrPoint[]> polygons,
        int imageWidth,
        int imageHeight,
        double ycenterThreshold = 0.5,
        double heightThreshold = 0.5,
        double widthThreshold = 1.0,
        double addMargin = 0.05)
    {
        if (polygons.Count == 0) return polygons;

        // Reduce each quad to an axis-aligned box: [xMin, xMax, yMin, yMax, yCenter, height].
        var boxes = new List<Box>(polygons.Count);
        foreach (var poly in polygons)
        {
            double xMin = poly.Min(p => p.X), xMax = poly.Max(p => p.X);
            double yMin = poly.Min(p => p.Y), yMax = poly.Max(p => p.Y);
            boxes.Add(new Box(xMin, xMax, yMin, yMax, 0.5 * (yMin + yMax), yMax - yMin));
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
            }
            else if (Math.Abs(Mean(bYCenters) - box.YCenter) < ycenterThreshold * Mean(bHeights))
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

    private static OcrPoint[] MergeRun(List<Box> run, double addMargin, int imgW, int imgH)
    {
        double xMin = run.Min(b => b.XMin);
        double xMax = run.Max(b => b.XMax);
        double yMin = run.Min(b => b.YMin);
        double yMax = run.Max(b => b.YMax);
        return WithMargin(new Box(xMin, xMax, yMin, yMax, 0, yMax - yMin), addMargin, imgW, imgH);
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

    private readonly record struct Box(double XMin, double XMax, double YMin, double YMax, double YCenter, double Height);
}
