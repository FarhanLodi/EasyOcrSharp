using EasyOcrSharp.Structure.Engine.Classification;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Pure-function tests (no model download, CI-safe) for
/// <see cref="TextLineClassifier.BuildInputTensor"/> — the textline-orientation (cls) preprocessing:
/// stretch-resize to exactly 160×80 (no aspect preservation, no padding) and ImageNet
/// <c>(x/255 − mean)/std</c> normalization in RGB CHW order, matching PaddleX's
/// <c>_CLS_PREPROCESS_TEXTLINE</c>.
/// </summary>
public class TextLineClassifierPreprocessTests
{
    [Fact]
    public void Input_tensor_has_the_fixed_cls_shape()
    {
        using var crop = new Image<Rgb24>(300, 40); // arbitrary aspect — must be stretched, not padded
        var tensor = TextLineClassifier.BuildInputTensor(crop);

        Assert.Equal(new[] { 1, 3, 80, 160 }, tensor.Dimensions.ToArray());
    }

    [Fact]
    public void Solid_color_crop_normalizes_with_imagenet_stats_in_rgb_chw_order()
    {
        // A solid color survives any resample, so every plane must be a constant at the ImageNet
        // normalization of that channel: (x/255 - mean) / std with mean [0.485,0.456,0.406] and
        // std [0.229,0.224,0.225], R in plane 0, G in plane 1, B in plane 2.
        var color = new Rgb24(128, 64, 200);
        using var crop = new Image<Rgb24>(97, 33, color);
        var tensor = TextLineClassifier.BuildInputTensor(crop);

        float expectedR = (128 / 255f - 0.485f) / 0.229f;
        float expectedG = (64 / 255f - 0.456f) / 0.224f;
        float expectedB = (200 / 255f - 0.406f) / 0.225f;

        const int plane = 80 * 160;
        var buffer = tensor.Buffer.Span;
        // Sample the corners and center of each plane rather than all 12800 px per channel.
        foreach (int offset in new[] { 0, 159, 40 * 160 + 80, 79 * 160, 79 * 160 + 159 })
        {
            Assert.Equal(expectedR, buffer[offset], 4);
            Assert.Equal(expectedG, buffer[plane + offset], 4);
            Assert.Equal(expectedB, buffer[2 * plane + offset], 4);
        }
    }

    [Fact]
    public void Stretch_resize_maps_left_and_right_halves_without_padding()
    {
        // Left half black, right half white, wider than 160: a stretch (no pad) keeps the halves at
        // the tensor's left/right ends. Zero-padding (the old behavior) would leave the right end at
        // the padding value instead of white.
        using var crop = new Image<Rgb24>(400, 40);
        for (int y = 0; y < 40; y++)
        {
            for (int x = 200; x < 400; x++)
            {
                crop[x, y] = new Rgb24(255, 255, 255);
            }
        }

        var tensor = TextLineClassifier.BuildInputTensor(crop);
        var buffer = tensor.Buffer.Span;

        float black = (0 / 255f - 0.485f) / 0.229f;
        float white = (255 / 255f - 0.485f) / 0.229f;
        int midRow = 40 * 160;
        Assert.Equal(black, buffer[midRow + 2], 3);       // near the left edge
        Assert.Equal(white, buffer[midRow + 157], 3);     // near the right edge — not padding
    }
}
