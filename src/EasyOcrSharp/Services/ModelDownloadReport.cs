namespace EasyOcrSharp.Services;

/// <summary>
/// Groups of model files for <see cref="EasyOcrModels"/>. Combine with <c>|</c>. Files shared by several
/// sets are fetched once.
/// </summary>
[Flags]
public enum EasyOcrModelSet
{
    /// <summary>No models.</summary>
    None = 0,

    /// <summary>
    /// Everything plain text recognition loads for the requested languages: the CRAFT detector plus each
    /// language's recognizer pack and vocabulary (the int8 variant when
    /// <see cref="EasyOcrServiceOptions.Quantize"/> is set). Languages served by a
    /// <see cref="CustomRecognizer"/>, and the detector when <see cref="EasyOcrServiceOptions.DetectorModelPath"/>
    /// is set, are skipped — they are loaded from disk, never downloaded.
    /// </summary>
    Ocr = 1 << 0,

    /// <summary>
    /// The PP-LCNet page-orientation classifier behind <see cref="Models.PreprocessingOptions.DocumentOrientation"/>
    /// (and, combined with a structure set, <see cref="Models.DocumentAnalysisOptions.DocumentOrientation"/>).
    /// </summary>
    DocumentOrientation = 1 << 1,

    /// <summary>
    /// The UVDoc page-unwarp model behind <see cref="Models.PreprocessingOptions.DocumentUnwarp"/>
    /// (and, combined with a structure set, <see cref="Models.DocumentAnalysisOptions.DocumentUnwarp"/>).
    /// </summary>
    DocumentUnwarp = 1 << 2,

    /// <summary>
    /// The hosted TrOCR handwriting encoder, decoder and tokenizer (int8 unless
    /// <see cref="Models.HandwritingOptions.Quantize"/> is off). Files the service's
    /// <see cref="EasyOcrServiceOptions.Handwriting"/> points at locally are skipped.
    /// </summary>
    Handwriting = 1 << 3,

    /// <summary>
    /// What <c>AnalyzeDocumentAsync</c> loads before any optional recognizer: the PP-DocLayoutV3 layout
    /// detector and labels, the PP-OCRv5 text detector, the text-line orientation classifier, and the
    /// PP-OCRv5 recognizer pack for each requested language (the default Chinese/English/Japanese pack when
    /// none of them maps to a document-analysis pack). Every other structure set implies this one.
    /// </summary>
    StructureCore = 1 << 4,

    /// <summary>The default table-structure model, SLANet_plus (<see cref="Models.DocumentTableModel.SlanetPlus"/>).</summary>
    StructureTables = 1 << 5,

    /// <summary>
    /// The SLANeXt table pipeline (<see cref="Models.DocumentTableModel.SlaNeXt"/>): the wired/wireless table
    /// classifier, SLANeXt_wired and SLANet_plus.
    /// </summary>
    StructureTablesSlaNeXt = 1 << 6,

    /// <summary>The LaTeX-OCR formula recognizer (image resizer, encoder, decoder and tokenizer).</summary>
    StructureFormulas = 1 << 7,

    /// <summary>The PP-OCRv4 seal (curved text) detector. Seal text is read with the <see cref="StructureCore"/> recognizer.</summary>
    StructureSeals = 1 << 8,

    /// <summary>Both page-preprocessing models: <see cref="DocumentOrientation"/> and <see cref="DocumentUnwarp"/>.</summary>
    DocumentPreprocessing = DocumentOrientation | DocumentUnwarp,

    /// <summary>
    /// What <c>AnalyzeDocumentAsync</c> loads with default <see cref="Models.DocumentAnalysisOptions"/>:
    /// <see cref="StructureCore"/>, <see cref="StructureTables"/>, <see cref="StructureFormulas"/> and
    /// <see cref="StructureSeals"/>.
    /// </summary>
    Structure = StructureCore | StructureTables | StructureFormulas | StructureSeals,

    /// <summary>Every set.</summary>
    All = Ocr | DocumentPreprocessing | Handwriting | Structure | StructureTablesSlaNeXt,
}

