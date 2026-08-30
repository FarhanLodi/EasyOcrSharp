using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Tests for the background warm-up hosted service and the deep (self-test) health check.
/// <para>
/// Everything here runs against a temp directory standing in for the model cache and a stub
/// <see cref="IEasyOcrService"/> — <b>no network, no model downloads, no ONNX sessions</b>. The stub is
/// what makes the headline case testable at all: it reproduces exactly what a truncated model file does
/// in production (the session fails to initialize) without needing a 100 MB corrupt file on disk.
/// </para>
/// </summary>
public class WarmUpHealthCheckTests
{
    // ------------------------------------------------------------------ helpers

    private static string FreshCacheDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "easyocr_warmup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Writes files with the exact names the health check looks for, filled with garbage — i.e. a cache
    /// that passes every File.Exists check while holding models that cannot possibly load.
    /// </summary>
    private static void SeedTruncatedModels(string cacheRoot, string language)
    {
        var junk = new byte[16];
        File.WriteAllBytes(Path.Combine(cacheRoot, ModelRegistry.Detector.FileName), junk);

        var def = ModelRegistry.FindByLanguage(language)
                  ?? throw new InvalidOperationException($"Test language '{language}' is not in the registry.");
        File.WriteAllBytes(Path.Combine(cacheRoot, def.Model.FileName), junk);
        File.WriteAllBytes(Path.Combine(cacheRoot, def.Vocab.FileName), junk);
    }

    private static EasyOcrServiceOptions OptionsFor(string cache, bool offline = false) => new()
    {
        ModelCachePath = cache,
        Download = new ModelDownloadOptions { Offline = offline },
    };

    private static Task<HealthCheckResult> Check(EasyOcrHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext());

    // ------------------------------------------------------------------ the headline fix

