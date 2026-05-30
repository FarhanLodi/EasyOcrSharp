using EasyOcrSharp.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Crops detected regions out of the source image, rectifies them, runs the per-language
/// CRNN ONNX model, and CTC-decodes the output to text + confidence. The preprocessing,
/// decoding, confidence formula and low-confidence contrast retry all mirror upstream EasyOCR
/// so accuracy matches the reference implementation.
/// </summary>
internal sealed class CrnnRecognizer : IDisposable
{
    public const int TargetHeight = 64;
    // Upper bound on the resized width fed to the model. EasyOCR doesn't cap single-box width;
    // this only guards against pathologically wide boxes blowing up memory / LSTM runtime.
    public const int MaxWidth = 4096;
    // EasyOCR re-runs a box with contrast stretching when its confidence falls below this.
    private const double ContrastThreshold = 0.1;

    private readonly InferenceSession _session;
    private readonly string _imageInputName;
    private readonly string? _textInputName;
    private readonly string _outputName;
    private readonly string _characters;

    public CrnnRecognizer(string modelPath, string characters, SessionOptions? sessionOptions = null)
    {
        _session = new InferenceSession(modelPath, sessionOptions ?? new SessionOptions());
        // EasyOCR's Model.forward(input, text) — `text` is ignored for CTC but PyTorch
        // demanded it stay in the exported graph. Image is float, text is int64.
        _imageInputName = _session.InputMetadata
            .First(kv => kv.Value.ElementType == typeof(float)).Key;
        _textInputName = _session.InputMetadata
            .FirstOrDefault(kv => kv.Value.ElementType != typeof(float)).Key;
        _outputName = _session.OutputMetadata.Keys.First();
        _characters = characters;
    }

    public IReadOnlyList<OcrLine> Recognize(Image<Rgb24> source, IReadOnlyList<OcrPoint[]> polygons)
    {
        var results = new List<OcrLine>(polygons.Count);

        foreach (var poly in polygons)
        {
            using var crop = RectifyQuad(source, poly);
            if (crop is null || crop.Width == 0 || crop.Height == 0) continue;

            // Round 1: no contrast adjustment (EasyOCR's AlignCollate_normal).
            var (text, conf) = RunOnce(crop, adjustContrast: false);

            // Round 2: EasyOCR re-runs low-confidence boxes with contrast stretching
            // and keeps whichever decode scored higher.
            if (conf < ContrastThreshold)
            {
                var (text2, conf2) = RunOnce(crop, adjustContrast: true);
                if (conf2 > conf)
                {
                    text = text2;
                    conf = conf2;
                }
            }

            results.Add(new OcrLine
            {
                Text = text,
                Confidence = conf,
                BoundingPolygon = poly,
                BoundingBox = OcrBoundingBox.FromPoints(poly),
            });
        }

        return results;
    }

    private (string Text, double Confidence) RunOnce(Image<Rgb24> crop, bool adjustContrast)
    {
        var tensorData = ImageProcessing.PreprocessForCrnn(crop, TargetHeight, MaxWidth, adjustContrast, out int width);
        if (width < 1) return (string.Empty, 0.0);

        var input = new DenseTensor<float>(tensorData, new[] { 1, 1, TargetHeight, width });
        var feeds = new List<NamedOnnxValue>(2)
        {
            NamedOnnxValue.CreateFromTensor(_imageInputName, input),
        };
        if (_textInputName is not null)
        {
            var textPlaceholder = new DenseTensor<long>(new[] { 1, 1 });
            feeds.Add(NamedOnnxValue.CreateFromTensor(_textInputName, textPlaceholder));
        }

        using var run = _session.Run(feeds);
        var output = run.First(o => o.Name == _outputName).AsTensor<float>();

        // Expected shape (T, 1, C) or (1, T, C). Normalise to (T, C).
        int t, c;
        float[,] logits;
        if (output.Dimensions.Length == 3 && output.Dimensions[1] == 1)
        {
            t = output.Dimensions[0];
            c = output.Dimensions[2];
            logits = new float[t, c];
            for (int i = 0; i < t; i++)
                for (int j = 0; j < c; j++)
                    logits[i, j] = output[i, 0, j];
        }
        else if (output.Dimensions.Length == 3 && output.Dimensions[0] == 1)
        {
            t = output.Dimensions[1];
            c = output.Dimensions[2];
            logits = new float[t, c];
            for (int i = 0; i < t; i++)
                for (int j = 0; j < c; j++)
                    logits[i, j] = output[0, i, j];
        }
        else
        {
            throw new EasyOcrSharpException(
                $"Unexpected recognizer output shape [{string.Join(",", output.Dimensions.ToArray())}]. Expected 3D tensor.");
        }

        return CtcGreedyDecode(logits, t, c);
    }

