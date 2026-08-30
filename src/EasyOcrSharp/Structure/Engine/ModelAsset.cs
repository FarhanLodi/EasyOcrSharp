using System.Collections.Frozen;

namespace EasyOcrSharp.Structure.Engine;

/// <summary>
/// Defines a downloadable model asset (an ONNX network or a character dictionary file).
/// </summary>
/// <param name="FileName">The single-segment file name cached on disk (e.g. <c>PP-OCRv5_mobile_rec.onnx</c>).</param>
/// <param name="Url">The absolute HTTPS URL the asset is fetched from when not cached.</param>
/// <param name="Sha256">
/// Expected upper-case-hex SHA256, or <c>null</c> when no checksum is published yet. A <c>null</c>
/// checksum makes <see cref="ModelDownloadManager"/> take its fail-closed path: the asset is only accepted
/// when <see cref="Services.ModelDownloadOptions.AllowUnverifiedModels"/> is set, otherwise the download
/// is rejected. <see cref="StructureModelRegistry.Checksums"/> is fully populated, so every structure
/// model shipped by this library is verified on download.
/// </param>
internal sealed record ModelAsset(string FileName, string Url, string? Sha256);
