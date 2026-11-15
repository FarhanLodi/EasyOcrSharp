using System.Diagnostics;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using OcrDownloadManager = EasyOcrSharp.Internal.ModelDownloadManager;
using OcrModelAsset = EasyOcrSharp.Internal.ModelAsset;
using StructureDownloadManager = EasyOcrSharp.Structure.Engine.ModelDownloadManager;
using StructureModelAsset = EasyOcrSharp.Structure.Engine.ModelAsset;
using StructureRegistry = EasyOcrSharp.Structure.Engine.StructureModelRegistry;

namespace EasyOcrSharp.Services;

/// <summary>
/// Fills the model cache ahead of time — for Docker image builds, air-gapped deployments (download once,
/// then run with <see cref="ModelDownloadOptions.Offline"/>), or simply to keep first-request latency off
/// the hot path — and checks a pre-seeded cache without touching the network.
/// </summary>
/// <remarks>
/// <para>
/// Files are fetched by the same download managers the service uses, into the same directories: the
/// service's <see cref="EasyOcrServiceOptions.ModelCachePath"/> when given, otherwise
/// <c>EASYOCRSHARP_CACHE</c> (document-analysis models: <c>EASYOCRSHARP_STRUCTURE_CACHE</c>), otherwise
/// <c>%LOCALAPPDATA%/EasyOcrSharp/models</c>. Every download is SHA256-verified fail-closed, resumes a
/// partial file, retries transient failures, and honours <see cref="ModelDownloadOptions.BaseUrlOverride"/>
/// / <c>EASYOCRSHARP_MODEL_BASE_URL</c> exactly as the service does (document-analysis models use
/// <c>EASYOCRSHARP_STRUCTURE_MODEL_BASE_URL</c>, as <c>AnalyzeDocumentAsync</c> does).
/// </para>
/// <para>
/// Every file is attempted and failures are reported per file rather than thrown — call
/// <see cref="ModelDownloadReport.EnsureSuccess"/> to fail the build step:
/// <code>
/// var report = await EasyOcrModels.DownloadAsync(["en", "de"], EasyOcrModelSet.Ocr | EasyOcrModelSet.Structure);
/// report.EnsureSuccess();
/// </code>
/// </para>
/// </remarks>
public static class EasyOcrModels
{
    private const EasyOcrModelSet StructureSets =
        EasyOcrModelSet.StructureCore | EasyOcrModelSet.StructureTables | EasyOcrModelSet.StructureTablesSlaNeXt
        | EasyOcrModelSet.StructureFormulas | EasyOcrModelSet.StructureSeals;

    /// <summary>
    /// Returns the absolute text-recognition model cache directory a service built with
    /// <paramref name="options"/> uses (the default cache when <paramref name="options"/> or its
    /// <see cref="EasyOcrServiceOptions.ModelCachePath"/> is <see langword="null"/>). The directory is not created.
    /// </summary>
    /// <param name="options">The service options whose cache path to resolve, or <see langword="null"/> for the default.</param>
    public static string GetCacheDirectory(EasyOcrServiceOptions? options = null)
        => OcrDownloadManager.ResolveCacheRoot(NormalizeCachePath(options?.ModelCachePath));

