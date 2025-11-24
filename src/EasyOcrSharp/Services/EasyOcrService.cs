using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using Microsoft.Extensions.Logging;
using Python.Runtime;
using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace EasyOcrSharp.Services;

/// <summary>
/// Provides a high-level OCR service backed by the EasyOCR Python library.
/// </summary>
public sealed class EasyOcrService : IAsyncDisposable, IDisposable
{
    private readonly ILogger<EasyOcrService>? _logger;
    private readonly string? _modelCachePath;
    private readonly ConcurrentDictionary<string, Lazy<Task<PyObject>>> _readerCache = new(StringComparer.OrdinalIgnoreCase);
    private bool? _gpuAvailable;

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="EasyOcrService"/> class.
    /// </summary>
    /// <param name="modelCachePath">Optional path where language models should be cached. If not provided, uses LocalAppData\EasyOcrSharp\models by default.</param>
    /// <param name="logger">Optional logger instance for diagnostic messages.</param>
    public EasyOcrService(string? modelCachePath = null, ILogger<EasyOcrService>? logger = null)
    {
        _logger = logger;
        _modelCachePath = string.IsNullOrWhiteSpace(modelCachePath) ? null : Path.GetFullPath(modelCachePath);
    }

    /// <summary>
    /// Performs OCR on the specified image file path using the specified languages.
    /// Languages that require English (ch_sim, ch_tra, zh_sim, zh_tra, ja, ko, th) will automatically include English.
    /// GPU will be automatically detected and used if available.
    /// </summary>
    /// <param name="imagePath">The absolute or relative path to the image file.</param>
    /// <param name="languages">List of language codes to use for OCR.</param>
    /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
    /// <returns>An <see cref="OcrResult"/> containing extracted text and metadata.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="imagePath"/> is null or whitespace, or when <paramref name="languages"/> is null or empty.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file cannot be located.</exception>
    public async Task<OcrResult> ExtractTextFromImage(
        string imagePath,
        IEnumerable<string> languages,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();

        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new ArgumentException("Image path must be provided.", nameof(imagePath));
        }

        if (languages == null || !languages.Any())
        {
            throw new ArgumentException("At least one language must be specified.", nameof(languages));
        }

        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The image file '{fullPath}' could not be found.", fullPath);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var resolvedLanguages = languages
            .Where(lang => !string.IsNullOrWhiteSpace(lang))
            .Select(lang => lang.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (resolvedLanguages.Length == 0)
        {
            throw new ArgumentException("At least one valid language must be specified.", nameof(languages));
        }

        var stopwatch = Stopwatch.StartNew();

        await ModelDownloadManager.EnsureModelsAvailableAsync(resolvedLanguages, _modelCachePath, _logger, cancellationToken).ConfigureAwait(false);

        await PythonInitializer.EnsureInitializedAsync(_logger, cancellationToken).ConfigureAwait(false);

        var useGpu = await DetectGpuAvailabilityAsync(cancellationToken).ConfigureAwait(false);

        var allLines = new List<OcrLine>();
        var allLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var languageGroups = GroupLanguagesForMultilingualOcr(resolvedLanguages);

        _logger?.LogInformation(
            "Processing {GroupCount} language group(s) in parallel: {Groups}",
            languageGroups.Length,
            string.Join("; ", languageGroups.Select(g => $"[{string.Join(", ", g)}]")));

        var tasks = languageGroups.Select(async languageGroup =>
        {
            try
            {
                var reader = await GetOrCreateReaderAsync(languageGroup, useGpu, cancellationToken).ConfigureAwait(false);

                OcrResult groupResult;
        using (Py.GIL())
        {
            using var pythonResult = InvokeReader(reader, fullPath);
                    groupResult = ConvertToResult(pythonResult, languageGroup, useGpu, TimeSpan.Zero);
                }

                _logger?.LogDebug(
                    "OCR completed for language group [{Languages}]: {LineCount} lines detected",
                    string.Join(", ", languageGroup),
                    groupResult.Lines.Count);

                return (Success: true, LanguageGroup: languageGroup, OcrResult: groupResult, Exception: (Exception?)null);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, 
                    "Failed to process language group [{Languages}], continuing with other groups",
                    string.Join(", ", languageGroup));
                return (Success: false, LanguageGroup: languageGroup, OcrResult: (OcrResult?)null, Exception: ex);
            }
        }).ToArray();