    [Fact]
    public async Task Deep_probe_reports_unhealthy_when_a_cached_model_is_corrupt()
    {
        var cache = FreshCacheDir();
        try
        {
            SeedTruncatedModels(cache, "en");
            var options = OptionsFor(cache);

            // The historical check only asks whether the files exist, so a truncated cache reports
            // Healthy and then every single request 500s. This assertion documents that blind spot.
            var shallow = await Check(new EasyOcrHealthCheck(options, new[] { "en" }));
            Assert.Equal(HealthStatus.Healthy, shallow.Status);

            // The deep probe runs the pipeline, which is where a corrupt model actually fails.
            var ocr = new StubOcrService
            {
                WarmUpFailure = new InvalidOperationException("Failed to load model: protobuf parsing failed"),
            };
            var deep = new EasyOcrHealthCheck(
                options, new[] { "en" }, HealthStatus.Degraded, ocr,
                new EasyOcrHealthCheckOptions { DeepProbe = true });

            var result = await Check(deep);

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal("failed", result.Data["probe"]);
            Assert.Equal(1, ocr.WarmUpCalls);
            Assert.NotNull(result.Exception);
            Assert.Contains("protobuf", result.Exception!.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    // ------------------------------------------------------------------ probe caching / re-probing

    [Fact]
    public async Task Deep_probe_verdict_is_cached_within_the_interval()
    {
        var cache = FreshCacheDir();
        try
        {
            SeedTruncatedModels(cache, "en");
            var ocr = new StubOcrService();
            var check = new EasyOcrHealthCheck(
                OptionsFor(cache), new[] { "en" }, HealthStatus.Degraded, ocr,
                new EasyOcrHealthCheckOptions { DeepProbe = true, ProbeInterval = TimeSpan.FromMinutes(30) });

            var first = await Check(check);
            var second = await Check(check);

            Assert.Equal(HealthStatus.Healthy, first.Status);
            Assert.Equal(HealthStatus.Healthy, second.Status);
            // Running OCR on every readiness poll would be a self-inflicted load test.
            Assert.Equal(1, ocr.ExtractCalls);
            Assert.Equal(1, ocr.WarmUpCalls);
            Assert.Equal(first.Data["probeAt"], second.Data["probeAt"]);
            Assert.Equal("Cpu", second.Data["executionProvider"]);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task Deep_probe_reruns_after_the_interval_so_a_transient_failure_can_recover()
    {
        var cache = FreshCacheDir();
        try
        {
            SeedTruncatedModels(cache, "en");
            var ocr = new StubOcrService { ExtractFailure = new TimeoutException("GPU busy") };
            // Zero interval = probe on every check; the point is that a failed verdict never latches.
            var check = new EasyOcrHealthCheck(
                OptionsFor(cache), new[] { "en" }, HealthStatus.Degraded, ocr,
                new EasyOcrHealthCheckOptions { DeepProbe = true, ProbeInterval = TimeSpan.Zero });

            Assert.Equal(HealthStatus.Unhealthy, (await Check(check)).Status);

            ocr.ExtractFailure = null;   // the transient condition clears

            var recovered = await Check(check);
            Assert.Equal(HealthStatus.Healthy, recovered.Status);
            Assert.Equal(2, ocr.ExtractCalls);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    // ------------------------------------------------------------------ the probe must never download

    [Fact]
    public async Task Deep_probe_is_skipped_when_models_are_not_cached()
    {
        var cache = FreshCacheDir();   // deliberately empty
        try
        {
            var ocr = new StubOcrService();
            var check = new EasyOcrHealthCheck(
                OptionsFor(cache), new[] { "en" }, HealthStatus.Degraded, ocr,
                new EasyOcrHealthCheckOptions { DeepProbe = true });

            var result = await Check(check);

            Assert.Equal(HealthStatus.Degraded, result.Status);
            // Probing here would download the very models the check is reporting as absent.
            Assert.Equal(0, ocr.WarmUpCalls);
            Assert.Equal(0, ocr.ExtractCalls);
            Assert.Contains("skipped", (string)result.Data["probe"], StringComparison.Ordinal);
            Assert.True(result.Data.ContainsKey("missing"));
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task Offline_with_missing_models_is_unhealthy_even_with_the_deep_probe_on()
    {
        var cache = FreshCacheDir();
        try
        {
            var ocr = new StubOcrService();
            var check = new EasyOcrHealthCheck(
                OptionsFor(cache, offline: true), new[] { "en" }, HealthStatus.Degraded, ocr,
                new EasyOcrHealthCheckOptions { DeepProbe = true });

            var result = await Check(check);

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Contains("Offline", result.Description!, StringComparison.Ordinal);
            Assert.Equal(0, ocr.WarmUpCalls);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    // ------------------------------------------------------------------ warm-up state in the result

    [Fact]
    public async Task Warm_up_in_progress_reports_not_ready()
    {
        var cache = FreshCacheDir();
        try
        {
            SeedTruncatedModels(cache, "en");
            var state = new EasyOcrWarmUpState();
            state.MarkStarted(new[] { "en" });

            var check = new EasyOcrHealthCheck(
                OptionsFor(cache), new[] { "en" }, HealthStatus.Degraded, new StubOcrService(),
                checkOptions: null, warmUpState: state);

            var result = await Check(check);

            // Unhealthy, not Degraded: Degraded maps to HTTP 200 and would let traffic in mid-warm-up.
            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal(nameof(EasyOcrWarmUpStatus.InProgress), result.Data["warmUp"]);
            Assert.Contains("warm-up in progress", result.Description!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task Warm_up_failure_is_surfaced_in_the_result()
    {
        var cache = FreshCacheDir();
        try
        {
            SeedTruncatedModels(cache, "en");
            var state = new EasyOcrWarmUpState();
            state.MarkStarted(new[] { "en" });
            state.MarkFailed(TimeSpan.FromSeconds(3), new HttpRequestException("model mirror unreachable"));

            var check = new EasyOcrHealthCheck(
                OptionsFor(cache), new[] { "en" }, HealthStatus.Degraded, new StubOcrService(),
                checkOptions: null, warmUpState: state);

            var result = await Check(check);

            // Files are on disk, so this is not fatal — but it must not be reported as plain Healthy.
            Assert.Equal(HealthStatus.Degraded, result.Status);
            Assert.Equal(nameof(EasyOcrWarmUpStatus.Failed), result.Data["warmUp"]);
            Assert.Equal("model mirror unreachable", result.Data["warmUpError"]);
            Assert.Contains("mirror unreachable", result.Description!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    // ------------------------------------------------------------------ the hosted service

    [Fact]
    public async Task Warm_up_service_never_throws_when_warm_up_fails()
    {
        var state = new EasyOcrWarmUpState();
        var ocr = new StubOcrService { WarmUpFailure = new HttpRequestException("no route to model host") };
        using var service = new EasyOcrWarmUpService(ocr, new[] { "en" }, state);

        await service.StartAsync(CancellationToken.None);
        // An exception escaping ExecuteAsync stops the whole host (StopHost is the default behaviour),
        // which is exactly the crash loop warm-up is supposed to avoid.
        await service.ExecuteTask!;
        await service.StopAsync(CancellationToken.None);

        var snapshot = state.Snapshot();
        Assert.Equal(EasyOcrWarmUpStatus.Failed, snapshot.Status);
        Assert.IsType<HttpRequestException>(snapshot.Error);
        Assert.Equal(new[] { "en" }, snapshot.Languages);
    }

    [Fact]
    public async Task Warm_up_service_records_success_and_elapsed_time()
    {
        var state = new EasyOcrWarmUpState();
        using var service = new EasyOcrWarmUpService(new StubOcrService(), new[] { "en" }, state);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        Assert.Equal(EasyOcrWarmUpStatus.Completed, state.Status);
        Assert.Null(state.Error);
        Assert.False(state.IsPending);
    }

    [Fact]
    public async Task Warm_up_service_completes_quietly_on_shutdown()
    {
        var state = new EasyOcrWarmUpState();
        var ocr = new StubOcrService { BlockUntilCancelled = true };
        using var service = new EasyOcrWarmUpService(ocr, new[] { "en" }, state);

        await service.StartAsync(CancellationToken.None);
        await ocr.WarmUpEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await service.StopAsync(CancellationToken.None);

        // Cancellation on shutdown is normal: the task must complete, not fault.
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Null(state.Error);
    }

    // ------------------------------------------------------------------ unchanged defaults

    [Fact]
    public async Task Default_health_check_behaves_exactly_as_before()
    {
        var cache = FreshCacheDir();
        try
        {
            // Empty cache: Degraded + a "missing" list, no probe keys, no warm-up keys.
            var check = new EasyOcrHealthCheck(OptionsFor(cache), new[] { "en" });
            var missingResult = await Check(check);

            Assert.Equal(HealthStatus.Degraded, missingResult.Status);
            Assert.True(missingResult.Data.ContainsKey("missing"));
            Assert.False(missingResult.Data.ContainsKey("probe"));
            Assert.False(missingResult.Data.ContainsKey("warmUp"));

            // Files present: Healthy with the original description and only the cache path in data.
            SeedTruncatedModels(cache, "en");
            var presentResult = await Check(check);

            Assert.Equal(HealthStatus.Healthy, presentResult.Status);
            Assert.Equal("Models present; ready to serve.", presentResult.Description);
            Assert.Equal(new[] { "cachePath" }, presentResult.Data.Keys.ToArray());
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task Deep_probe_without_an_ocr_service_says_so_instead_of_claiming_health()
    {
        var cache = FreshCacheDir();
        try
        {
            SeedTruncatedModels(cache, "en");
            var check = new EasyOcrHealthCheck(
                OptionsFor(cache), new[] { "en" }, HealthStatus.Degraded, ocrService: null,
                new EasyOcrHealthCheckOptions { DeepProbe = true });

            var result = await Check(check);

            Assert.Equal(HealthStatus.Degraded, result.Status);
            Assert.Contains("unavailable", (string)result.Data["probe"], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    // ------------------------------------------------------------------ DI wiring

    [Fact]
    public void AddEasyOcrWarmUp_registers_one_hosted_service_and_a_shared_state()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEasyOcrService>(new StubOcrService());
        services.AddEasyOcrWarmUp("en");
        // A library and the host app both calling it must not produce two competing warm-ups.
        services.AddEasyOcrWarmUp("en", "fr");

        using var provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<IHostedService>().OfType<EasyOcrWarmUpService>());
        var state = provider.GetRequiredService<EasyOcrWarmUpState>();
        Assert.Same(state, provider.GetRequiredService<EasyOcrWarmUpState>());
        // The hosted service publishes into the same singleton the health check reads.
        Assert.Same(state, provider.GetServices<IHostedService>().OfType<EasyOcrWarmUpService>().Single().State);
        Assert.Equal(EasyOcrWarmUpStatus.NotStarted, state.Status);
    }

    [Fact]
    public async Task AddEasyOcrHealthCheck_with_probe_options_picks_up_the_registered_service_and_state()
    {
        var cache = FreshCacheDir();
        try
        {
            SeedTruncatedModels(cache, "en");
            var ocr = new StubOcrService();

            var services = new ServiceCollection();
            services.AddSingleton(OptionsFor(cache));
            services.AddSingleton<IEasyOcrService>(ocr);
            services.AddEasyOcrWarmUp("en");
            services.AddHealthChecks().AddEasyOcrHealthCheck(
                new EasyOcrHealthCheckOptions { DeepProbe = true },
                new[] { "en" });

            using var provider = services.BuildServiceProvider();
            var check = provider.GetRequiredService<EasyOcrHealthCheck>();

            var result = await Check(check);

            // Warm-up is registered but has not run, so the check reports "not ready" rather than
            // probing — proving both the service and the state were wired in.
            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal(nameof(EasyOcrWarmUpStatus.NotStarted), result.Data["warmUp"]);
            Assert.Equal(0, ocr.ExtractCalls);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    // ------------------------------------------------------------------ stub

    /// <summary>
    /// Minimal <see cref="IEasyOcrService"/> that records calls and can be told to fail — standing in for
    /// an engine whose ONNX sessions cannot be created. No models, no network, no ONNX Runtime.
    /// </summary>
    private sealed class StubOcrService : IEasyOcrService
    {
        private int _warmUpCalls;
        private int _extractCalls;

        public Exception? WarmUpFailure { get; set; }
        public Exception? ExtractFailure { get; set; }
        public bool BlockUntilCancelled { get; set; }
        public TaskCompletionSource WarmUpEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int WarmUpCalls => Volatile.Read(ref _warmUpCalls);
        public int ExtractCalls => Volatile.Read(ref _extractCalls);

        public async Task WarmUp(IEnumerable<string> languages, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _warmUpCalls);
            WarmUpEntered.TrySetResult();
            if (BlockUntilCancelled)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            if (WarmUpFailure is not null)
            {
                throw WarmUpFailure;
            }
        }

        public Task<OcrResult> ExtractTextFromImage(
            Image<Rgb24> image,
            IEnumerable<string> languages,
            RecognitionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _extractCalls);
            if (ExtractFailure is not null)
            {
                return Task.FromException<OcrResult>(ExtractFailure);
            }
            return Task.FromResult(OcrResult.Empty with { Languages = languages.ToArray() });
        }

        public Task<OcrResult> ExtractTextFromImage(string imagePath, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OcrResult> ExtractTextFromImage(Stream imageStream, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OcrResult> ExtractTextFromImage(byte[] imageBytes, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OcrResult> ExtractTextFromImage(ReadOnlyMemory<byte> imageBytes, IEnumerable<string> languages, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> DetectLanguagesAsync(Image<Rgb24> image, IEnumerable<string>? candidates = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> DetectLanguagesAsync(string imagePath, IEnumerable<string>? candidates = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
