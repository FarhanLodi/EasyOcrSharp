using System.Diagnostics;
using System.Globalization;
using System.Text;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyOcrSharp.Evaluation;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using Xunit;
using Xunit.Abstractions;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Evaluation harness (not a regression gate) for EasyImageSharp 1.2.0's document preprocessing profiles —
/// <see cref="DocumentPreprocessingProfile.NeuralOcr"/>, <see cref="DocumentPreprocessingProfile.ScannedDocument"/>,
/// <see cref="DocumentPreprocessingProfile.PhoneDocument"/> and <see cref="DocumentPreprocessingProfile.Screenshot"/>
/// — in front of the CRAFT + CRNN pipeline. It answers one question: does a profile measurably help OCR on the
/// input type it targets? Only a yes would justify exposing it (opt-in) on <see cref="PreprocessingOptions"/>;
/// nothing here adds public API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Inputs.</b> <c>test/assets</c> pages (the 100-page synthetic corpus plus <c>sample.png</c>), each OCR'd as
/// rendered (<c>clean</c>) and after deterministic, seeded degradations that imitate each profile's target:
/// <c>phone</c> (page on a dark background under a perspective transform, uneven illumination and a slight
/// blur), <c>screenshot</c> (downscaled, small anti-aliased text with a screen tint), <c>scan</c> (low contrast,
/// sensor noise and a 2.5° skew on grey paper) and <c>scan-rot90</c> (the scan turned a quarter).
/// </para>
/// <para>
/// <b>Configurations.</b> No preprocessing; each profile with its defaults, run through
/// <see cref="DocumentProcessingExtensions.PreprocessDocument{TPixel}"/> with the recognized boxes mapped back to
/// the input's coordinates through the profile's <see cref="IDocumentCoordinateMap"/>; and the existing
/// <see cref="PreprocessingOptions.Deskew"/> + <see cref="PreprocessingOptions.Binarize"/> as a reference point.
/// A profile run that applied no stage produced an unchanged clone, so it reuses the no-preprocessing result.
/// </para>
/// <para>
/// <b>Metrics</b> per configuration and input type: CER (relaxed: case, whitespace and punctuation folded)
/// against the ground truth where one exists (<c>sample.gt.txt</c>) and otherwise against the clean page's own
/// unpreprocessed transcription (pseudo ground truth, so <c>clean / none</c> is 0 by construction); bag-of-words
/// recall and precision against the same reference (order-insensitive, so a reading-order change after a
/// geometric correction is not scored as recognition loss); lines found; mean line confidence; the share of
/// mapped-back polygon points the coordinate map could map; the mean best IoU of mapped-back boxes against the
/// unpreprocessed run's boxes on the same input (a check that the map really lands boxes on the input); and
/// preprocessing time. The table goes to the test output and to <c>document-profile-evaluation.md</c> /
/// <c>.csv</c> in <c>EASYOCRSHARP_PROFILE_EVAL_OUT</c> (default: the test output directory).
/// </para>
/// <para>
/// <b>Opt-in.</b> Skipped unless <c>EASYOCRSHARP_RUN_PROFILE_EVAL=1</c>, so ordinary integration runs are not
/// lengthened. By default it uses <c>sample.png</c> plus the first page of five categories (plain text, tables,
/// receipts, low quality, rotated/perspective); <c>EASYOCRSHARP_PROFILE_EVAL_PER_CATEGORY=N</c> instead takes
/// the first N pages of every corpus category. Needs the CRAFT and <c>latin_g2</c> models.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(OcrIntegrationCollection.Name)]
public sealed class DocumentProfileEvaluationTests
{
    private const string EnableVariable = "EASYOCRSHARP_RUN_PROFILE_EVAL";
    private const string PerCategoryVariable = "EASYOCRSHARP_PROFILE_EVAL_PER_CATEGORY";
    private const string OutputVariable = "EASYOCRSHARP_PROFILE_EVAL_OUT";

    private static readonly string[] DefaultCategories =
        ["plain_text", "tables", "receipts_invoices", "low_quality", "rotated_perspective"];

