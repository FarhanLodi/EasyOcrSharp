using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Microsoft.Extensions.Logging;
using EasyOcrSharp.Structure.Engine.Classification;
using EasyOcrSharp.Structure.Engine.Recognition;
using EasyOcrSharp.Structure.Engine.Models;

namespace EasyOcrSharp.Structure.Engine.Detection;

/// <summary>
/// Runs each loaded model once on a small blank input so ONNX Runtime's first-inference costs — kernel
/// selection, memory-arena growth, lazy graph initialization and (on GPU) algorithm search — are paid
/// during warm-up rather than by the first real OCR request. Loading a session alone does none of this.
/// <para>
/// Shapes match what the pipeline feeds each model: a 320×320 detector input, the classifier's fixed
/// 80×160 crop, and a 48×320 recognizer line (the recognizer's minimum batch width). Blank images yield
/// no detections or text; only the side effects matter. Failures are logged and swallowed — warm-up must
/// never make a working engine fail.
/// </para>
/// </summary>
internal static class ModelWarmUp
{
    /// <summary>
    /// Runs one dummy inference on every non-null model.
    /// </summary>
    /// <param name="detector">The DB text detector, or null to skip.</param>
    /// <param name="classifier">The text-line orientation classifier, or null to skip.</param>
    /// <param name="recognizers">Recognizers to warm (may be empty).</param>
    /// <param name="logger">Optional logger for swallowed failures.</param>
    public static void Run(
        ITextDetector? detector,
        IAngleClassifier? classifier,
        IEnumerable<ITextRecognizer> recognizers,
        ILogger? logger)
    {
        if (detector is not null)
        {
            Try(logger, "detector", () =>
            {
                using var image = Blank(320, 320);
                detector.Detect(image, DetectionOptions.Default);
            });
        }

        if (classifier is not null)
        {
            Try(logger, "classifier", () =>
            {
                using var image = Blank(160, 80);
                classifier.Classify(image);
            });
        }

        foreach (var recognizer in recognizers)
        {
            Try(logger, "recognizer", () =>
            {
                using var image = Blank(320, 48);
                recognizer.Recognize(image);
            });
        }
    }

    private static Image<Rgb24> Blank(int width, int height)
    {
        var image = new Image<Rgb24>(width, height);
        image.Mutate(c => c.BackgroundColor(Color.White));
        return image;
    }

    private static void Try(ILogger? logger, string model, Action run)
    {
        try
        {
            run();
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Warm-up inference for the {Model} failed; it will initialize on first use.", model);
        }
    }
}