        var taskResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var (success, languageGroup, ocrResult, exception) in taskResults)
        {
            if (success && ocrResult != null)
            {
                allLines.AddRange(ocrResult.Lines);
                
                foreach (var lang in ocrResult.Languages)
                {
                    allLanguages.Add(lang);
                }
            }
        }

        var mergedLines = MergeAndSortOcrLines(allLines);

        var textBuilder = new StringBuilder();
        foreach (var line in mergedLines)
        {
            if (!string.IsNullOrEmpty(line.Text))
            {
                if (textBuilder.Length > 0)
                {
                    textBuilder.AppendLine();
                }
                textBuilder.Append(line.Text);
            }
        }

        stopwatch.Stop();

        var result = new OcrResult
        {
            FullText = textBuilder.ToString(),
            Lines = mergedLines,
            Languages = allLanguages.ToArray(),
            Duration = stopwatch.Elapsed,
            UsedGpu = useGpu
        };

        _logger?.LogInformation(
            "Multilingual OCR completed: {TotalLines} lines detected from {LanguageGroupCount} language group(s) in {Duration}ms",
            mergedLines.Count,
            languageGroups.Length,
            stopwatch.Elapsed.TotalMilliseconds);

        return result;
    }

    /// <summary>
    /// Performs OCR on an image stream by persisting it temporarily to disk using the specified languages.
    /// Languages that require English (ch_sim, ch_tra, zh_sim, zh_tra, ja, ko, th) will automatically include English.
    /// GPU will be automatically detected and used if available.
    /// </summary>
    /// <param name="imageStream">The image stream.</param>
    /// <param name="languages">List of language codes to use for OCR.</param>
    /// <param name="fileNameHint">Optional file name hint (used for temp file extension).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="OcrResult"/> containing extracted text and metadata.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="languages"/> is null or empty.</exception>
    public async Task<OcrResult> ExtractTextFromImage(
        Stream imageStream,
        IEnumerable<string> languages,
        string? fileNameHint = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();

        if (imageStream is null)
        {
            throw new ArgumentNullException(nameof(imageStream));
        }

        var extension = Path.GetExtension(fileNameHint);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".tmp";
        }

        var tempFilePath = Path.Combine(Path.GetTempPath(), $"easyocrsharp_{Guid.NewGuid():N}{extension}");

        try
        {
            await using (var fileStream = new FileStream(tempFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                imageStream.Position = 0;
                await imageStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return await ExtractTextFromImage(tempFilePath, languages, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(tempFilePath);
        }
    }

    /// <summary>
    /// Releases all resources used by the <see cref="EasyOcrService"/>.
    /// </summary>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Asynchronously releases all resources used by the <see cref="EasyOcrService"/>.
    /// </summary>
    /// <returns>A task that represents the asynchronous dispose operation.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var entry in _readerCache.ToArray())
        {
            if (!entry.Value.IsValueCreated)
            {
                continue;
            }

            try
            {
                var reader = await entry.Value.Value.ConfigureAwait(false);
                using (Py.GIL())
                {
                    reader.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to dispose EasyOCR reader for cache key '{Key}'.", entry.Key);
            }
        }

        _readerCache.Clear();
        _disposed = true;
    }

    private async Task<bool> DetectGpuAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (_gpuAvailable.HasValue)
        {
            return _gpuAvailable.Value;
        }

        return await Task.Run(() =>
        {
            try
            {
                using (Py.GIL())
                {
                    using var torch = Py.Import("torch");
                    using var cudaAvailable = torch.GetAttr("cuda");
                    using var isAvailable = cudaAvailable.GetAttr("is_available");
                    var available = isAvailable.Invoke();
                    var gpuAvailable = available.As<bool>();

                    _gpuAvailable = gpuAvailable;

                    if (gpuAvailable)
                    {
                        _logger?.LogInformation("GPU detected and will be used for OCR processing.");
                    }
                    else
                    {
                        _logger?.LogInformation("No GPU detected. Using CPU for OCR processing.");
                    }

                    return gpuAvailable;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to check GPU availability. Falling back to CPU.");
                _gpuAvailable = false;
                return false;
            }
        }, cancellationToken);
    }

    private async Task<PyObject> GetOrCreateReaderAsync(string[] languages, bool useGpu, CancellationToken cancellationToken)
    {
        languages = FixLanguageDependencies(languages);
        var cacheKey = BuildCacheKey(languages, useGpu);

        while (true)
        {
            if (_readerCache.TryGetValue(cacheKey, out var existing))
            {
                try
                {
                    return await existing.Value.ConfigureAwait(false);
                }
                catch
                {
                    _readerCache.TryRemove(cacheKey, out _);
                }
            }

            var lazyReader = new Lazy<Task<PyObject>>(() => CreateReaderAsync(languages, useGpu, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication);
            if (_readerCache.TryAdd(cacheKey, lazyReader))
            {
                return await lazyReader.Value.ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Groups languages for multilingual OCR processing.
    /// Separates languages into groups that work well together for better accuracy.
    /// Each group will be processed separately and results will be merged.
    /// </summary>
    private string[][] GroupLanguagesForMultilingualOcr(string[] languages)
    {
        var groups = new List<string[]>();
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (languages.Contains("ar", StringComparer.OrdinalIgnoreCase))
        {
            groups.Add(new[] { "ar", "fa", "ur", "ug", "en" });
            processed.Add("ar");
            processed.Add("fa");
            processed.Add("ur");
            processed.Add("ug");
        }

        var englishOnlyLangs = new[] { "th", "ch_tra" };
        foreach (var lang in englishOnlyLangs)
        {
            if (languages.Contains(lang, StringComparer.OrdinalIgnoreCase) && !processed.Contains(lang))
            {
                groups.Add(new[] { lang, "en" });
                processed.Add(lang);
            }
        }

        var englishRequiredLangs = new[] { "ch_sim", "zh_sim", "zh_tra", "ja", "ko" };
        var englishRequiredGroup = new List<string> { "en" };
        foreach (var lang in englishRequiredLangs)
        {
            if (languages.Contains(lang, StringComparer.OrdinalIgnoreCase) && !processed.Contains(lang))
            {
                englishRequiredGroup.Add(lang);
                processed.Add(lang);
            }
        }
        if (englishRequiredGroup.Count > 1)
        {
            groups.Add(englishRequiredGroup.ToArray());
        }

        var otherLangs = languages
            .Where(l => !processed.Contains(l))
            .ToArray();

        var nonLatinScripts = new[] { "hi", "bn", "te", "ta", "mr", "gu", "kn", "ml", "ne", "pa", "si" };
        var latinScripts = new List<string>();
        
        foreach (var lang in otherLangs)
        {
            if (nonLatinScripts.Contains(lang, StringComparer.OrdinalIgnoreCase))
            {
                groups.Add(new[] { lang, "en" });
                processed.Add(lang);
            }
            else
            {
                latinScripts.Add(lang);
            }
        }

        if (latinScripts.Count > 0)
        {
            if (!latinScripts.Contains("en", StringComparer.OrdinalIgnoreCase) && 
                !processed.Contains("en", StringComparer.OrdinalIgnoreCase))
            {
                latinScripts.Add("en");
            }
            groups.Add(latinScripts.ToArray());
        }

        if (groups.Count == 0)
        {
            groups.Add(languages);
        }

        return groups.ToArray();
    }

    /// <summary>
    /// Merges OCR lines from multiple language groups, removes duplicates, and sorts by position.
    /// </summary>
    private List<OcrLine> MergeAndSortOcrLines(List<OcrLine> allLines)
    {
        if (allLines.Count == 0)
        {
            return allLines;
        }

        var uniqueLines = new List<OcrLine>();
        var processedBoxes = new HashSet<string>();

        foreach (var line in allLines)
        {
            if (string.IsNullOrWhiteSpace(line.Text))
            {
                continue;
            }

            var box = line.BoundingBox;
            var boxKey = $"{Math.Round(box.MinX, 1)},{Math.Round(box.MinY, 1)},{Math.Round(box.MaxX, 1)},{Math.Round(box.MaxY, 1)}";

            if (processedBoxes.Contains(boxKey))
            {
                var existing = uniqueLines.FirstOrDefault(l =>
                {
                    var existingBox = l.BoundingBox;
                    var existingKey = $"{Math.Round(existingBox.MinX, 1)},{Math.Round(existingBox.MinY, 1)},{Math.Round(existingBox.MaxX, 1)},{Math.Round(existingBox.MaxY, 1)}";
                    return existingKey == boxKey;
                });

                if (existing != null && line.Confidence > existing.Confidence)
                {
                    uniqueLines.Remove(existing);
                    uniqueLines.Add(line);
                }
                continue;
            }

            var isDuplicate = false;
            foreach (var existing in uniqueLines)
            {
                var overlap = CalculateBoundingBoxOverlap(box, existing.BoundingBox);
                var textSimilarity = CalculateTextSimilarity(line.Text, existing.Text);
                
                if (overlap > 0.7 && textSimilarity > 0.8)
                {
                    if (line.Confidence > existing.Confidence)
                    {
                        uniqueLines.Remove(existing);
                        uniqueLines.Add(line);
                    }
                    isDuplicate = true;
                    break;
                }
            }

            if (!isDuplicate)
            {
                uniqueLines.Add(line);
                processedBoxes.Add(boxKey);
            }
        }

        const double yTolerance = 10.0;

        var sortedLines = uniqueLines
            .OrderBy(line => Math.Round(line.BoundingBox.MinY / yTolerance) * yTolerance)
            .ThenBy(line => line.BoundingBox.MinX)
            .ToList();

        return sortedLines;
    }

    /// <summary>
    /// Calculates the Intersection over Union (IOU) of two bounding boxes.
    /// Returns a value between 0 and 1, where 1 means complete overlap.
    /// </summary>
    private static double CalculateBoundingBoxOverlap(OcrBoundingBox box1, OcrBoundingBox box2)
    {
        if (box1.IsEmpty || box2.IsEmpty)
        {
            return 0.0;
        }

        var intersectionMinX = Math.Max(box1.MinX, box2.MinX);
        var intersectionMinY = Math.Max(box1.MinY, box2.MinY);
        var intersectionMaxX = Math.Min(box1.MaxX, box2.MaxX);
        var intersectionMaxY = Math.Min(box1.MaxY, box2.MaxY);

        if (intersectionMaxX <= intersectionMinX || intersectionMaxY <= intersectionMinY)
        {
            return 0.0;
        }

        var intersectionArea = (intersectionMaxX - intersectionMinX) * (intersectionMaxY - intersectionMinY);
        var box1Area = box1.Width * box1.Height;
        var box2Area = box2.Width * box2.Height;
        var unionArea = box1Area + box2Area - intersectionArea;

        if (unionArea <= 0)
        {
            return 0.0;
        }

            return intersectionArea / unionArea;
    }

    /// <summary>
    /// Calculates text similarity using Levenshtein distance ratio.
    /// Returns a value between 0 and 1, where 1 means identical text.
    /// </summary>
    private static double CalculateTextSimilarity(string text1, string text2)
    {
        if (string.IsNullOrEmpty(text1) && string.IsNullOrEmpty(text2))
        {
            return 1.0;
        }

        if (string.IsNullOrEmpty(text1) || string.IsNullOrEmpty(text2))
        {
            return 0.0;
        }

        if (text1.Equals(text2, StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }

        var normalized1 = text1.Trim().ToLowerInvariant();
        var normalized2 = text2.Trim().ToLowerInvariant();

        if (normalized1.Contains(normalized2) || normalized2.Contains(normalized1))
        {
            return 0.9;
        }

        var maxLength = Math.Max(normalized1.Length, normalized2.Length);
        if (maxLength == 0)
        {
            return 1.0;
        }

        var distance = LevenshteinDistance(normalized1, normalized2);
        return 1.0 - (distance / (double)maxLength);
    }

    /// <summary>
    /// Calculates Levenshtein distance between two strings.
    /// </summary>
    private static int LevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.IsNullOrEmpty(t) ? 0 : t.Length;
        }

        if (string.IsNullOrEmpty(t))
        {
            return s.Length;
        }

        var n = s.Length;
        var m = t.Length;
        var d = new int[n + 1, m + 1];

        for (var i = 0; i <= n; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= m; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }

    /// <summary>
    /// Fixes language dependencies by adding required companion languages.
    /// - Languages that require English: ch_sim, ch_tra, zh_sim, zh_tra, ja, ko, th
    /// - Arabic (ar) can ONLY work with: fa, ur, ug, en (no other languages allowed)
    /// </summary>
    private string[] FixLanguageDependencies(string[] languages)
    {
        var set = new HashSet<string>(languages, StringComparer.OrdinalIgnoreCase);

        if (set.Contains("ar", StringComparer.OrdinalIgnoreCase))
        {
            var arabicOnlyLangs = new[] { "ar", "fa", "ur", "ug", "en" };
            var restrictedSet = new HashSet<string>(arabicOnlyLangs, StringComparer.OrdinalIgnoreCase);
            
            var removedLangs = set.Except(restrictedSet, StringComparer.OrdinalIgnoreCase).ToList();
            
            if (removedLangs.Count > 0)
            {
                _logger?.LogWarning(
                    "Arabic (ar) can only work with [ar, fa, ur, ug, en]. Removing incompatible languages: {RemovedLanguages}",
                    string.Join(", ", removedLangs));
            }
            
            foreach (var lang in arabicOnlyLangs)
            {
                restrictedSet.Add(lang);
            }
            
            _logger?.LogInformation(
                "Using Arabic-compatible language set: {Languages}",
                string.Join(", ", restrictedSet.OrderBy(l => l)));
            
            return restrictedSet.ToArray();
        }

        var englishOnlyLangs = new[] { "ch_sim", "ch_tra", "zh_sim", "zh_tra", "ja", "ko", "th" };
        var originalCount = set.Count;

        if (languages.Any(l => englishOnlyLangs.Contains(l, StringComparer.OrdinalIgnoreCase)))
        {
            set.Add("en");
            
            if (set.Count > originalCount)
            {
                var dependentLangs = languages.Where(l => englishOnlyLangs.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList();
                _logger?.LogInformation(
                    "Added 'en' to language list due to dependency requirements. Languages requiring English: {DependentLanguages}",
                    string.Join(", ", dependentLangs));
            }
        }

        var nonLatinScripts = new[] { "hi", "bn", "te", "ta", "mr", "gu", "kn", "ml", "ne", "pa", "si" };
        if (languages.Any(l => nonLatinScripts.Contains(l, StringComparer.OrdinalIgnoreCase)) && !set.Contains("en", StringComparer.OrdinalIgnoreCase))
        {
            set.Add("en");
            var scriptLangs = languages.Where(l => nonLatinScripts.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList();
            _logger?.LogInformation(
                "Added 'en' to language list for better multilingual support. Images often contain both English and {Script} text.",
                string.Join(", ", scriptLangs));
        }

        return set.ToArray();
    }

    private Task<PyObject> CreateReaderAsync(string[] languages, bool useGpu, CancellationToken cancellationToken)
    {
        languages = FixLanguageDependencies(languages);
        var fixedLanguages = languages;

        return Task.Run(() =>
        {
            using var gil = Py.GIL();

            if (_logger is not null)
            {
                _logger.LogInformation(
                    "Creating EasyOCR reader for languages [{Languages}] (GPU: {Gpu}).",
                    string.Join(", ", fixedLanguages),
                    useGpu);
            }

            // Python runtime is already initialized by PythonInitializer
            // We just need to ensure site-packages is in sys.path
            var sitePackages = Path.Combine(PythonEngine.PythonHome ?? string.Empty, "Lib", "site-packages");
            
            using var sys = Py.Import("sys");
            using var sysPath = sys.GetAttr("path");
            
            bool sitePackagesInPath = false;
            var pathStrings = new List<string>();
            
            using (var iter = sysPath.GetIterator())
            {
                while (iter.MoveNext())
                {
                    using var item = iter.Current;
                    var pathStr = item?.ToString() ?? string.Empty;
                    pathStrings.Add(pathStr);
                    if (pathStr.Contains("site-packages", StringComparison.OrdinalIgnoreCase))
                    {
                        sitePackagesInPath = true;
                    }
                }
            }
            
            if (!sitePackagesInPath && Directory.Exists(sitePackages))
            {
                _logger?.LogDebug("Adding site-packages to sys.path: {SitePackages}", sitePackages);
                using var insertMethod = sysPath.GetAttr("insert");
                insertMethod.Invoke(PyObject.FromManagedObject(0), PyObject.FromManagedObject(sitePackages));
            }
            
            _logger?.LogDebug("Python sys.path: {SysPath}", string.Join("; ", pathStrings));

            using var easyocrModule = Py.Import("easyocr");
            using var readerCallable = easyocrModule.GetAttr("Reader");
            using var languageArg = PyObject.FromManagedObject(fixedLanguages);
            using var args = new PyTuple(new[] { languageArg });
            using var kwargs = new PyDict();
            
            if (useGpu)
            {
                using var gpuValue = PyObject.FromManagedObject(true);
                kwargs.SetItem("gpu", gpuValue);
            }
            
            using var verboseValue = PyObject.FromManagedObject(true);
            kwargs.SetItem("verbose", verboseValue);

            _logger?.LogInformation(
                "Initializing EasyOCR reader for languages [{Languages}]. " +
                "This may take several minutes on first run as language models are downloaded. " +
                "Models will be cached for future use.",
                string.Join(", ", fixedLanguages));

            var reader = readerCallable.Invoke(args, kwargs);
            
            _logger?.LogInformation("EasyOCR reader initialized successfully.");
            
            return reader;
        }, cancellationToken);
    }

    private PyObject InvokeReader(PyObject reader, string imagePath)
    {
        dynamic readerDynamic = reader;
        return readerDynamic.readtext(imagePath, detail: 1, paragraph: false);
    }

    private static double ConvertToDouble(PyObject obj)
    {
        try
        {
            return obj.As<double>();
        }
        catch
        {
            try
            {
                return (double)obj.As<float>();
            }
                catch
                {
                    try
                    {
                        return (double)obj.As<int>();
                    }
                    catch
                    {
                        return double.Parse(obj.ToString() ?? "0");
                }
            }
        }
    }

    private OcrResult ConvertToResult(PyObject pythonResult, string[] languages, bool usedGpu, TimeSpan elapsed)
    {
        var lines = new List<OcrLine>();
        var textBuilder = new StringBuilder();

        using var iterable = new PyIterable(pythonResult);
        foreach (var item in iterable)
        {
            using var tuple = item;

            var text = tuple[1].ToString() ?? string.Empty;
            var confidence = ConvertToDouble(tuple[2]);
            var points = new List<OcrPoint>();

            using var bbox = tuple[0];
                using var bboxIterable = new PyIterable(bbox);
                foreach (var pointObj in bboxIterable)
                {
                    using var point = pointObj;
                    var x = ConvertToDouble(point[0]);
                    var y = ConvertToDouble(point[1]);
                    points.Add(new OcrPoint(x, y));
            }

            var boundingBox = points.Count > 0
                ? OcrBoundingBox.FromPoints(points)
                : OcrBoundingBox.Empty;

            if (!string.IsNullOrEmpty(text))
            {
                if (textBuilder.Length > 0)
                {
                    textBuilder.AppendLine();
                }

                textBuilder.Append(text);
            }

            lines.Add(new OcrLine
            {
                Text = text,
                Confidence = confidence,
                BoundingPolygon = points.Count > 0 ? points : Array.Empty<OcrPoint>(),
                BoundingBox = boundingBox
            });
        }

        return new OcrResult
        {
            FullText = textBuilder.ToString(),
            Lines = lines,
            Languages = Array.AsReadOnly((string[])languages.Clone()),
            Duration = elapsed,
            UsedGpu = usedGpu
        };
    }

    private static string BuildCacheKey(IEnumerable<string> languages, bool useGpu)
    {
        var languageKey = string.Join("-", languages.OrderBy(lang => lang, StringComparer.OrdinalIgnoreCase));
        return $"{languageKey}|gpu:{useGpu}";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void PatchPyTorchDllLoading(string sitePackages)
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            return;
        }

        try
        {
            using var gil = Py.GIL();
            
            // Execute Python code to patch os.add_dll_directory
            // Use the sitePackages path to resolve relative paths
            var patchCode = $@"
import os
import pathlib

# Store original function
_original_add_dll_directory = os.add_dll_directory

# Base path for resolving relative paths
_site_packages_base = r'{sitePackages.Replace("\\", "\\\\")}'

def _patched_add_dll_directory(path):
    '''Patch os.add_dll_directory to handle relative paths'''
    try:
        # Convert to Path object to handle relative paths
        path_obj = pathlib.Path(path)
        
        # If it's a relative path, try to resolve it
        if not path_obj.is_absolute():
            # Try resolving relative to site-packages/torch
            base_path = pathlib.Path(_site_packages_base)
            torch_dir = base_path / 'torch'
            
            # Try common relative paths
            resolved = torch_dir / path
            if resolved.exists() and resolved.is_dir():
                path = str(resolved)
            else:
                # Try torch/lib directory
                resolved = torch_dir / 'lib' / path
                if resolved.exists() and resolved.is_dir():
                    path = str(resolved)
                else:
                    # If can't resolve, skip this directory
                    return None
        
        # Ensure path exists and is a directory
        if not os.path.isdir(path):
            return None
            
        # Call original function with absolute path
        return _original_add_dll_directory(path)
    
    except Exception:
        # If anything fails, skip silently
        return None

# Replace the function
os.add_dll_directory = _patched_add_dll_directory
";
            
            PythonEngine.Exec(patchCode);
        }
        catch (Exception ex)
        {
            // If patching fails, log but continue - torch might still work
            System.Diagnostics.Debug.WriteLine($"Failed to patch PyTorch DLL loading: {ex.Message}");
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EasyOcrService));
        }
    }
}
