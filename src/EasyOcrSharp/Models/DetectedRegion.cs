namespace EasyOcrSharp.Models;

/// <summary>
/// A text region located by the detector <b>without</b> recognizing its contents. Returned by
/// <see cref="Services.IEasyOcrService.DetectRegionsAsync(SixLabors.ImageSharp.Image{SixLabors.ImageSharp.PixelFormats.Rgb24}, RecognitionOptions?, System.Threading.CancellationToken)"/>
/// — useful for layout analysis, redaction, or cropping fields before a separate recognition pass.
/// </summary>
public sealed record DetectedRegion
{
    /// <summary>The four corners of the detected region, in original-image coordinates.</summary>
    public IReadOnlyList<OcrPoint> BoundingPolygon { get; init; } = Array.Empty<OcrPoint>();

    /// <summary>The axis-aligned bounding box computed from <see cref="BoundingPolygon"/>.</summary>
    public OcrBoundingBox BoundingBox { get; init; } = OcrBoundingBox.Empty;
}
