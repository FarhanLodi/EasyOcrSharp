namespace EasyOcrSharp.Structure.Engine.Geometry;

/// <summary>
/// Minimal binary-image morphology used by DB detection post-processing. Mirrors the single
/// OpenCV call PaddleOCR makes — <c>cv2.dilate(segmentation, np.ones((2,2)))</c> — when
/// <c>use_dilation</c> is set, so thin/broken strokes are bridged before contour extraction.
/// </summary>
internal static class Morphology
{
    /// <summary>
    /// Dilates a binary mask with the 2x2 box kernel PaddleOCR uses (<c>np.ones((2, 2))</c>).
    /// <para>
    /// OpenCV anchors that even kernel at <c>(kw/2, kh/2) = (1, 1)</c>, so for output pixel (x, y) the
    /// kernel covers input pixels (x, y), (x-1, y), (x, y-1), (x-1, y-1). An output pixel is set when any
    /// covered input pixel is non-zero (so each lit input grows down and to the right). Pixels reaching
    /// outside the image are treated as background (BORDER_CONSTANT 0), matching OpenCV's default; values
    /// are normalized to 0/1.
    /// </para>
    /// </summary>
    /// <param name="mask">Binary mask, row-major <c>mask[y*width+x]</c>; any non-zero value is "on".</param>
    /// <param name="width">Mask width in pixels.</param>
    /// <param name="height">Mask height in pixels.</param>
    /// <returns>A new mask (0/1) of the same dimensions; the input is not modified.</returns>
    public static byte[] Dilate2x2(ReadOnlySpan<byte> mask, int width, int height)
    {
        var result = new byte[width * height];
        if (width <= 0 || height <= 0 || mask.Length < width * height)
        {
            return result;
        }

        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                // Output (x, y) is on if any of the 2x2 input cells anchored here is on:
                // (x, y), (x-1, y), (x, y-1), (x-1, y-1). Out-of-range cells count as background.
                bool on = mask[rowBase + x] != 0
                    || (x > 0 && mask[rowBase + x - 1] != 0)
                    || (y > 0 && mask[rowBase - width + x] != 0)
                    || (x > 0 && y > 0 && mask[rowBase - width + x - 1] != 0);

                if (on)
                {
                    result[rowBase + x] = 1;
                }
            }
        }

        return result;
    }
}
