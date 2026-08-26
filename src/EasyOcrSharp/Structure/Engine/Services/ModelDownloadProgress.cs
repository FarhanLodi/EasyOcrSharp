using System.Net.Http;

namespace EasyOcrSharp.Structure.Engine.Services;

/// <summary>
/// Progress for a single model-file download, reported via <see cref="ModelDownloadOptions.Progress"/>.
/// </summary>
/// <param name="FileName">The asset being downloaded (e.g. <c>en_PP-OCRv4_rec_infer.onnx</c>).</param>
/// <param name="BytesDownloaded">Bytes received so far (including any resumed prefix).</param>
/// <param name="TotalBytes">Total size in bytes, or <c>-1</c> if the server didn't report it.</param>
internal readonly record struct ModelDownloadProgress(string FileName, long BytesDownloaded, long TotalBytes)
{
    /// <summary>
    /// Completion fraction (0–1), or <c>null</c> when the total size is unknown.
    /// </summary>
    public double? Fraction => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes : null;
}