    /// <summary>
    /// Lists the model file names <see cref="DownloadAsync(IEnumerable{string}, EasyOcrModelSet, ModelDownloadOptions?, IProgress{ModelDownloadProgress}?, CancellationToken)"/>
    /// would fetch, without touching the network or the disk — handy for building a mirror or a Dockerfile.
    /// </summary>
    /// <param name="languages">EasyOCR language codes (e.g. <c>"en"</c>, <c>"ch_sim"</c>), as passed to the OCR calls.</param>
    /// <param name="sets">The model sets to include. Defaults to <see cref="EasyOcrModelSet.Ocr"/>.</param>
    /// <param name="serviceOptions">
    /// Optional service options: quantized recognizers, custom recognizers, a local detector and local
    /// handwriting files are honoured exactly as the service would.
    /// </param>
    /// <returns>The distinct file names, in download order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="languages"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="EasyOcrModelSet.Ocr"/> is requested and a language has no recognizer.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sets"/> contains undefined flags.</exception>
    public static IReadOnlyList<string> GetModelFileNames(
        IEnumerable<string> languages,
        EasyOcrModelSet sets = EasyOcrModelSet.Ocr,
        EasyOcrServiceOptions? serviceOptions = null)
        => Plan(languages, sets, serviceOptions).Select(p => p.FileName).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Ensures the model files for <paramref name="languages"/> and <paramref name="sets"/> are in the default
    /// model cache, downloading any that are missing.
    /// </summary>
    /// <param name="languages">EasyOCR language codes (e.g. <c>"en"</c>, <c>"ch_sim"</c>), as passed to the OCR calls.</param>
    /// <param name="sets">The model sets to fetch. Defaults to <see cref="EasyOcrModelSet.Ocr"/>.</param>
    /// <param name="options">Download behaviour (retries, proxy, mirror, checksum policy, offline). <see langword="null"/> uses the defaults. Never mutated.</param>
    /// <param name="progress">Optional progress sink: byte progress during downloads, plus one completed report per already-cached file.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One entry per file with its status, size and path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="languages"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="EasyOcrModelSet.Ocr"/> is requested and a language has no recognizer.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sets"/> contains undefined flags.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static Task<ModelDownloadReport> DownloadAsync(
        IEnumerable<string> languages,
        EasyOcrModelSet sets = EasyOcrModelSet.Ocr,
        ModelDownloadOptions? options = null,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = Plan(languages, sets, serviceOptions: null);
        return DownloadCoreAsync(plan, cachePath: null, options ?? new ModelDownloadOptions(), progress, cancellationToken);
    }

    /// <summary>
    /// Ensures the model files a service configured with <paramref name="serviceOptions"/> needs are in that
    /// service's cache: its <see cref="EasyOcrServiceOptions.ModelCachePath"/>,
    /// <see cref="EasyOcrServiceOptions.Download"/> options, quantized and custom recognizers, local detector
    /// and local handwriting files are all honoured.
    /// </summary>
    /// <param name="serviceOptions">The options the OCR service will be built with.</param>
    /// <param name="languages">EasyOCR language codes, as passed to the OCR calls.</param>
    /// <param name="sets">The model sets to fetch. Defaults to <see cref="EasyOcrModelSet.Ocr"/>.</param>
    /// <param name="progress">Optional progress sink: byte progress during downloads, plus one completed report per already-cached file.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One entry per file with its status, size and path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serviceOptions"/> or <paramref name="languages"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="EasyOcrModelSet.Ocr"/> is requested and a language has no recognizer.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sets"/> contains undefined flags.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public static Task<ModelDownloadReport> DownloadAsync(
        EasyOcrServiceOptions serviceOptions,
        IEnumerable<string> languages,
        EasyOcrModelSet sets = EasyOcrModelSet.Ocr,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceOptions);
        var plan = Plan(languages, sets, serviceOptions);
        return DownloadCoreAsync(
            plan, NormalizeCachePath(serviceOptions.ModelCachePath), serviceOptions.Download, progress, cancellationToken);
    }

    /// <summary>
    /// Report-only: checks whether the files for <paramref name="languages"/> and <paramref name="sets"/> are
    /// already in the cache, without downloading, creating directories or touching the network. Absent files
    /// are reported as <see cref="ModelFileStatus.Missing"/>, so <see cref="ModelDownloadReport.EnsureSuccess"/>
    /// verifies a pre-seeded air-gapped cache.
    /// </summary>
    /// <param name="languages">EasyOCR language codes, as passed to the OCR calls.</param>
    /// <param name="sets">The model sets to check. Defaults to <see cref="EasyOcrModelSet.Ocr"/>.</param>
    /// <param name="serviceOptions">Optional service options (cache path, quantized/custom recognizers, local files).</param>
    /// <returns>One entry per file, each <see cref="ModelFileStatus.Cached"/> or <see cref="ModelFileStatus.Missing"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="languages"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="EasyOcrModelSet.Ocr"/> is requested and a language has no recognizer.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sets"/> contains undefined flags.</exception>
    public static ModelDownloadReport CheckCache(
        IEnumerable<string> languages,
        EasyOcrModelSet sets = EasyOcrModelSet.Ocr,
        EasyOcrServiceOptions? serviceOptions = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var plan = Plan(languages, sets, serviceOptions);
        var cachePath = NormalizeCachePath(serviceOptions?.ModelCachePath);
        var roots = new CacheRoots(cachePath);

        var files = new List<ModelFileReport>(plan.Count);
        var seen = new HashSet<string>(PathComparer);
        foreach (var item in plan)
        {
            string path = roots.PathOf(item);
            if (!seen.Add(path)) continue;
            var info = new FileInfo(path);
            files.Add(info.Exists
                ? new ModelFileReport(item.FileName, ModelFileStatus.Cached, info.Length, path)
                : new ModelFileReport(item.FileName, ModelFileStatus.Missing, 0, path));
        }

        return new ModelDownloadReport
        {
            CacheDirectory = roots.Ocr,
            Files = files,
            Duration = stopwatch.Elapsed,
        };
    }

    // ---- planning ----

    /// <summary>One file to obtain, from either the text-recognition or the structure registry.</summary>
    internal sealed record PlannedModel(string FileName, OcrModelAsset? OcrAsset, StructureModelAsset? StructureAsset)
    {
        public bool IsStructure => StructureAsset is not null;
    }

    /// <summary>Resolves the registry assets for the requested languages and sets, in download order.</summary>
    internal static IReadOnlyList<PlannedModel> Plan(
        IEnumerable<string> languages,
        EasyOcrModelSet sets,
        EasyOcrServiceOptions? serviceOptions)
    {
        ArgumentNullException.ThrowIfNull(languages);
        if ((sets & ~EasyOcrModelSet.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(sets), sets, "The model set contains undefined flags.");

        // Same normalization the service applies to its languages argument.
        var langs = languages
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var plan = new List<PlannedModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddOcr(OcrModelAsset asset)
        {
            if (seen.Add("o:" + asset.FileName)) plan.Add(new PlannedModel(asset.FileName, asset, null));
        }
        void AddStructure(StructureModelAsset asset)
        {
            if (seen.Add("s:" + asset.FileName)) plan.Add(new PlannedModel(asset.FileName, null, asset));
        }

        if ((sets & EasyOcrModelSet.Ocr) != 0)
        {
            if (string.IsNullOrWhiteSpace(serviceOptions?.DetectorModelPath))
            {
                AddOcr(ModelRegistry.Detector);
            }

            var custom = new HashSet<string>(
                serviceOptions?.CustomRecognizers.SelectMany(c => c.Languages ?? Array.Empty<string>()) ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var unsupported = new List<string>();
            foreach (var lang in langs)
            {
                if (custom.Contains(lang)) continue;
                var def = ModelRegistry.FindByLanguage(lang);
                if (def is null)
                {
                    unsupported.Add(lang);
                    continue;
                }
                AddOcr(serviceOptions?.Quantize == true ? ModelRegistry.QuantizedModel(def) : def.Model);
                AddOcr(def.Vocab);
            }

            if (unsupported.Count > 0)
            {
                throw new ArgumentException(
                    $"No built-in or custom recognizer handles language(s): {string.Join(", ", unsupported)}.",
                    nameof(languages));
            }
        }

        if ((sets & EasyOcrModelSet.DocumentOrientation) != 0) AddOcr(DocPreprocessModelRegistry.DocOrientationClassifier);
        if ((sets & EasyOcrModelSet.DocumentUnwarp) != 0) AddOcr(DocPreprocessModelRegistry.DocUnwarp);

        if ((sets & EasyOcrModelSet.Handwriting) != 0)
        {
            var handwriting = serviceOptions?.Handwriting ?? HandwritingOptions.Default;
            if (string.IsNullOrWhiteSpace(handwriting.EncoderModelPath)) AddOcr(ModelRegistry.TrOcrEncoder(handwriting.Quantize));
            if (string.IsNullOrWhiteSpace(handwriting.DecoderModelPath)) AddOcr(ModelRegistry.TrOcrDecoder(handwriting.Quantize));
            if (string.IsNullOrWhiteSpace(handwriting.TokenizerPath)) AddOcr(ModelRegistry.TrOcrVocab);
        }

        if ((sets & StructureSets) != 0)
        {
            // Analysis always runs layout + whole-page text recognition, whatever else is enabled.
            AddStructure(StructureRegistry.DocLayoutV3);
            AddStructure(StructureRegistry.DocLayoutV3Labels);
            AddStructure(StructureRegistry.MobileDetector);
            AddStructure(StructureRegistry.TextLineOrientationClassifier);
            foreach (var pack in ResolveStructurePacks(langs))
            {
                AddStructure(pack.Model);
                AddStructure(pack.Dictionary);
            }

            if ((sets & EasyOcrModelSet.StructureTables) != 0)
            {
                AddStructure(StructureRegistry.SlanetPlus);
            }

            if ((sets & EasyOcrModelSet.StructureTablesSlaNeXt) != 0)
            {
                AddStructure(StructureRegistry.TableClassifier);
                AddStructure(StructureRegistry.SlaNeXtWired);
                AddStructure(StructureRegistry.SlanetPlus);
            }

            if ((sets & EasyOcrModelSet.StructureFormulas) != 0)
            {
                AddStructure(StructureRegistry.FormulaImageResizer);
                AddStructure(StructureRegistry.FormulaEncoder);
                AddStructure(StructureRegistry.FormulaDecoder);
                AddStructure(StructureRegistry.FormulaTokenizer);
            }

            if ((sets & EasyOcrModelSet.StructureSeals) != 0)
            {
                AddStructure(StructureRegistry.SealDetector);
            }

            // Analysis runs its own page pre-processing through the structure cache.
            if ((sets & EasyOcrModelSet.DocumentOrientation) != 0) AddStructure(StructureRegistry.DocImageOrientation);
            if ((sets & EasyOcrModelSet.DocumentUnwarp) != 0) AddStructure(StructureRegistry.DocUnwarp);
        }

        return plan;
    }

    /// <summary>
    /// The document-analysis recognizer packs for the languages, mapped exactly as
    /// <c>AnalyzeDocumentAsync</c> maps <see cref="DocumentAnalysisOptions.Languages"/> (unknown codes are
    /// skipped; none left means the structure engine's default language).
    /// </summary>
    private static IEnumerable<EasyOcrSharp.Structure.Engine.RecognizerPack> ResolveStructurePacks(string[] langs)
    {
        var structureOptions = EasyOcrService.ToStructureOptions(new DocumentAnalysisOptions { Languages = langs }, logger: null);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in structureOptions.Languages)
        {
            var pack = StructureRegistry.FindByLanguage(
                EasyOcrSharp.Structure.Engine.Models.OcrLanguageExtensions.ToCode(language));
            if (pack is not null && names.Add(pack.Name)) yield return pack;
        }

        if (names.Count == 0) yield return StructureRegistry.MobileRecognizer;
    }

    // ---- download ----

    /// <summary>Resolves where each planned file lives, mirroring how the service configures both engines.</summary>
    private sealed class CacheRoots(string? cachePath)
    {
        public string Ocr { get; } = OcrDownloadManager.ResolveCacheRoot(cachePath);

        public string Structure { get; } = StructureDownloadManager.ResolveCacheRoot(cachePath);

        public string PathOf(PlannedModel item) => Path.Combine(item.IsStructure ? Structure : Ocr, item.FileName);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static async Task<ModelDownloadReport> DownloadCoreAsync(
        IReadOnlyList<PlannedModel> plan,
        string? cachePath,
        ModelDownloadOptions options,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var roots = new CacheRoots(cachePath);
        var effectiveOptions = progress is null ? options : WithProgress(options, progress);
        var structureOptions = plan.Any(p => p.IsStructure) ? EasyOcrService.MapDownload(effectiveOptions) : null;
        var stopwatch = Stopwatch.StartNew();

        var files = new List<ModelFileReport>(plan.Count);
        var seen = new HashSet<string>(PathComparer);
        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string expectedPath = roots.PathOf(item);
            if (!seen.Add(expectedPath)) continue;

            bool wasCached = File.Exists(expectedPath);
            try
            {
                string path = item.StructureAsset is { } structureAsset
                    ? await StructureDownloadManager
                        .EnsureModelAsync(structureAsset, cachePath, structureOptions!, logger: null, cancellationToken)
                        .ConfigureAwait(false)
                    : await OcrDownloadManager
                        .EnsureModelAsync(item.OcrAsset!, cachePath, effectiveOptions, logger: null, cancellationToken)
                        .ConfigureAwait(false);
                long size = new FileInfo(path).Length;
                if (wasCached) progress?.Report(new ModelDownloadProgress(item.FileName, size, size));
                files.Add(new ModelFileReport(item.FileName, wasCached ? ModelFileStatus.Cached : ModelFileStatus.Downloaded, size, path));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                files.Add(new ModelFileReport(item.FileName, ModelFileStatus.Failed, 0, expectedPath, ToPublicException(ex)));
            }
        }

        return new ModelDownloadReport
        {
            CacheDirectory = roots.Ocr,
            Files = files,
            Duration = stopwatch.Elapsed,
        };
    }

    /// <summary>
    /// The structure engine's download exceptions are internal types; restate them as the public ones the
    /// text-recognition path throws, so callers can catch one family whichever engine a file belongs to.
    /// </summary>
    private static Exception ToPublicException(Exception ex) => ex switch
    {
        EasyOcrSharp.Structure.Engine.OfflineModelMissingException => new OfflineModelMissingException(ex.Message),
        EasyOcrSharp.Structure.Engine.ModelChecksumException => new ModelChecksumException(ex.Message),
        EasyOcrSharp.Structure.Engine.ModelDownloadException => new ModelDownloadException(ex.Message, ex),
        _ => ex,
    };

    private static string? NormalizeCachePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    /// <summary>Copies the caller's options (never mutated) with the progress sink added alongside any existing one.</summary>
    private static ModelDownloadOptions WithProgress(ModelDownloadOptions options, IProgress<ModelDownloadProgress> progress)
        => new()
        {
            MaxDownloadBytes = options.MaxDownloadBytes,
            MaxRetries = options.MaxRetries,
            RetryBaseDelay = options.RetryBaseDelay,
            Offline = options.Offline,
            HttpClientFactory = options.HttpClientFactory,
            BaseUrlOverride = options.BaseUrlOverride,
            AllowInsecureModelSource = options.AllowInsecureModelSource,
            AllowUnverifiedModels = options.AllowUnverifiedModels,
            Progress = options.Progress is { } existing ? new TeeProgress(existing, progress) : progress,
        };

    private sealed class TeeProgress(IProgress<ModelDownloadProgress> first, IProgress<ModelDownloadProgress> second)
        : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value)
        {
            first.Report(value);
            second.Report(value);
        }
    }
}
