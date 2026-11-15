using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Model-free tests for <see cref="EasyOcrModels"/>: which files each set resolves to (honouring the service
/// options exactly as the engines do), the report-only cache check, and offline pre-download against a temp
/// cache seeded with placeholder files. Nothing here touches the network — every download call runs with
/// <see cref="ModelDownloadOptions.Offline"/> — and the real model cache is never used.
/// </summary>
public class EasyOcrModelsTests
{
    private static string FreshCacheDir(bool create = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "easyocr_models_" + Guid.NewGuid().ToString("N"));
        if (create) Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Seed(string dir, params string[] fileNames)
    {
        foreach (var name in fileNames)
        {
            File.WriteAllBytes(Path.Combine(dir, name), new byte[] { 1, 2, 3, 4 });
        }
    }

    private static void TryDelete(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    // ---- file planning ----

    [Fact]
    public void Ocr_set_is_the_detector_plus_one_pack_per_script()
    {
        Assert.Equal(
            new[] { "craft_mlt_25k.onnx", "latin_g2.onnx", "latin_g2.vocab.json" },
            EasyOcrModels.GetModelFileNames(new[] { "en" }));

        // en and fr share the Latin pack; codes are normalized like the service does.
        Assert.Equal(
            new[] { "craft_mlt_25k.onnx", "latin_g2.onnx", "latin_g2.vocab.json", "cyrillic_g2.onnx", "cyrillic_g2.vocab.json" },
            EasyOcrModels.GetModelFileNames(new[] { "en", " FR ", "ru", "", "en" }));
    }

    [Fact]
    public void Ocr_set_honours_quantize_custom_recognizers_and_a_local_detector()
    {
        var quantized = new EasyOcrServiceOptions { Quantize = true };
        Assert.Equal(
            new[] { "craft_mlt_25k.onnx", "latin_g2.int8.onnx", "latin_g2.vocab.json" },
            EasyOcrModels.GetModelFileNames(new[] { "en" }, EasyOcrModelSet.Ocr, quantized));

        var custom = new EasyOcrServiceOptions { DetectorModelPath = "my_craft.onnx" };
        custom.CustomRecognizers.Add(new CustomRecognizer { Name = "mine", ModelPath = "mine.onnx", Characters = "ab", Languages = new[] { "en" } });
        Assert.Equal(
            new[] { "cyrillic_g2.onnx", "cyrillic_g2.vocab.json" },
            EasyOcrModels.GetModelFileNames(new[] { "en", "ru" }, EasyOcrModelSet.Ocr, custom));
    }

    [Fact]
    public void Unsupported_language_fails_fast_for_the_ocr_set_only()
    {
        var ex = Assert.Throws<ArgumentException>(() => EasyOcrModels.GetModelFileNames(new[] { "en", "xx" }));
        Assert.Contains("xx", ex.Message);

        // Document analysis skips unknown codes (and falls back to its default pack), as AnalyzeDocumentAsync does.
        var names = EasyOcrModels.GetModelFileNames(new[] { "xx" }, EasyOcrModelSet.StructureCore);
        Assert.Contains("PP-OCRv5_mobile_rec.onnx", names);
    }

    [Fact]
    public void Document_preprocessing_and_handwriting_sets_resolve_their_models()
    {
        Assert.Equal(
            new[] { "PP-LCNet_x1_0_doc_ori.onnx", "UVDoc.onnx" },
            EasyOcrModels.GetModelFileNames(Array.Empty<string>(), EasyOcrModelSet.DocumentPreprocessing));

        Assert.Equal(
            new[] { "trocr_base_handwritten_encoder.int8.onnx", "trocr_base_handwritten_decoder.int8.onnx", "trocr_base_handwritten.vocab.json" },
            EasyOcrModels.GetModelFileNames(Array.Empty<string>(), EasyOcrModelSet.Handwriting));

        var fullPrecision = new EasyOcrServiceOptions { Handwriting = new HandwritingOptions { Quantize = false, EncoderModelPath = "enc.onnx" } };
        Assert.Equal(
            new[] { "trocr_base_handwritten_decoder.onnx", "trocr_base_handwritten.vocab.json" },
            EasyOcrModels.GetModelFileNames(Array.Empty<string>(), EasyOcrModelSet.Handwriting, fullPrecision));
    }

    [Fact]
    public void Structure_set_matches_what_default_document_analysis_loads()
    {
        var names = EasyOcrModels.GetModelFileNames(new[] { "en" }, EasyOcrModelSet.Structure);

        Assert.Equal(
            new[]
            {
                "PP-DocLayoutV3.onnx", "PP-DocLayoutV3_labels.txt", "PP-OCRv5_mobile_det.onnx",
                "PP-LCNet_x1_0_textline_ori.onnx", "PP-OCRv5_mobile_rec.onnx", "ppocrv5_dict.txt",
                "SLANet_plus.onnx",
                "latexocr_image_resizer.onnx", "latexocr_encoder.onnx", "latexocr_decoder.onnx", "latexocr_tokenizer.json",
                "PP-OCRv4_server_seal_det.onnx",
            },
            names);
        Assert.DoesNotContain("craft_mlt_25k.onnx", names);
        Assert.DoesNotContain("SLANeXt_wired.onnx", names);
    }

    [Fact]
    public void Structure_languages_map_to_document_analysis_packs()
    {
        var names = EasyOcrModels.GetModelFileNames(new[] { "fr" }, EasyOcrModelSet.StructureCore);

        Assert.Contains("latin_PP-OCRv5_mobile_rec.onnx", names);
        Assert.Contains("latin_dict.txt", names);
        Assert.DoesNotContain("SLANet_plus.onnx", names);
    }

    [Fact]
    public void Every_structure_set_implies_the_core_models()
    {
        var names = EasyOcrModels.GetModelFileNames(Array.Empty<string>(), EasyOcrModelSet.StructureTablesSlaNeXt);

        Assert.Equal(
            new[]
            {
                "PP-DocLayoutV3.onnx", "PP-DocLayoutV3_labels.txt", "PP-OCRv5_mobile_det.onnx",
                "PP-LCNet_x1_0_textline_ori.onnx", "PP-OCRv5_mobile_rec.onnx", "ppocrv5_dict.txt",
                "PP-LCNet_x1_0_table_cls.onnx", "SLANeXt_wired.onnx", "SLANet_plus.onnx",
            },
            names);
    }

    [Fact]
    public void File_names_shared_between_engines_are_listed_once()
    {
        var names = EasyOcrModels.GetModelFileNames(new[] { "en" }, EasyOcrModelSet.All);

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Single(names, n => n == "PP-LCNet_x1_0_doc_ori.onnx");
        Assert.Single(names, n => n == "UVDoc.onnx");
    }

    [Fact]
    public void Undefined_set_flags_and_null_languages_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EasyOcrModels.GetModelFileNames(new[] { "en" }, (EasyOcrModelSet)(1 << 20)));
        Assert.Throws<ArgumentNullException>(() => EasyOcrModels.GetModelFileNames(null!));
        Assert.Throws<ArgumentNullException>(() => EasyOcrModels.CheckCache(null!));
    }

    [Fact]
    public void GetCacheDirectory_resolves_the_service_cache_path()
    {
        var dir = FreshCacheDir(create: false);
        Assert.Equal(Path.GetFullPath(dir), EasyOcrModels.GetCacheDirectory(new EasyOcrServiceOptions { ModelCachePath = dir }));
        Assert.False(Directory.Exists(dir));
    }

    // ---- report-only cache check ----

    [Fact]
    public void CheckCache_reports_cached_and_missing_files_without_creating_anything()
    {
        var dir = FreshCacheDir();
        try
        {
            Seed(dir, "craft_mlt_25k.onnx", "latin_g2.onnx");
            var before = Directory.GetFiles(dir).Length;

            var report = EasyOcrModels.CheckCache(new[] { "en" }, EasyOcrModelSet.Ocr, new EasyOcrServiceOptions { ModelCachePath = dir });

            Assert.Equal(Path.GetFullPath(dir), report.CacheDirectory);
            Assert.Equal(
                new[] { ModelFileStatus.Cached, ModelFileStatus.Cached, ModelFileStatus.Missing },
                report.Files.Select(f => f.Status));
            Assert.Equal(new long[] { 4, 4, 0 }, report.Files.Select(f => f.SizeBytes));
            Assert.Equal(Path.Combine(Path.GetFullPath(dir), "latin_g2.vocab.json"), report.Files[2].Path);
            Assert.Equal(2, report.CachedCount);
            Assert.Equal(1, report.MissingCount);
            Assert.Equal(8, report.TotalBytes);
            Assert.False(report.IsComplete);
            Assert.Equal(before, Directory.GetFiles(dir).Length);

            var ex = Assert.Throws<ModelDownloadException>(report.EnsureSuccess);
            Assert.Contains("latin_g2.vocab.json", ex.Message);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void CheckCache_on_a_missing_directory_does_not_create_it()
    {
        var dir = FreshCacheDir(create: false);
        try
        {
            var report = EasyOcrModels.CheckCache(new[] { "en" }, EasyOcrModelSet.Ocr | EasyOcrModelSet.StructureCore,
                new EasyOcrServiceOptions { ModelCachePath = dir });

            Assert.All(report.Files, f => Assert.Equal(ModelFileStatus.Missing, f.Status));
            Assert.False(Directory.Exists(dir));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ---- offline pre-download ----

    [Fact]
    public async Task DownloadAsync_reports_seeded_files_as_cached_and_signals_progress()
    {
        var dir = FreshCacheDir();
        try
        {
            Seed(dir, "craft_mlt_25k.onnx", "latin_g2.onnx", "latin_g2.vocab.json");
            var download = new ModelDownloadOptions { Offline = true };
            var progress = new RecordingProgress();

            var report = await EasyOcrModels.DownloadAsync(
                new EasyOcrServiceOptions { ModelCachePath = dir, Download = download }, new[] { "en" }, EasyOcrModelSet.Ocr, progress);

            Assert.True(report.IsComplete);
            Assert.Equal(3, report.CachedCount);
            Assert.Equal(0, report.DownloadedCount);
            report.EnsureSuccess();

            Assert.Equal(new[] { "craft_mlt_25k.onnx", "latin_g2.onnx", "latin_g2.vocab.json" }, progress.Reports.Select(r => r.FileName));
            Assert.All(progress.Reports, r => Assert.Equal(1.0, r.Fraction));
            Assert.Null(download.Progress); // the caller's options are never mutated
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task DownloadAsync_reports_each_missing_file_as_failed_instead_of_throwing()
    {
        var dir = FreshCacheDir();
        try
        {
            Seed(dir, "craft_mlt_25k.onnx");

            var report = await EasyOcrModels.DownloadAsync(
                new EasyOcrServiceOptions { ModelCachePath = dir, Download = new ModelDownloadOptions { Offline = true } },
                new[] { "en" });

            Assert.Equal(ModelFileStatus.Cached, report.Files[0].Status);
            Assert.Equal(2, report.FailedCount);
            Assert.All(report.Files.Skip(1), f => Assert.IsType<OfflineModelMissingException>(f.Error));

            var ex = Assert.Throws<ModelDownloadException>(report.EnsureSuccess);
            Assert.IsType<OfflineModelMissingException>(ex.InnerException);
            Assert.Equal(new[] { "craft_mlt_25k.onnx" }, Directory.GetFiles(dir).Select(Path.GetFileName));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task Structure_failures_surface_as_public_exception_types()
    {
        var dir = FreshCacheDir();
        try
        {
            Seed(dir, "PP-DocLayoutV3.onnx");

            var report = await EasyOcrModels.DownloadAsync(
                new EasyOcrServiceOptions { ModelCachePath = dir, Download = new ModelDownloadOptions { Offline = true } },
                new[] { "en" }, EasyOcrModelSet.StructureCore);

            Assert.Equal(ModelFileStatus.Cached, report.Files.Single(f => f.FileName == "PP-DocLayoutV3.onnx").Status);
            var failed = report.Files.Where(f => f.Status == ModelFileStatus.Failed).ToList();
            Assert.Equal(5, failed.Count);
            Assert.All(failed, f => Assert.IsType<OfflineModelMissingException>(f.Error));
            Assert.All(report.Files, f => Assert.StartsWith(Path.GetFullPath(dir), f.Path, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task DownloadAsync_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EasyOcrModels.DownloadAsync(
            new[] { "en" }, EasyOcrModelSet.Ocr, new ModelDownloadOptions { Offline = true }, cancellationToken: cts.Token));
    }

    [Fact]
    public void EnsureSuccess_wraps_the_first_failure()
    {
        var first = new IOException("disk full");
        var report = new ModelDownloadReport
        {
            CacheDirectory = "c:/cache",
            Files = new[]
            {
                new ModelFileReport("a.onnx", ModelFileStatus.Downloaded, 10, "c:/cache/a.onnx"),
                new ModelFileReport("b.onnx", ModelFileStatus.Failed, 0, "c:/cache/b.onnx", first),
                new ModelFileReport("c.onnx", ModelFileStatus.Failed, 0, "c:/cache/c.onnx", new IOException("other")),
            },
        };

        var ex = Assert.Throws<ModelDownloadException>(report.EnsureSuccess);
        Assert.Same(first, ex.InnerException);
        Assert.Contains("2 of 3", ex.Message);
        Assert.Equal(1, report.DownloadedCount);
        Assert.Equal(10, report.TotalBytes);
    }

    private sealed class RecordingProgress : IProgress<ModelDownloadProgress>
    {
        public List<ModelDownloadProgress> Reports { get; } = new();

        public void Report(ModelDownloadProgress value) => Reports.Add(value);
    }
}
