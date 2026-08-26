using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Structure.Table;

/// <summary>
/// PP-LCNet table-type classifier (<c>PP-LCNet_x1_0_table_cls</c>). Resizes the table crop to the model's
/// fixed 224×224 input, ImageNet-normalizes it, and runs a 2-class head whose argmax selects <i>wired</i>
/// (class 0, ruled/bordered) or <i>wireless</i> (class 1, borderless). Owns and disposes the ONNX session.
/// <para>
/// Verified against the real exported graph via onnxruntime: <b>input</b> <c>x</c> float [N,3,224,224];
/// <b>output</b> float [N,2]. On a bordered synthetic table the head returns class 0 (wired) at 0.94, matching
/// PaddleOCR's class order <c>["wired", "wireless"]</c>.
/// </para>
/// </summary>
internal sealed class TableClassifier : ITableClassifier
{
    /// <summary>The fixed square input edge the classifier was exported with.</summary>
    private const int InputSize = 224;

    /// <summary>ImageNet per-channel mean (RGB order).</summary>
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };

    /// <summary>ImageNet per-channel std (RGB order).</summary>
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;

    /// <summary>Creates the classifier over a built PP-LCNet_x1_0_table_cls session (takes ownership).</summary>
    public TableClassifier(InferenceSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    /// <inheritdoc />
    public bool IsWireless(Image<Rgb24> tableCrop)
    {
        ArgumentNullException.ThrowIfNull(tableCrop);

        var input = Preprocess(tableCrop);
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) };
        using var outputs = _session.Run(inputs, new[] { _outputName });

        var logits = outputs.First().AsTensor<float>().ToArray();
        // Class order is ["wired", "wireless"]; argmax == 1 -> wireless. A degenerate single-class head
        // (shouldn't happen) defaults to wired.
        return logits.Length >= 2 && logits[1] > logits[0];
    }

    /// <summary>
    /// Resizes to 224×224 (stretch), ImageNet-normalizes <c>(pixel/255 - mean) / std</c> in RGB order, and
    /// packs planar CHW into a <c>[1,3,224,224]</c> float tensor.
    /// </summary>
    private static DenseTensor<float> Preprocess(Image<Rgb24> crop)
    {
        using var resized = crop.Clone(c => c.Resize(new ResizeOptions
        {
            Size = new Size(InputSize, InputSize),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic,
        }));

        int plane = InputSize * InputSize;
        var data = new float[3 * plane];
        resized.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < InputSize; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);
                int rowBase = y * InputSize;
                for (int x = 0; x < InputSize; x++)
                {
                    Rgb24 px = row[x];
                    int p = rowBase + x;
                    data[p] = (px.R / 255f - Mean[0]) / Std[0];             // channel 0 (R)
                    data[plane + p] = (px.G / 255f - Mean[1]) / Std[1];     // channel 1 (G)
                    data[2 * plane + p] = (px.B / 255f - Mean[2]) / Std[2]; // channel 2 (B)
                }
            }
        });

        return new DenseTensor<float>(data, new[] { 1, 3, InputSize, InputSize });
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}
