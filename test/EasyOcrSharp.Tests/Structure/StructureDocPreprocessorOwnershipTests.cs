using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyOcrSharp.Structure.Preprocess;
using Xunit;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Ownership contract of <see cref="DocPreprocessor.Apply"/>: the caller's input is never disposed or
/// modified, and a new image (which the caller must dispose) is returned only when a stage changed pixels.
/// </summary>
public sealed class StructureDocPreprocessorOwnershipTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Apply_with_no_active_stage_returns_the_input_itself_unowned(bool useOrientation, bool useUnwarp)
    {
        // No sessions: every requested stage is unavailable, so nothing can change the pixels.
        using var preprocessor = new DocPreprocessor(orientation: null, unwarp: null);
        using var input = new Image<Rgb24>(8, 6, new Rgb24(10, 20, 30));

        var result = preprocessor.Apply(input, useOrientation, useUnwarp);

        Assert.Same(input, result.Image);
        Assert.False(result.OwnsImage);
        Assert.Equal(0, result.RotationApplied);

        // The input is still alive and untouched: cloning a disposed image would throw.
        using var probe = input.Clone();
        Assert.Equal(new Rgb24(10, 20, 30), probe[3, 2]);
    }

    [Fact]
    public void Apply_rejects_a_null_input()
    {
        using var preprocessor = new DocPreprocessor(orientation: null, unwarp: null);
        Assert.Throws<ArgumentNullException>(() => preprocessor.Apply(null!, true, true));
    }
}
