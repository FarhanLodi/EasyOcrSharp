using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EasyOcrSharp.Services;

/// <summary>
/// Tuning for <see cref="EasyOcrHealthCheck"/>'s optional deep (self-test) mode. Every value is
/// additive: an unmodified instance leaves the health check behaving exactly like the file-presence
/// check it has always been.
/// </summary>
public sealed class EasyOcrHealthCheckOptions
{
    /// <summary>
    /// Run a tiny synthetic image through the real OCR pipeline instead of only checking that the model
    /// files exist. Default <c>false</c>.
    /// <para>
    /// File presence is not proof of health: a truncated model file, a half-copied cache volume, or a GPU
    /// whose execution provider fails at session initialization all pass <c>File.Exists</c> and then make
    /// every request fail. The deep probe is the only check that catches those before traffic arrives.
    /// </para>
    /// </summary>
    public bool DeepProbe { get; set; }

    /// <summary>
    /// How long a probe verdict is reused before the pipeline is exercised again. Default 5 minutes.
    /// <para>
    /// The interval works in both directions: it stops a per-second readiness probe from running OCR on
    /// every poll, and it stops a single transient failure (a GPU busy for one moment, a cache being
    /// rewritten) from latching the process Unhealthy for its whole lifetime. <see cref="TimeSpan.Zero"/>
    /// probes on every check; a negative value (e.g. <see cref="Timeout.InfiniteTimeSpan"/>) probes once
    /// and never again.
    /// </para>
    /// </summary>
    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Ceiling on how long a single probe may take before it is treated as a failure. Default 30 seconds.
    /// Without it, a wedged ONNX session initialization would hang the readiness endpoint itself rather
    /// than reporting the process as unhealthy. <see cref="TimeSpan.Zero"/> or less disables the ceiling.
    /// </summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Health check that reports whether EasyOcrSharp can serve requests: the model cache directory is
/// accessible, and the models for the configured languages are present (so the first real request
/// won't block on a download). Register via <see cref="ServiceCollectionExtensions.AddEasyOcrHealthCheck(Microsoft.Extensions.DependencyInjection.IHealthChecksBuilder, System.Collections.Generic.IEnumerable{string}, string, HealthStatus)"/>.
/// <para>
/// Opt into <see cref="EasyOcrHealthCheckOptions.DeepProbe"/> to additionally run a tiny synthetic image
/// through the real pipeline once per <see cref="EasyOcrHealthCheckOptions.ProbeInterval"/> — the only
/// way to catch a corrupt cache or an execution provider that fails at session initialization. When an
/// <see cref="EasyOcrWarmUpState"/> is supplied, the check also reports "not ready" while warm-up is
/// still running, and surfaces a warm-up failure.
/// </para>
/// </summary>
public sealed class EasyOcrHealthCheck : IHealthCheck
{
    // A readiness probe must not spike every core: one worker is plenty for a 64x32 image.
    private static readonly RecognitionOptions ProbeRecognitionOptions = new() { MaxDegreeOfParallelism = 1 };

    private readonly EasyOcrServiceOptions _options;
    private readonly string[] _languages;
    private readonly HealthStatus _failureStatus;
    private readonly IEasyOcrService? _ocr;
    private readonly EasyOcrHealthCheckOptions _checkOptions;
    private readonly EasyOcrWarmUpState? _warmUpState;

    // Serializes probes so a burst of concurrent health requests runs the pipeline once, not N times.
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private ProbeVerdict? _lastProbe;

    /// <summary>Creates a health check for the given service options and expected languages.</summary>
    public EasyOcrHealthCheck(EasyOcrServiceOptions options, IEnumerable<string>? languages = null, HealthStatus failureStatus = HealthStatus.Degraded)
        : this(options, languages, failureStatus, null)
    {
    }

