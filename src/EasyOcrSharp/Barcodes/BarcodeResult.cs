using System;
using System.Collections.Generic;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Barcodes;

/// <summary>
/// A single barcode or QR code found in an image, reported in the source image's pixel coordinates
/// alongside the OCR results for the same page.
/// </summary>
public sealed record BarcodeResult
{
    /// <summary>
    /// Gets the decoded payload as text. For binary payloads this is the decoder's best-effort textual
    /// interpretation; use <see cref="RawBytes"/> when the exact bytes matter.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>Gets the symbology the payload was read from.</summary>
    public BarcodeFormat Format { get; init; }

    /// <summary>
    /// Gets the raw bytes encoded in the symbol, before any character-set interpretation. Empty for the
    /// symbologies that carry no byte-level representation (most 1D codes, whose payload is digits by
    /// construction) — a barcode with an empty <see cref="RawBytes"/> is normal, not an error.
    /// </summary>
    public ReadOnlyMemory<byte> RawBytes { get; init; }

    /// <summary>
    /// Gets the feature points the decoder located, in source-image pixel coordinates — and in source
    /// orientation even when <see cref="BarcodeOptions.AutoRotate"/> found the symbol sideways.
    /// </summary>
    /// <remarks>
    /// This is not a four-corner quadrilateral: what the points mean is symbology-specific. A QR code
    /// reports its three finder patterns, a 1D barcode the two ends of its centre scan line, PDF417 the
    /// corners of its row blocks. Use <see cref="BoundingBox"/> when you just want a rectangle to draw or
    /// redact.
    /// </remarks>
    public IReadOnlyList<OcrPoint> BoundingPolygon { get; init; } = Array.Empty<OcrPoint>();

    /// <summary>
    /// Gets the axis-aligned bounding box of <see cref="BoundingPolygon"/>. Because the feature points
    /// sit on the symbol rather than around it (the centre line of a 1D barcode, the finder centres of a
    /// QR code), this box is a close but slightly conservative outline of the printed symbol.
    /// </summary>
    public OcrBoundingBox BoundingBox { get; init; } = OcrBoundingBox.Empty;
}

/// <summary>
/// The combined outcome of reading a page once: the recognized text and every barcode on it. Returned by
/// <see cref="BarcodeExtensions"/>' <c>ExtractTextAndBarcodesAsync</c>.
/// </summary>
public sealed record OcrAndBarcodeResult
{
    /// <summary>Gets the OCR result, exactly as <c>ExtractTextFromImage</c> would have returned it.</summary>
    public required OcrResult Ocr { get; init; }

    /// <summary>Gets the barcodes found on the page. Empty when there are none.</summary>
    public required IReadOnlyList<BarcodeResult> Barcodes { get; init; }

    /// <summary>Gets a value indicating whether at least one barcode was found.</summary>
    public bool HasBarcodes => Barcodes.Count > 0;

    /// <summary>An empty combined result — no text, no barcodes.</summary>
    public static OcrAndBarcodeResult Empty { get; } = new()
    {
        Ocr = OcrResult.Empty,
        Barcodes = Array.Empty<BarcodeResult>(),
    };
}
