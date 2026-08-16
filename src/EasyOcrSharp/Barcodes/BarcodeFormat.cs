using System;
using System.Collections.Generic;
using ZXingFormat = ZXing.BarcodeFormat;

namespace EasyOcrSharp.Barcodes;

/// <summary>
/// The barcode symbologies EasyOcrSharp can read. This mirrors the underlying decoder's format list but
/// is deliberately a type of its own, so the barcode engine stays an implementation detail that can be
/// swapped without a breaking change to your code.
/// </summary>
public enum BarcodeFormat
{
    /// <summary>The symbology could not be mapped to a known value. Never returned in practice.</summary>
    Unknown = 0,

    /// <summary>Aztec 2D barcode.</summary>
    Aztec,

    /// <summary>Codabar 1D barcode.</summary>
    Codabar,

    /// <summary>Code 39 1D barcode.</summary>
    Code39,

    /// <summary>Code 93 1D barcode.</summary>
    Code93,

    /// <summary>Code 128 1D barcode.</summary>
    Code128,

    /// <summary>Data Matrix 2D barcode.</summary>
    DataMatrix,

    /// <summary>EAN-8 1D barcode.</summary>
    Ean8,

    /// <summary>EAN-13 1D barcode.</summary>
    Ean13,

    /// <summary>USPS Intelligent Mail barcode.</summary>
    IntelligentMail,

    /// <summary>ITF (Interleaved 2 of 5) 1D barcode.</summary>
    Itf,

    /// <summary>MaxiCode 2D barcode.</summary>
    MaxiCode,

    /// <summary>MSI Plessey 1D barcode.</summary>
    Msi,

    /// <summary>PDF417 stacked 2D barcode.</summary>
    Pdf417,

    /// <summary>Pharmacode 1D barcode.</summary>
    Pharmacode,

    /// <summary>Plessey 1D barcode.</summary>
    Plessey,

    /// <summary>QR Code 2D barcode.</summary>
    QrCode,

    /// <summary>GS1 DataBar (formerly RSS-14) 1D barcode.</summary>
    Rss14,

    /// <summary>GS1 DataBar Expanded (formerly RSS Expanded) 1D barcode.</summary>
    RssExpanded,

    /// <summary>UPC-A 1D barcode.</summary>
    UpcA,

    /// <summary>UPC-E 1D barcode.</summary>
    UpcE,

    /// <summary>A UPC/EAN add-on (2- or 5-digit supplemental) symbol.</summary>
    UpcEanExtension,
}

/// <summary>
/// Translates between <see cref="BarcodeFormat"/> and the decoder's own format enum. Kept internal so
/// the barcode engine never leaks into the public surface.
/// </summary>
internal static class BarcodeFormatMap
{
    /// <summary>Maps a decoder format onto the public enum, falling back to <see cref="BarcodeFormat.Unknown"/>.</summary>
    internal static BarcodeFormat FromZXing(ZXingFormat format) => format switch
    {
        ZXingFormat.AZTEC => BarcodeFormat.Aztec,
        ZXingFormat.CODABAR => BarcodeFormat.Codabar,
        ZXingFormat.CODE_39 => BarcodeFormat.Code39,
        ZXingFormat.CODE_93 => BarcodeFormat.Code93,
        ZXingFormat.CODE_128 => BarcodeFormat.Code128,
        ZXingFormat.DATA_MATRIX => BarcodeFormat.DataMatrix,
        ZXingFormat.EAN_8 => BarcodeFormat.Ean8,
        ZXingFormat.EAN_13 => BarcodeFormat.Ean13,
        ZXingFormat.IMB => BarcodeFormat.IntelligentMail,
        ZXingFormat.ITF => BarcodeFormat.Itf,
        ZXingFormat.MAXICODE => BarcodeFormat.MaxiCode,
        ZXingFormat.MSI => BarcodeFormat.Msi,
        ZXingFormat.PDF_417 => BarcodeFormat.Pdf417,
        ZXingFormat.PHARMA_CODE => BarcodeFormat.Pharmacode,
        ZXingFormat.PLESSEY => BarcodeFormat.Plessey,
        ZXingFormat.QR_CODE => BarcodeFormat.QrCode,
        ZXingFormat.RSS_14 => BarcodeFormat.Rss14,
        ZXingFormat.RSS_EXPANDED => BarcodeFormat.RssExpanded,
        ZXingFormat.UPC_A => BarcodeFormat.UpcA,
        ZXingFormat.UPC_E => BarcodeFormat.UpcE,
        ZXingFormat.UPC_EAN_EXTENSION => BarcodeFormat.UpcEanExtension,
        _ => BarcodeFormat.Unknown,
    };