    /// <summary>
    /// Creates a health check that can additionally self-test the pipeline and report warm-up progress.
    /// </summary>
    /// <param name="options">The same options the service was built with (cache path, offline mode, provider).</param>
    /// <param name="languages">Languages whose models must be present for a Healthy result, and which the probe exercises.</param>
    /// <param name="failureStatus">Status reported when models are missing.</param>
    /// <param name="ocrService">
    /// Service used by the deep probe. Optional: null keeps the file-presence behaviour. It lives on a
    /// separate constructor on purpose — the original three-argument one is untouched, so no existing
    /// call site (source or already-compiled) has to change.
    /// </param>
    /// <param name="checkOptions">Deep-probe settings. Null means the historical file-presence behaviour.</param>
    /// <param name="warmUpState">Shared warm-up state, when <see cref="EasyOcrWarmUpService"/> is registered.</param>
    public EasyOcrHealthCheck(
        EasyOcrServiceOptions options,
        IEnumerable<string>? languages,
        HealthStatus failureStatus,
        IEasyOcrService? ocrService,
        EasyOcrHealthCheckOptions? checkOptions = null,
        EasyOcrWarmUpState? warmUpState = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _languages = languages?.ToArray() ?? Array.Empty<string>();
        _failureStatus = failureStatus;
        _ocr = ocrService;
        _checkOptions = checkOptions ?? new EasyOcrHealthCheckOptions();
        _warmUpState = warmUpState;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string cacheRoot;
        try
        {
            cacheRoot = ModelDownloadManager.ResolveCacheRoot(_options.ModelCachePath);
            Directory.CreateDirectory(cacheRoot);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Model cache directory is not accessible.", ex);
        }

        var data = new Dictionary<string, object> { ["cachePath"] = cacheRoot };
        var missing = new List<string>();

        // The CRAFT detector is required for every language.
        AddIfMissing(cacheRoot, ModelRegistry.Detector.FileName, missing);

        foreach (var lang in _languages)
        {
            var def = ModelRegistry.FindByLanguage(lang);
            if (def is null)
            {
                missing.Add($"{lang} (unsupported language)");
                continue;
            }
            AddIfMissing(cacheRoot, def.Model.FileName, missing);
            AddIfMissing(cacheRoot, def.Vocab.FileName, missing);
        }

        var warmUp = _warmUpState?.Snapshot();
        if (warmUp is { } snapshot)
        {
            data["warmUp"] = snapshot.Status.ToString();
            if (snapshot.Elapsed > TimeSpan.Zero)
            {
                data["warmUpMs"] = Math.Round(snapshot.Elapsed.TotalMilliseconds);
            }
            if (snapshot.Error is not null)
            {
                data["warmUpError"] = snapshot.Error.Message;
            }

            if (snapshot.Status is EasyOcrWarmUpStatus.NotStarted or EasyOcrWarmUpStatus.InProgress)
            {
                if (missing.Count > 0)
                {
                    data["missing"] = missing;
                }

                // Deliberately Unhealthy rather than _failureStatus: the default failure status is
                // Degraded, and the ASP.NET health middleware maps Degraded to HTTP 200 — which would
                // let the load balancer send traffic to a process that is still downloading its models,
                // the exact cold-start stampede warm-up exists to prevent.
                return new HealthCheckResult(
                    HealthStatus.Unhealthy,
                    snapshot.Status == EasyOcrWarmUpStatus.InProgress
                        ? "Model warm-up in progress; not ready to serve."
                        : "Model warm-up has not started yet; not ready to serve.",
                    data: data);
            }
        }

        if (missing.Count > 0)
        {
            data["missing"] = missing;
            if (_checkOptions.DeepProbe)
            {
                // Never probe with models missing: the probe would trigger the very download this check
                // is supposed to report as pending (and in offline mode it would only fail noisily).
                data["probe"] = "skipped: models are not cached";
            }

            var description = _options.Download.Offline
                ? "Offline mode: required models are missing from the cache."
                : "Some models are not cached yet; they will download on first use.";

            // In offline mode, missing models mean the service cannot run at all.
            var status = _options.Download.Offline ? HealthStatus.Unhealthy : _failureStatus;
            return new HealthCheckResult(status, description, data: data);
        }

        ProbeVerdict? probe = null;
        if (_checkOptions.DeepProbe)
        {
            if (_ocr is null)
            {
                // Opted into the deep probe but nothing to probe with — say so instead of silently
                // downgrading to the shallow check, which would look identical to a passing self-test.
                data["probe"] = "unavailable: no IEasyOcrService was supplied";
                return new HealthCheckResult(
                    _failureStatus,
                    "Deep probe is enabled but no IEasyOcrService is registered; only file presence was checked.",
                    data: data);
            }

            if (_languages.Length == 0)
            {
                // Recognition needs a language pack; with none configured there is no pipeline to run.
                data["probe"] = "skipped: no languages configured";
            }
            else
            {
                probe = await GetOrRunProbeAsync(cancellationToken).ConfigureAwait(false);
                data["probeAt"] = probe.At.ToString("O");
                data["probeMs"] = Math.Round(probe.Duration.TotalMilliseconds);
                data["executionProvider"] = probe.Provider;
                data["probe"] = probe.Success ? "ok" : "failed";

                if (!probe.Success)
                {
                    return new HealthCheckResult(
                        HealthStatus.Unhealthy,
                        "Model files are present but the OCR self-test failed; the pipeline cannot serve requests.",
                        probe.Error,
                        data);
                }
            }
        }

        if (warmUp is { Status: EasyOcrWarmUpStatus.Failed } failed)
        {
            // A successful self-test outranks a stale warm-up failure: the pipeline demonstrably works
            // now, so the process is healthy even though the startup preload did not finish.
            return probe is { Success: true }
                ? new HealthCheckResult(
                    HealthStatus.Healthy,
                    "OCR self-test passed (an earlier model warm-up had failed).",
                    data: data)
                : new HealthCheckResult(
                    _failureStatus,
                    $"Model warm-up failed: {failed.Error?.Message}",
                    failed.Error,
                    data);
        }

        if (probe is not null)
        {
            return HealthCheckResult.Healthy($"OCR self-test passed on {probe.Provider}; ready to serve.", data);
        }

        return HealthCheckResult.Healthy(
            _languages.Length > 0 ? "Models present; ready to serve." : "Model cache accessible.",
            data);
    }

