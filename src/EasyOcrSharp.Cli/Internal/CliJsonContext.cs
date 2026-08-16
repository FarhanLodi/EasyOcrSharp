using System.Text.Json.Serialization;
using EasyOcrSharp.Models;

namespace EasyOcrSharp.Cli.Internal;

/// <summary>
/// One entry of the CLI's JSON output. A scan of ten files (or a ten-page PDF) emits an array of
/// these, so a consumer can always correlate a result with the file — and the page — it came from.
/// A failed input is reported with <see cref="Error"/> set instead of being dropped, so the JSON is a
/// complete record of the run.
/// </summary>
internal sealed record CliScanReport
{
    /// <summary>Path of the file this entry is for, as it was resolved on disk.</summary>
    public required string Source { get; init; }

    /// <summary>1-based page number for PDF input; null for single-image input.</summary>
    public int? Page { get; init; }

    /// <summary>The OCR result, or null when the input failed.</summary>
    public OcrResult? Result { get; init; }

    /// <summary>The failure message, or null when the input succeeded.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Machine-readable form of <c>easyocrsharp info</c>, emitted by <c>info --json</c> so provisioning
/// scripts can assert on the execution provider or the cache location without scraping text.
/// </summary>
internal sealed record CliInfoReport
{
    /// <summary>Version of the CLI tool package.</summary>
    public required string CliVersion { get; init; }

    /// <summary>Version of the EasyOcrSharp library in use.</summary>
    public required string LibraryVersion { get; init; }

    /// <summary>The .NET runtime the tool is running on.</summary>
    public required string Runtime { get; init; }

    /// <summary>Human-readable operating-system description.</summary>
    public required string OsDescription { get; init; }

    /// <summary>Process architecture (x64, arm64, …).</summary>
    public required string Architecture { get; init; }

    /// <summary>The execution provider the service resolved to for this host.</summary>
    public required string ExecutionProvider { get; init; }

    /// <summary>Execution providers compiled into the loaded ONNX Runtime.</summary>
    public required IReadOnlyList<string> AvailableProviders { get; init; }

    /// <summary>Advice when a GPU is present but unused; null when there is nothing to say.</summary>
    public string? GpuHint { get; init; }

    /// <summary>Directory ONNX models are cached in.</summary>
    public required string ModelCachePath { get; init; }

    /// <summary>Number of files currently in the model cache.</summary>
    public required int CachedFileCount { get; init; }

    /// <summary>Total size of the model cache in bytes.</summary>
    public required long CachedBytes { get; init; }
}

/// <summary>
/// Source-generated JSON for the CLI's own output shapes — the tool ships AOT-friendly, so no
/// reflection-based serialization is used anywhere.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliScanReport))]
[JsonSerializable(typeof(CliScanReport[]))]
[JsonSerializable(typeof(CliInfoReport))]
internal sealed partial class CliJsonContext : JsonSerializerContext
{
}