    /// <summary>
    /// Greedy CTC decode mirroring EasyOCR: per-timestep softmax + argmax, collapse consecutive
    /// duplicates, drop the blank (index 0); class index k (≥1) maps to <c>_characters[k-1]</c>.
    /// Confidence is EasyOCR's <c>custom_mean</c> — the geometric-style mean
    /// <c>(∏ p)^(2/√n)</c> over the max softmax probability at every non-blank timestep.
    /// </summary>
    private (string Text, double Confidence) CtcGreedyDecode(float[,] logits, int steps, int classes)
    {
        var sb = new System.Text.StringBuilder();
        double logProbSum = 0;   // Σ ln(maxProb) over non-blank timesteps
        int probCount = 0;
        int lastIdx = -1;

        for (int t = 0; t < steps; t++)
        {
            // Numerically stable softmax over the class dimension.
            float max = float.NegativeInfinity;
            int argmax = 0;
            for (int cc = 0; cc < classes; cc++)
            {
                if (logits[t, cc] > max) { max = logits[t, cc]; argmax = cc; }
            }
            double sumExp = 0;
            for (int cc = 0; cc < classes; cc++)
                sumExp += Math.Exp(logits[t, cc] - max);
            double prob = 1.0 / sumExp; // exp(max-max)=1 over sumExp == softmax of the argmax class

            if (argmax != 0)
            {
                int charIdx = argmax - 1;
                // Vocab positions exported as U+0000 are EasyOCR's word-segmentation separators
                // (ignore_idx); skip them entirely — they're neither emitted nor counted, matching
                // EasyOCR's decoder which filters them out of the text.
                bool isSeparator = charIdx >= 0 && charIdx < _characters.Length && _characters[charIdx] == '\0';
                if (!isSeparator)
                {
                    // EasyOCR's custom_mean uses every timestep whose argmax is a real character.
                    logProbSum += Math.Log(prob);
                    probCount++;

                    // CTC collapse: emit only when the class changes from the previous step.
                    if (argmax != lastIdx && charIdx >= 0 && charIdx < _characters.Length)
                        sb.Append(_characters[charIdx]);
                }
            }
            lastIdx = argmax;
        }

        // custom_mean(x) = (∏ x)^(2/√n) = exp( (2/√n) · Σ ln x ).
        double confidence = probCount > 0
            ? Math.Exp(2.0 / Math.Sqrt(probCount) * logProbSum)
            : 0.0;
        return (sb.ToString(), confidence);
    }

    /// <summary>
    /// Rectifies a (possibly rotated) quadrilateral region from the source image into an
    /// upright rectangle. Uses an axis-aligned bbox of the quad as an approximation —
    /// works for the slight rotations that CRAFT typically produces, and matches EasyOCR's
    /// horizontal-box crop path. For perfectly horizontal text this is identical to a plain crop.
    /// </summary>
    private static Image<Rgb24>? RectifyQuad(Image<Rgb24> source, OcrPoint[] quad)
    {
        if (quad.Length < 3) return null;

        double minX = quad.Min(p => p.X);
        double minY = quad.Min(p => p.Y);
        double maxX = quad.Max(p => p.X);
        double maxY = quad.Max(p => p.Y);

        int x = (int)Math.Floor(minX);
        int y = (int)Math.Floor(minY);
        int w = (int)Math.Ceiling(maxX - minX);
        int h = (int)Math.Ceiling(maxY - minY);

        x = Math.Max(0, x);
        y = Math.Max(0, y);
        if (x + w > source.Width) w = source.Width - x;
        if (y + h > source.Height) h = source.Height - y;
        if (w < 2 || h < 2) return null;

        var rect = new Rectangle(x, y, w, h);
        return source.Clone(ctx => ctx.Crop(rect));
    }

    public void Dispose() => _session.Dispose();
}