    /// <summary>
    /// Maps the public enum onto a decoder format. Returns <see langword="false"/> for
    /// <see cref="BarcodeFormat.Unknown"/> (and anything unmapped), which callers treat as
    /// "leave this one out of the format filter" rather than as an error.
    /// </summary>
    internal static bool TryToZXing(BarcodeFormat format, out ZXingFormat zxing)
    {
        switch (format)
        {
            case BarcodeFormat.Aztec: zxing = ZXingFormat.AZTEC; return true;
            case BarcodeFormat.Codabar: zxing = ZXingFormat.CODABAR; return true;
            case BarcodeFormat.Code39: zxing = ZXingFormat.CODE_39; return true;
            case BarcodeFormat.Code93: zxing = ZXingFormat.CODE_93; return true;
            case BarcodeFormat.Code128: zxing = ZXingFormat.CODE_128; return true;
            case BarcodeFormat.DataMatrix: zxing = ZXingFormat.DATA_MATRIX; return true;
            case BarcodeFormat.Ean8: zxing = ZXingFormat.EAN_8; return true;
            case BarcodeFormat.Ean13: zxing = ZXingFormat.EAN_13; return true;
            case BarcodeFormat.IntelligentMail: zxing = ZXingFormat.IMB; return true;
            case BarcodeFormat.Itf: zxing = ZXingFormat.ITF; return true;
            case BarcodeFormat.MaxiCode: zxing = ZXingFormat.MAXICODE; return true;
            case BarcodeFormat.Msi: zxing = ZXingFormat.MSI; return true;
            case BarcodeFormat.Pdf417: zxing = ZXingFormat.PDF_417; return true;
            case BarcodeFormat.Pharmacode: zxing = ZXingFormat.PHARMA_CODE; return true;
            case BarcodeFormat.Plessey: zxing = ZXingFormat.PLESSEY; return true;
            case BarcodeFormat.QrCode: zxing = ZXingFormat.QR_CODE; return true;
            case BarcodeFormat.Rss14: zxing = ZXingFormat.RSS_14; return true;
            case BarcodeFormat.RssExpanded: zxing = ZXingFormat.RSS_EXPANDED; return true;
            case BarcodeFormat.UpcA: zxing = ZXingFormat.UPC_A; return true;
            case BarcodeFormat.UpcE: zxing = ZXingFormat.UPC_E; return true;
            case BarcodeFormat.UpcEanExtension: zxing = ZXingFormat.UPC_EAN_EXTENSION; return true;
            default: zxing = default; return false;
        }
    }

    /// <summary>
    /// Projects a caller-supplied format filter onto the decoder's list. Returns <see langword="null"/>
    /// when the filter is null/empty or contains nothing mappable, meaning "try every symbology".
    /// </summary>
    internal static IList<ZXingFormat>? ToZXingList(IReadOnlyList<BarcodeFormat>? formats)
    {
        if (formats is null || formats.Count == 0) return null;

        var mapped = new List<ZXingFormat>(formats.Count);
        foreach (var format in formats)
        {
            if (TryToZXing(format, out var zxing) && !mapped.Contains(zxing))
                mapped.Add(zxing);
        }

        return mapped.Count == 0 ? null : mapped;
    }
}