/// <summary>
/// Outcome for one file in a <see cref="ModelDownloadReport"/>.
/// </summary>
public enum ModelFileStatus
{
    /// <summary>The file was already in the cache; nothing was downloaded.</summary>
    Cached,

    /// <summary>The file was downloaded (and SHA256-verified) into the cache.</summary>
    Downloaded,

    /// <summary>
    /// The file is not in the cache. Reported only by <see cref="EasyOcrModels.CheckCache(IEnumerable{string}, EasyOcrModelSet, EasyOcrServiceOptions?)"/>,
    /// which never downloads.
    /// </summary>
    Missing,

    /// <summary>The file could not be obtained; see <see cref="ModelFileReport.Error"/>.</summary>
    Failed,
}

/// <summary>
/// The outcome for one model file in a <see cref="ModelDownloadReport"/>.
/// </summary>
/// <param name="FileName">The model file name (e.g. <c>craft_mlt_25k.onnx</c>).</param>
/// <param name="Status">Whether the file was cached, downloaded, missing, or failed.</param>
/// <param name="SizeBytes">The file size in bytes; 0 when it is missing or failed.</param>
/// <param name="Path">The absolute path of the file in the cache (where it would be, when it is missing or failed).</param>
/// <param name="Error">The failure, when <paramref name="Status"/> is <see cref="ModelFileStatus.Failed"/>.</param>
public sealed record ModelFileReport(
    string FileName,
    ModelFileStatus Status,
    long SizeBytes,
    string Path,
    Exception? Error = null);

/// <summary>
/// The result of an <see cref="EasyOcrModels"/> pre-download or cache check: one entry per model file.
/// </summary>
public sealed record ModelDownloadReport
{
    /// <summary>
    /// The absolute cache directory of the text-recognition models. Document-analysis models resolve
    /// against the structure engine's cache, which is the same directory unless
    /// <see cref="EasyOcrServiceOptions.ModelCachePath"/> is unset and <c>EASYOCRSHARP_CACHE</c> and
    /// <c>EASYOCRSHARP_STRUCTURE_CACHE</c> differ — each <see cref="ModelFileReport.Path"/> is exact.
    /// </summary>
    public required string CacheDirectory { get; init; }

    /// <summary>One entry per model file, in download order.</summary>
    public required IReadOnlyList<ModelFileReport> Files { get; init; }

    /// <summary>Total time spent.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Number of files that were already cached.</summary>
    public int CachedCount => Files.Count(f => f.Status == ModelFileStatus.Cached);

    /// <summary>Number of files that were downloaded.</summary>
    public int DownloadedCount => Files.Count(f => f.Status == ModelFileStatus.Downloaded);

    /// <summary>Number of files found missing by a cache check.</summary>
    public int MissingCount => Files.Count(f => f.Status == ModelFileStatus.Missing);

    /// <summary>Number of files that could not be obtained.</summary>
    public int FailedCount => Files.Count(f => f.Status == ModelFileStatus.Failed);

    /// <summary>Total size in bytes of the files now in the cache.</summary>
    public long TotalBytes => Files.Sum(f => f.SizeBytes);

    /// <summary>True when every file is present in the cache.</summary>
    public bool IsComplete => Files.All(f => f.Status is ModelFileStatus.Cached or ModelFileStatus.Downloaded);

    /// <summary>
    /// Throws when any file is missing or failed — convenient at the end of a Docker build step or a
    /// deployment script.
    /// </summary>
    /// <exception cref="ModelDownloadException">
    /// One or more files are not in the cache; the first failure, when there is one, is the inner exception.
    /// </exception>
    public void EnsureSuccess()
    {
        var bad = Files.Where(f => f.Status is ModelFileStatus.Failed or ModelFileStatus.Missing).ToList();
        if (bad.Count == 0) return;

        string message = $"{bad.Count} of {Files.Count} model file(s) are not available in '{CacheDirectory}': "
            + string.Join(", ", bad.Select(f => $"{f.FileName} ({f.Status.ToString().ToLowerInvariant()})")) + ".";
        var inner = bad.Select(f => f.Error).FirstOrDefault(e => e is not null);
        throw inner is not null
            ? new ModelDownloadException(message, inner)
            : new ModelDownloadException(message);
    }
}
