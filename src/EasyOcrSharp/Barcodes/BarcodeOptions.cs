using System.Collections.Generic;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Barcodes;

/// <summary>
/// Tunable options for a barcode scan. Every knob trades scan time for hit rate, so the defaults aim at
/// the common document case — one upright, dark-on-light symbol somewhere on the page — and you opt in
/// to the more exhaustive passes.
/// </summary>
/// <remarks>
/// Barcode scanning is pure image processing: it needs no OCR model, no download, and no GPU, so it is
/// cheap enough to run alongside recognition. Use <see cref="BarcodeScanner"/> on its own, or
/// <see cref="BarcodeExtensions"/> to get text and barcodes from a single decode of the image.
/// </remarks>
public sealed record BarcodeOptions
{
    /// <summary>
    /// Restrict the scan to these symbologies (e.g. <c>[BarcodeFormat.QrCode]</c> for a QR-only kiosk).
    /// Narrowing the list is both faster and less prone to false positives. Null or empty (the default)
    /// tries every supported symbology.
    /// </summary>
    public IReadOnlyList<BarcodeFormat>? Formats { get; init; }

    /// <summary>
    /// Spend more effort looking for a symbol — additional scan lines and rotated 1D passes. Defaults to
    /// <see langword="true"/>, because photographed and scanned pages rarely decode on the first try;
    /// set it to <see langword="false"/> for high-throughput scanning of clean, machine-generated images.
    /// </summary>
    public bool TryHarder { get; init; } = true;

    /// <summary>
    /// Keep scanning after the first hit and return every symbol found in the image. Defaults to
    /// <see langword="false"/> (stop at the first), which is markedly faster when you know there is only
    /// one code — a shipping label, a ticket, an invoice stamp.
    /// </summary>
    public bool MultipleCodes { get; init; }

    /// <summary>
    /// If nothing is found, retry with the image inverted, so light-on-dark symbols (screens, etched
    /// metal, reversed labels) are read too. Roughly doubles the worst-case scan time. Default off.
    /// </summary>
    public bool TryInverted { get; init; }

    /// <summary>
    /// If nothing is found upright, retry the image rotated by 90°, 180° and 270°. Up to four times the
    /// worst-case scan time. Coordinates are always reported back in the original image's space, whatever
    /// orientation the symbol was found in. Default off.
    /// </summary>
    public bool AutoRotate { get; init; }

    /// <summary>
    /// Restrict the scan to a rectangular sub-region of the image — the corner of a form, the label area
    /// of a parcel photo. Faster and far less prone to false positives than scanning the whole page.
    /// Coordinates are still reported in the original image's space. Null (the default) scans everything.
    /// </summary>
    public OcrRegion? Region { get; init; }

    /// <summary>
    /// Refuse to decode images larger than this many pixels, the decompression-bomb / pixel-flood guard.
    /// Only applies to the overloads that decode an image themselves (path, bytes, stream); an image you
    /// already hold has by definition already been decoded. Default 100,000,000 (100 MP), matching
    /// <c>EasyOcrServiceOptions.MaxImagePixels</c>; set to 0 to disable the guard.
    /// </summary>
    public long MaxImagePixels { get; init; } = 100_000_000;

    /// <summary>
    /// The default options: every symbology, <see cref="TryHarder"/> on, first match only, no inversion,
    /// no rotation, whole image.
    /// </summary>
    public static BarcodeOptions Default { get; } = new();
}