    private static void AddIfMissing(string cacheRoot, string fileName, List<string> missing)
    {
        if (!File.Exists(Path.Combine(cacheRoot, fileName)))
        {
            missing.Add(fileName);
        }
    }

    // ---- deep probe ----

    private async Task<ProbeVerdict> GetOrRunProbeAsync(CancellationToken cancellationToken)
    {
        var cached = Volatile.Read(ref _lastProbe);
        if (IsFresh(cached))
        {
            return cached!;
        }

        await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-read inside the gate: while this call queued, another health request may have just run
            // the probe, and running OCR a second time would defeat the point of the interval.
            cached = Volatile.Read(ref _lastProbe);
            if (IsFresh(cached))
            {
                return cached!;
            }

            var verdict = await RunProbeAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _lastProbe, verdict);
            return verdict;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private bool IsFresh(ProbeVerdict? verdict)
    {
        if (verdict is null) return false;
        if (_checkOptions.ProbeInterval < TimeSpan.Zero) return true;    // negative: probe once, never again
        if (_checkOptions.ProbeInterval == TimeSpan.Zero) return false;  // zero: probe on every check
        return TimeProvider.System.GetElapsedTime(verdict.Timestamp) < _checkOptions.ProbeInterval;
    }

    private async Task<ProbeVerdict> RunProbeAsync(CancellationToken cancellationToken)
    {
        var at = DateTimeOffset.UtcNow;
        var timestamp = TimeProvider.System.GetTimestamp();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_checkOptions.ProbeTimeout > TimeSpan.Zero)
        {
            timeout.CancelAfter(_checkOptions.ProbeTimeout);
        }

        try
        {
            // WarmUp first: a small probe image can legitimately produce no detections, in which case the
            // recognizer session is never created and a truncated recognizer model would go unnoticed.
            // WarmUp loads the detector *and* the recognizer packs, so corrupt weights and a broken
            // execution provider both fail right here rather than on the first customer request.
            await _ocr!.WarmUp(_languages, timeout.Token).ConfigureAwait(false);

            using var image = CreateProbeImage();
            var result = await _ocr
                .ExtractTextFromImage(image, _languages, ProbeRecognitionOptions, timeout.Token)
                .ConfigureAwait(false);

            return new ProbeVerdict(
                Success: true,
                At: at,
                Duration: TimeProvider.System.GetElapsedTime(timestamp),
                Provider: DescribeProvider(result.UsedGpu),
                Error: null,
                Timestamp: timestamp);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller (the health endpoint) went away; that says nothing about the pipeline, so don't
            // cache a failure verdict for it.
            throw;
        }
        catch (Exception ex)
        {
            return new ProbeVerdict(
                Success: false,
                At: at,
                Duration: TimeProvider.System.GetElapsedTime(timestamp),
                Provider: DescribeProvider(usedGpu: false),
                Error: ex,
                Timestamp: timestamp);
        }
    }

    /// <summary>
    /// Builds the 64x32 synthetic page the probe runs: real ink on white rather than a blank image, so
    /// detection has something to find, and small enough that the whole probe costs milliseconds.
    /// </summary>
    private static Image<Rgb24> CreateProbeImage()
    {
        var image = new Image<Rgb24>(64, 32, new Rgb24(255, 255, 255));
        var ink = new Rgb24(0, 0, 0);
        for (var y = 8; y < 24; y++)
        {
            for (var x = 12; x < 52; x += 8)
            {
                image[x, y] = ink;
                image[x + 1, y] = ink;
            }
        }
        return image;
    }

    /// <summary>
    /// Names the provider the engine actually ended up on. <see cref="OcrResult.UsedGpu"/> reflects what
    /// the engine resolved (Auto has already become a concrete provider by then), so a CPU fallback is
    /// reported as CPU even when a GPU was requested.
    /// </summary>
    private string DescribeProvider(bool usedGpu)
    {
        if (!usedGpu)
        {
            return nameof(OcrExecutionProvider.Cpu);
        }

        // Mirrors EasyOcrServiceOptions.ToEngineOptions: the legacy UseGpu flag means "force CUDA"
        // unless an explicit provider was chosen.
        var requested = _options.ExecutionProvider;
        if (_options.UseGpu && requested is OcrExecutionProvider.Auto or OcrExecutionProvider.Cpu)
        {
            requested = OcrExecutionProvider.Cuda;
        }
        return ExecutionProviderResolver.Resolve(requested, logger: null).ToString();
    }

    /// <summary>Cached outcome of one deep probe.</summary>
    private sealed record ProbeVerdict(
        bool Success,
        DateTimeOffset At,
        TimeSpan Duration,
        string Provider,
        Exception? Error,
        long Timestamp);
}
