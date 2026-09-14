using EasyOcrSharp.Structure.Engine.Recognition;
using Xunit;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for
/// <see cref="SvtrRecognizer.ComputeBatchTensorWidth"/> — the Python-parity batch tensor width
/// (PaddleX <c>text_recognition/processors.py</c>): <c>imgW = int(H · max(320/H, widest w/h))</c>,
/// floored to int and clamped into [320, 3200].
/// </summary>
public class RecognitionBatchWidthTests
{
    [Theory]
    [InlineData(1.0)]   // near-square crop
    [InlineData(0.2)]   // tall/narrow crop
    [InlineData(6.6)]   // just below 320/48
    public void Narrow_batches_get_the_320px_floor(double widestRatio)
        => Assert.Equal(320, SvtrRecognizer.ComputeBatchTensorWidth(48, widestRatio));

    [Fact]
    public void Wide_batches_scale_with_the_widest_crop_using_floor_truncation()
    {
        // 48 * 10.5 = 504 exactly; 48 * 10.99 = 527.52 truncates to 527 (Python int()).
        Assert.Equal(504, SvtrRecognizer.ComputeBatchTensorWidth(48, 10.5));
        Assert.Equal(527, SvtrRecognizer.ComputeBatchTensorWidth(48, 10.99));
    }

    [Fact]
    public void Extremely_wide_batches_are_capped_at_3200()
        => Assert.Equal(3200, SvtrRecognizer.ComputeBatchTensorWidth(48, 100.0));

    [Theory]
    [InlineData(48)]
    [InlineData(32)]
    [InlineData(64)]
    public void Width_is_always_within_the_320_to_3200_envelope(int height)
    {
        foreach (double ratio in new[] { 0.1, 1.0, 5.0, 6.7, 20.0, 66.7, 1000.0 })
        {
            int w = SvtrRecognizer.ComputeBatchTensorWidth(height, ratio);
            Assert.InRange(w, 320, 3200);
        }
    }
}
