namespace EasyOcrSharp.Models;

/// <summary>
/// Progress for multi-frame image processing, reported after each frame is recognized via
/// <see cref="MultiFrameOcrOptions.Progress"/>. Shaped like <see cref="Pdf.PdfPageProgress"/>, since a
/// multi-page TIFF and a multi-page PDF are the same job from the caller's point of view.
/// </summary>
/// <param name="FrameNumber">1-based frame that has just been processed.</param>
/// <param name="FrameCount">Total frames in the source image.</param>
public readonly record struct MultiFrameProgress(int FrameNumber, int FrameCount)
{
    /// <summary>Completion fraction (0–1).</summary>
    public double Fraction => FrameCount > 0 ? (double)FrameNumber / FrameCount : 0;
}

/// <summary>
/// OCR result for a single frame (page) of a multi-frame image such as a scanned multi-page TIFF or an
/// animated GIF.
/// </summary>
public sealed record FrameOcrResult
{
    /// <summary>0-based index of this frame within the source image, in file order.</summary>
    public required int FrameIndex { get; init; }

    /// <summary>The recognized text and lines for this frame.</summary>
    public required OcrResult Ocr { get; init; }

    /// <summary>Frame width in pixels.</summary>
    public int PixelWidth { get; init; }

    /// <summary>Frame height in pixels.</summary>
    public int PixelHeight { get; init; }
}

/// <summary>
/// Aggregate OCR result for every frame of a multi-frame image. A single-frame image (an ordinary PNG or
/// JPEG) produces exactly one entry, so callers never need to branch on frame count.
/// </summary>
public sealed record MultiFrameOcrResult
{
    /// <summary>Per-frame results in file order.</summary>
    public required IReadOnlyList<FrameOcrResult> Frames { get; init; }

    /// <summary>Wall-clock duration of the whole run — decoding the container plus every frame's OCR.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Number of frames that were recognized.</summary>
    public int FrameCount => Frames.Count;

    /// <summary>All frames' text concatenated in file order, separated by blank lines.</summary>
    public string FullText => string.Join("\n\n", Frames.Select(f => f.Ocr.FullText));

    /// <summary>An empty result (no frames).</summary>
    public static MultiFrameOcrResult Empty { get; } = new()
    {
        Frames = Array.Empty<FrameOcrResult>(),
        Duration = TimeSpan.Zero,
    };
}

/// <summary>
/// Bounds and progress reporting for multi-frame image processing. Mirrors <see cref="Pdf.PdfOcrOptions"/>:
/// every setting is a guard against a hostile or accidental resource bomb, and an instance with no changes
/// is safe for untrusted input.
/// </summary>
public sealed class MultiFrameOcrOptions
{
    /// <summary>
    /// Maximum number of frames to accept. An image with more frames is rejected before any frame is
    /// recognized — a guard against a malicious TIFF/GIF forcing unbounded CPU/time. Exceeding it throws
    /// <see cref="ImageTooLargeException"/>. Default 5000. Set to 0 for no limit.
    /// </summary>
    public int MaxFrames { get; set; } = 5000;

    /// <summary>
    /// Maximum megapixels per frame (width × height). A frame that exceeds this is rejected before its
    /// pixels are cloned, throwing <see cref="ImageTooLargeException"/> — the same decompression-bomb guard
    /// as <c>EasyOcrServiceOptions.MaxImagePixels</c>, applied per frame because the multi-frame reader
    /// decodes the container itself. Default 100 (100 MP, matching that option). Set to 0 for no limit.
    /// </summary>
    public int MaxFrameMegapixels { get; set; } = 100;

    /// <summary>Optional per-frame progress callback, reported once each frame finishes.</summary>
    public IProgress<MultiFrameProgress>? Progress { get; set; }

    /// <summary>Per-frame pixel budget derived from <see cref="MaxFrameMegapixels"/> (0 = unlimited).</summary>
    internal long MaxFramePixels => MaxFrameMegapixels <= 0 ? 0 : (long)MaxFrameMegapixels * 1_000_000L;

    internal void Validate()
    {
        if (MaxFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxFrames), MaxFrames, "MaxFrames must be 0 (unlimited) or positive.");
        if (MaxFrameMegapixels < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxFrameMegapixels), MaxFrameMegapixels, "MaxFrameMegapixels must be 0 (unlimited) or positive.");
    }
}