    private static readonly string[] Variants = ["clean", "phone", "screenshot", "scan", "scan-rot90"];

    private const string NoneConfig = "none";
    private const string LegacyConfig = "Deskew+Binarize";

    private static readonly (string Name, DocumentPreprocessingProfile Profile)[] Profiles =
    [
        ("NeuralOcr", DocumentPreprocessingProfile.NeuralOcr),
        ("ScannedDocument", DocumentPreprocessingProfile.ScannedDocument),
        ("PhoneDocument", DocumentPreprocessingProfile.PhoneDocument),
        ("Screenshot", DocumentPreprocessingProfile.Screenshot),
    ];

    private static readonly string[] Languages = ["en"];

    private readonly ITestOutputHelper _out;

    public DocumentProfileEvaluationTests(ITestOutputHelper output) => _out = output;

    /// <summary>One OCR run: an input page, a degradation, a preprocessing configuration and what came out.</summary>
    private sealed record Run(
        string Image,
        string Variant,
        string Config,
        string Category,
        bool HasGroundTruth,
        int Lines,
        double MeanConfidence,
        double Cer,
        double WordRecall,
        double WordPrecision,
        double MappedPointShare,
        double MappedBoxIoU,
        double PreprocessMs,
        string StagesApplied,
        bool ReusedBaseline);

    [SkippableFact]
    public async Task Profiles_are_measured_on_their_target_input_types()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable(EnableVariable) == "1",
            $"Evaluation harness is opt-in: set {EnableVariable}=1 (needs the CRAFT and latin_g2 models).");

        var pages = SelectPages();
        Skip.If(pages.Count == 0, "No test/assets pages found next to the test assembly.");

        await using var ocr = new EasyOcrService(new EasyOcrServiceOptions { ExecutionProvider = OcrExecutionProvider.Cpu });
        var runs = new List<Run>();

        foreach (var (path, category) in pages)
        {
            string name = Path.GetFileName(path);
            string? groundTruth = GroundTruthFor(path);
            // Decoded exactly as the service decodes a file (EXIF orientation, transparency flattened).
            using var clean = DecodeLimits.Load(await File.ReadAllBytesAsync(path), 0, "evaluation");
            string? pseudoReference = null;

            foreach (var variant in Variants)
            {
                using var input = Degrade(clean, variant, seed: StableSeed(name, variant));

                // Baseline on this input: no preprocessing at all.
                var baseline = await ocr.ExtractTextFromImage(input, Languages);
                if (variant == "clean") pseudoReference = baseline.FullText;
                string reference = groundTruth ?? pseudoReference ?? baseline.FullText;
                var baselineBoxes = baseline.Lines.Select(l => l.BoundingBox).ToArray();

                runs.Add(Score(name, variant, NoneConfig, category, groundTruth is not null, baseline.Lines,
                    reference, mappedPointShare: 1, mappedBoxIoU: 1, preprocessMs: 0, stages: "-", reused: false));

                // Each profile, boxes mapped back to the input through the profile's coordinate map.
                foreach (var (profileName, profile) in Profiles)
                {
                    var sw = Stopwatch.StartNew();
                    using var processed = input.PreprocessDocument(new DocumentPreprocessingOptions { Profile = profile });
                    sw.Stop();
                    var page = processed.Pages[0];
                    var applied = page.Stages.Where(s => s.Applied && s.Name != "Analyze").Select(s => s.Name).ToArray();
                    string stages = applied.Length == 0 ? "-" : string.Join('+', applied);

                    if (applied.Length == 0)
                    {
                        // Nothing applied: the output is an unchanged clone, so OCR would repeat the baseline.
                        runs.Add(Score(name, variant, profileName, category, groundTruth is not null, baseline.Lines,
                            reference, 1, 1, sw.Elapsed.TotalMilliseconds, stages, reused: true));
                        continue;
                    }

                    var result = await ocr.ExtractTextFromImage(processed.Image, Languages);
                    var (mappedLines, mappedShare) = MapToSource(result.Lines, page.Coordinates);
                    double iou = MeanBestIoU(mappedLines.Select(l => l.BoundingBox).ToArray(), baselineBoxes);
                    runs.Add(Score(name, variant, profileName, category, groundTruth is not null, mappedLines,
                        reference, mappedShare, iou, sw.Elapsed.TotalMilliseconds, stages, reused: false));
                }

                // Reference point: the library's existing scan clean-up.
                var legacyOptions = new RecognitionOptions
                {
                    Preprocessing = new PreprocessingOptions { Deskew = true, Binarize = true },
                };
                var legacySw = Stopwatch.StartNew();
                var legacy = await ocr.ExtractTextFromImage(input, Languages, legacyOptions);
                legacySw.Stop();
                runs.Add(Score(name, variant, LegacyConfig, category, groundTruth is not null, legacy.Lines, reference,
                    1, MeanBestIoU(legacy.Lines.Select(l => l.BoundingBox).ToArray(), baselineBoxes),
                    double.NaN, "Deskew+Binarize", reused: false));
            }

            _out.WriteLine($"evaluated {name}");
        }

        string report = BuildReport(runs, pages.Count);
        _out.WriteLine(report);
        WriteOutputs(report, runs);

        // Sanity only: this is an evaluation, not a gate. The pipeline ran everywhere, the clean baseline read
        // text, and every mapped-back coordinate is finite.
        Assert.Equal(pages.Count * Variants.Length * (2 + Profiles.Length), runs.Count);
        Assert.Contains(runs, r => r.Variant == "clean" && r.Config == NoneConfig && r.Lines > 0);
        Assert.All(runs, r => Assert.True(double.IsFinite(r.MappedBoxIoU) || r.Lines == 0, $"{r.Image}/{r.Variant}/{r.Config}"));
    }

    // ------------------------------------------------------------------ selection

    private static List<(string Path, string Category)> SelectPages()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "assets");
        var pages = new List<(string, string)>();
        if (!Directory.Exists(dir)) return pages;

        var sample = Path.Combine(dir, "sample.png");
        if (File.Exists(sample)) pages.Add((sample, "sample"));

        var corpus = Directory.EnumerateFiles(dir, "???_*.png")
            .Where(p => char.IsDigit(Path.GetFileName(p)[0]))
            .OrderBy(p => p, StringComparer.Ordinal)
            .GroupBy(p => Path.GetFileNameWithoutExtension(p)[4..], StringComparer.Ordinal);

        int perCategory = int.TryParse(Environment.GetEnvironmentVariable(PerCategoryVariable), out var n) && n > 0 ? n : 0;
        foreach (var group in corpus)
        {
            if (perCategory > 0)
                pages.AddRange(group.Take(perCategory).Select(p => (p, group.Key)));
            else if (DefaultCategories.Contains(group.Key))
                pages.Add((group.First(), group.Key));
        }

        return pages;
    }

    private static string? GroundTruthFor(string imagePath)
    {
        var gt = Path.Combine(Path.GetDirectoryName(imagePath)!, Path.GetFileNameWithoutExtension(imagePath) + ".gt.txt");
        return File.Exists(gt) ? File.ReadAllText(gt) : null;
    }

    private static int StableSeed(string name, string variant)
    {
        int h = 17;
        foreach (char c in name + "|" + variant) h = unchecked(h * 31 + c);
        return h;
    }

    // ------------------------------------------------------------------ degradations

    /// <summary>Builds the degraded input for <paramref name="variant"/>. Deterministic for a given seed.</summary>
    internal static Image<Rgb24> Degrade(Image<Rgb24> clean, string variant, int seed)
    {
        var rng = new Random(seed);
        switch (variant)
        {
            case "clean":
                return clean.Clone();

            case "phone":
            {
                // The page photographed from an angle on a dark desk: perspective, uneven light, slight blur.
                int w = clean.Width, h = clean.Height;
                int outW = (int)(w * 1.18), outH = (int)(h * 1.18);
                float J(float span) => (float)(rng.NextDouble() * span);
                var quad = new[]
                {
                    new PointF(outW * 0.06f + J(outW * 0.06f), outH * 0.05f + J(outH * 0.04f)),
                    new PointF(outW * 0.94f - J(outW * 0.03f), outH * 0.08f + J(outH * 0.05f)),
                    new PointF(outW * 0.97f - J(outW * 0.04f), outH * 0.95f - J(outH * 0.03f)),
                    new PointF(outW * 0.03f + J(outW * 0.04f), outH * 0.92f - J(outH * 0.04f)),
                };
                var page = new[] { new PointF(0, 0), new PointF(w, 0), new PointF(w, h), new PointF(0, h) };
                var outputToPage = Homography(quad, page);
                var photo = Warp(clean, outputToPage, outW, outH, new Rgb24(78, 70, 62));
                Shade(photo, rng, strength: 0.42f);
                photo.Mutate(c => c.GaussianBlur(0.8f));
                return photo;
            }

            case "screenshot":
            {
                // Small anti-aliased text on a tinted screen background.
                int w = Math.Max(1, (int)(clean.Width * 0.58)), h = Math.Max(1, (int)(clean.Height * 0.58));
                var shot = clean.Clone(c => c.Resize(new ResizeOptions
                {
                    Size = new Size(w, h),
                    Sampler = KnownResamplers.Triangle,
                    Mode = ResizeMode.Stretch,
                }));
                MapPixels(shot, (x, y, p) => new Rgb24((byte)(p.R * 0.94f), (byte)(p.G * 0.97f), p.B));
                return shot;
            }

            case "scan":
            case "scan-rot90":
            {
                // A faint photocopy: compressed tonal range, sensor noise, a 2.5° skew on grey paper.
                var paper = new Rgb24(204, 202, 196);
                float angle = 2.5f * (rng.Next(2) == 0 ? -1 : 1);
                var skewed = Warp(clean, Rotation(clean.Width, clean.Height, angle), clean.Width, clean.Height, new Rgb24(255, 255, 255));
                MapPixels(skewed, (x, y, p) =>
                {
                    float noise = (float)(rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 1.5) * 9f;
                    return new Rgb24(Tone(p.R, paper.R, noise), Tone(p.G, paper.G, noise), Tone(p.B, paper.B, noise));
                });
                if (variant == "scan-rot90") skewed.Mutate(c => c.Rotate(RotateMode.Rotate90));
                return skewed;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(variant), variant, null);
        }
    }

    /// <summary>Compresses a channel into the [118, paper] range of a faint copy and adds noise.</summary>
    private static byte Tone(byte v, byte paperLevel, float noise)
        => (byte)Math.Clamp(118f + (v / 255f) * (paperLevel - 118f) + noise, 0f, 255f);

    /// <summary>Multiplies the image by a smooth diagonal illumination falloff plus a vignette.</summary>
    private static void Shade(Image<Rgb24> image, Random rng, float strength)
    {
        float cx = (float)rng.NextDouble(), cy = (float)rng.NextDouble();
        int w = image.Width, h = image.Height;
        MapPixels(image, (x, y, p) =>
        {
            float u = (float)x / w, v = (float)y / h;
            float diagonal = 1f - strength * Math.Clamp((u + v) / 2f + 0.15f, 0f, 1f);
            float dx = u - cx, dy = v - cy;
            float vignette = 1f - 0.25f * Math.Clamp(dx * dx + dy * dy, 0f, 1f);
            float k = diagonal * vignette;
            return new Rgb24((byte)(p.R * k), (byte)(p.G * k), (byte)(p.B * k));
        });
    }

    private static void MapPixels(Image<Rgb24> image, Func<int, int, Rgb24, Rgb24> map)
    {
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++) row[x] = map(x, y, row[x]);
            }
        });
    }

    /// <summary>
    /// Renders an <paramref name="outW"/>×<paramref name="outH"/> image by sampling <paramref name="source"/>
    /// bilinearly at <paramref name="outputToSource"/>(pixel centre); outside the source is <paramref name="fill"/>.
    /// </summary>
    private static Image<Rgb24> Warp(Image<Rgb24> source, double[] outputToSource, int outW, int outH, Rgb24 fill)
    {
        int sw = source.Width, sh = source.Height;
        var pixels = new Rgb24[sw * sh];
        source.CopyPixelDataTo(pixels);
        var output = new Image<Rgb24>(outW, outH);
        output.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < outH; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < outW; x++)
                {
                    var (sx, sy) = Apply(outputToSource, x + 0.5, y + 0.5);
                    sx -= 0.5;
                    sy -= 0.5;
                    if (sx < -0.5 || sy < -0.5 || sx > sw - 0.5 || sy > sh - 0.5)
                    {
                        row[x] = fill;
                        continue;
                    }

                    int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                    double fx = sx - x0, fy = sy - y0;
                    var p00 = At(x0, y0); var p10 = At(x0 + 1, y0);
                    var p01 = At(x0, y0 + 1); var p11 = At(x0 + 1, y0 + 1);
                    row[x] = new Rgb24(
                        Lerp(p00.R, p10.R, p01.R, p11.R, fx, fy),
                        Lerp(p00.G, p10.G, p01.G, p11.G, fx, fy),
                        Lerp(p00.B, p10.B, p01.B, p11.B, fx, fy));
                }
            }
        });
        return output;

        Rgb24 At(int x, int y) => pixels[Math.Clamp(y, 0, sh - 1) * sw + Math.Clamp(x, 0, sw - 1)];

        static byte Lerp(byte a, byte b, byte c, byte d, double fx, double fy)
        {
            double top = a + (b - a) * fx, bottom = c + (d - c) * fx;
            return (byte)Math.Clamp(Math.Round(top + (bottom - top) * fy), 0, 255);
        }
    }

    /// <summary>Output→source mapping for a rotation by <paramref name="degrees"/> about the image centre.</summary>
    private static double[] Rotation(int width, int height, float degrees)
    {
        double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        double cx = width / 2.0, cy = height / 2.0;
        // source = R(-a) * (out - c) + c
        return [cos, sin, cx - cos * cx - sin * cy, -sin, cos, cy + sin * cx - cos * cy, 0, 0, 1];
    }

    /// <summary>The 3×3 homography (row-major, h33 = 1) taking each <paramref name="from"/> point to the matching <paramref name="to"/> point.</summary>
    private static double[] Homography(PointF[] from, PointF[] to)
    {
        var a = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = from[i].X, y = from[i].Y, u = to[i].X, v = to[i].Y;
            double[] r1 = [x, y, 1, 0, 0, 0, -u * x, -u * y, u];
            double[] r2 = [0, 0, 0, x, y, 1, -v * x, -v * y, v];
            for (int j = 0; j < 9; j++) { a[2 * i, j] = r1[j]; a[2 * i + 1, j] = r2[j]; }
        }

        // Gauss-Jordan with partial pivoting on the 8x8 system.
        for (int col = 0; col < 8; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 8; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            for (int j = 0; j < 9; j++) (a[col, j], a[pivot, j]) = (a[pivot, j], a[col, j]);
            for (int r = 0; r < 8; r++)
            {
                if (r == col) continue;
                double f = a[r, col] / a[col, col];
                for (int j = col; j < 9; j++) a[r, j] -= f * a[col, j];
            }
        }

        var h = new double[9];
        for (int i = 0; i < 8; i++) h[i] = a[i, 8] / a[i, i];
        h[8] = 1;
        return h;
    }

    private static (double X, double Y) Apply(double[] h, double x, double y)
    {
        double w = h[6] * x + h[7] * y + h[8];
        return ((h[0] * x + h[1] * y + h[2]) / w, (h[3] * x + h[4] * y + h[5]) / w);
    }

    // ------------------------------------------------------------------ mapping and scoring

    /// <summary>
    /// Maps every line polygon from the processed image back to the input through the profile's coordinate map.
    /// Returns the mapped lines and the share of polygon points the map could map (unmappable points keep
    /// their processed coordinates and are counted as failures).
    /// </summary>
    private static (IReadOnlyList<OcrLine> Lines, double MappedShare) MapToSource(IReadOnlyList<OcrLine> lines, IDocumentCoordinateMap map)
    {
        int total = 0, mapped = 0;
        var result = new List<OcrLine>(lines.Count);
        foreach (var line in lines)
        {
            var polygon = new OcrPoint[line.BoundingPolygon.Count];
            for (int i = 0; i < polygon.Length; i++)
            {
                var p = line.BoundingPolygon[i];
                total++;
                if (map.TryMapToSource(new PointF((float)p.X, (float)p.Y), out var source)
                    && float.IsFinite(source.X) && float.IsFinite(source.Y))
                {
                    mapped++;
                    polygon[i] = new OcrPoint(source.X, source.Y);
                }
                else
                {
                    polygon[i] = p;
                }
            }

            result.Add(line with { BoundingPolygon = polygon, BoundingBox = OcrBoundingBox.FromPoints(polygon) });
        }

        return (result, total == 0 ? 1 : (double)mapped / total);
    }

    /// <summary>Mean over <paramref name="boxes"/> of the best IoU against any of <paramref name="against"/>.</summary>
    private static double MeanBestIoU(IReadOnlyList<OcrBoundingBox> boxes, IReadOnlyList<OcrBoundingBox> against)
    {
        if (boxes.Count == 0) return against.Count == 0 ? 1 : 0;
        double sum = 0;
        foreach (var b in boxes)
        {
            double best = 0;
            foreach (var a in against)
            {
                double ix = Math.Max(0, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX));
                double iy = Math.Max(0, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));
                double inter = ix * iy;
                double union = a.Width * a.Height + b.Width * b.Height - inter;
                if (union > 0) best = Math.Max(best, inter / union);
            }
            sum += best;
        }
        return sum / boxes.Count;
    }

    private static Run Score(
        string image, string variant, string config, string category, bool hasGroundTruth,
        IReadOnlyList<OcrLine> lines, string reference, double mappedPointShare, double mappedBoxIoU,
        double preprocessMs, string stages, bool reused)
    {
        string hypothesis = string.Join('\n', lines.Select(l => l.Text));
        double cer = reference.Length == 0 && hypothesis.Length == 0
            ? 0
            : TextAccuracyMetrics.CharacterErrorRate(reference, hypothesis, TextComparisonOptions.Relaxed);
        var (recall, precision) = WordOverlap(reference, hypothesis);
        double confidence = lines.Count == 0 ? 0 : lines.Average(l => l.Confidence);
        return new Run(image, variant, config, category, hasGroundTruth, lines.Count, confidence, cer, recall, precision,
            mappedPointShare, mappedBoxIoU, preprocessMs, stages, reused);
    }

    /// <summary>Order-insensitive word recall and precision (multiset intersection, case folded, punctuation trimmed).</summary>
    private static (double Recall, double Precision) WordOverlap(string reference, string hypothesis)
    {
        var refWords = Words(reference);
        var hypWords = Words(hypothesis);
        if (refWords.Count == 0) return (1, hypWords.Count == 0 ? 1 : 0);

        var pool = refWords.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        int hits = 0;
        foreach (var w in hypWords)
        {
            if (pool.TryGetValue(w, out int left) && left > 0)
            {
                pool[w] = left - 1;
                hits++;
            }
        }

        return ((double)hits / refWords.Count, hypWords.Count == 0 ? 0 : (double)hits / hypWords.Count);

        static List<string> Words(string text) => text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim().Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']').ToLowerInvariant())
            .Where(w => w.Length > 0)
            .ToList();
    }

    // ------------------------------------------------------------------ reporting

    private static string BuildReport(IReadOnlyList<Run> runs, int pageCount)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(ci, $"# Document preprocessing profiles — CRAFT + CRNN (en), {pageCount} pages");
        sb.AppendLine();
        sb.AppendLine("Reference: ground truth where present (sample.png), else the clean page's unpreprocessed transcription.");
        sb.AppendLine("ΔCER and ΔRecall are against `none` on the same input type (negative ΔCER / positive ΔRecall = better).");
        sb.AppendLine();
        sb.AppendLine("| Input | Config | Runs | CER | ΔCER | Word recall | ΔRecall | Word precision | Lines | Mean conf | Mapped pts | Box IoU vs none | Stage applied | Prep ms |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var variant in Variants)
        {
            var none = runs.Where(r => r.Variant == variant && r.Config == NoneConfig).ToArray();
            double noneCer = Mean(none, r => r.Cer), noneRecall = Mean(none, r => r.WordRecall);
            foreach (var config in new[] { NoneConfig }.Concat(Profiles.Select(p => p.Name)).Append(LegacyConfig))
            {
                var set = runs.Where(r => r.Variant == variant && r.Config == config).ToArray();
                if (set.Length == 0) continue;
                double cer = Mean(set, r => r.Cer), recall = Mean(set, r => r.WordRecall);
                double appliedShare = (double)set.Count(r => r.StagesApplied != "-") / set.Length;
                double prep = set.All(r => double.IsNaN(r.PreprocessMs)) ? double.NaN : Mean(set, r => r.PreprocessMs);
                sb.AppendLine(ci,
                    $"| {variant} | {config} | {set.Length} | {cer:F4} | {cer - noneCer:+0.0000;-0.0000;0} | {recall:F3} | " +
                    $"{recall - noneRecall:+0.000;-0.000;0} | {Mean(set, r => r.WordPrecision):F3} | {Mean(set, r => r.Lines):F1} | " +
                    $"{Mean(set, r => r.MeanConfidence):F3} | {Mean(set, r => r.MappedPointShare):P0} | {Mean(set, r => r.MappedBoxIoU):F3} | " +
                    $"{appliedShare:P0} | {(double.IsNaN(prep) ? "n/a" : prep.ToString("F0", ci))} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Stages applied per profile and input type");
        sb.AppendLine();
        foreach (var group in runs.Where(r => Profiles.Any(p => p.Name == r.Config))
                     .GroupBy(r => (r.Variant, r.Config)))
        {
            var counts = group.GroupBy(r => r.StagesApplied).Select(g => $"{g.Key}×{g.Count()}");
            sb.AppendLine(ci, $"- {group.Key.Variant} / {group.Key.Config}: {string.Join(", ", counts)}");
        }

        return sb.ToString();

        static double Mean(IEnumerable<Run> set, Func<Run, double> pick)
        {
            var values = set.Select(pick).Where(double.IsFinite).ToArray();
            return values.Length == 0 ? double.NaN : values.Average();
        }
    }

    private void WriteOutputs(string report, IReadOnlyList<Run> runs)
    {
        var dir = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(dir)) dir = AppContext.BaseDirectory;
        Directory.CreateDirectory(dir);

        var ci = CultureInfo.InvariantCulture;
        var csv = new StringBuilder("image,category,variant,config,ground_truth,lines,mean_confidence,cer,word_recall,word_precision,mapped_point_share,mapped_box_iou,preprocess_ms,stages,reused_baseline\n");
        foreach (var r in runs)
        {
            csv.AppendLine(ci,
                $"{r.Image},{r.Category},{r.Variant},{r.Config},{r.HasGroundTruth},{r.Lines},{r.MeanConfidence:F4},{r.Cer:F4}," +
                $"{r.WordRecall:F4},{r.WordPrecision:F4},{r.MappedPointShare:F4},{r.MappedBoxIoU:F4},{r.PreprocessMs:F1},{r.StagesApplied},{r.ReusedBaseline}");
        }

        File.WriteAllText(Path.Combine(dir, "document-profile-evaluation.md"), report);
        File.WriteAllText(Path.Combine(dir, "document-profile-evaluation.csv"), csv.ToString());
        _out.WriteLine($"Wrote document-profile-evaluation.md/.csv to {dir}");
    }
}
