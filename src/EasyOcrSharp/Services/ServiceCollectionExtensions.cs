using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EasyOcrSharp.Services;

/// <summary>
/// Dependency-injection helpers for registering EasyOcrSharp.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEasyOcrService"/> (implemented by <see cref="EasyOcrService"/>) as a
    /// singleton. ONNX sessions are expensive to create and thread-safe to reuse, so a singleton is
    /// the recommended lifetime. The configured <see cref="EasyOcrServiceOptions"/> is also registered
    /// so add-ons (e.g. the health check) can read it.
    /// </summary>
    public static IServiceCollection AddEasyOcrSharp(
        this IServiceCollection services,
        Action<EasyOcrServiceOptions>? configure = null)
    {
        var options = new EasyOcrServiceOptions();
        configure?.Invoke(options);

        // Both registrations use TryAdd, so they stay in step. Previously the options were TryAdd (first
        // call wins) while the service was Add (every call appends), so a library and its host app both
        // calling AddEasyOcrSharp left EasyOcrHealthCheck reading the FIRST options object while the
        // resolved service used the SECOND -- a readiness probe reporting on a different cache path and
        // language set than the service actually served. Enumerating IEasyOcrService also built two full
        // engines, each with its own ONNX sessions.
        services.TryAddSingleton(options);
        services.TryAddSingleton<IEasyOcrService>(sp =>
        {
            var logger = sp.GetService<ILogger<EasyOcrService>>();
            return new EasyOcrService(options, logger);
        });

        return services;
    }

    /// <summary>
    /// Registers <see cref="EasyOcrWarmUpService"/> — a hosted service that preloads the models for
    /// <paramref name="languages"/> in the background at startup — together with the shared
    /// <see cref="EasyOcrWarmUpState"/> singleton that reports how it went.
    /// <para>
    /// Warm-up runs off the startup path and never throws: a host that cannot reach the model mirror
    /// still starts and reports its state through the health check instead of crash-looping. Requires
    /// <see cref="AddEasyOcrSharp"/> (or another <see cref="IEasyOcrService"/> registration).
    /// </para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="languages">Languages to preload, e.g. <c>"en"</c>. Empty means "nothing to preload".</param>
    /// <example>
    /// <code>
    /// builder.Services.AddEasyOcrSharp(o => o.ModelCachePath = "/models");
    /// builder.Services.AddEasyOcrWarmUp("en");
    /// </code>
    /// </example>
    public static IServiceCollection AddEasyOcrWarmUp(
        this IServiceCollection services,
        params string[] languages)
    {
        ArgumentNullException.ThrowIfNull(services);
        var langs = languages ?? Array.Empty<string>();

        // Same TryAdd discipline as AddEasyOcrSharp, and for the same reason: the state singleton and the
        // hosted service that writes it must stay in step. TryAddEnumerable de-duplicates on the
        // implementation type, so a library and its host app both calling AddEasyOcrWarmUp get ONE warm-up
        // publishing to the ONE state the health check reads -- not a second warm-up racing the first and
        // a state object nothing ever writes to.
        services.TryAddSingleton<EasyOcrWarmUpState>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, EasyOcrWarmUpService>(sp =>
            new EasyOcrWarmUpService(
                sp.GetRequiredService<IEasyOcrService>(),
                langs,
                sp.GetRequiredService<EasyOcrWarmUpState>(),
                sp.GetService<ILogger<EasyOcrWarmUpService>>())));

        return services;
    }

    /// <summary>
    /// Adds a health check that verifies the model cache is accessible and (optionally) that the
    /// models for the given <paramref name="languages"/> are already present on disk — so a probe can
    /// distinguish "ready to serve" from "will download on first request".
    /// </summary>
    /// <param name="builder">The health-checks builder (from <c>services.AddHealthChecks()</c>).</param>
    /// <param name="languages">Languages whose models should be present for a Healthy result. Empty = cache check only.</param>
    /// <param name="name">Health check name. Defaults to <c>easyocr</c>.</param>
    /// <param name="failureStatus">Status reported when models are missing. Defaults to <see cref="HealthStatus.Degraded"/>.</param>
    public static IHealthChecksBuilder AddEasyOcrHealthCheck(
        this IHealthChecksBuilder builder,
        IEnumerable<string>? languages = null,
        string name = "easyocr",
        HealthStatus failureStatus = HealthStatus.Degraded)
        => AddEasyOcrHealthCheckCore(builder, languages, name, failureStatus, probeOptions: null);

    /// <summary>
    /// Adds a health check that additionally <b>self-tests the pipeline</b>: with
    /// <see cref="EasyOcrHealthCheckOptions.DeepProbe"/> set it runs a tiny synthetic image through the
    /// real OCR pipeline (at most once per <see cref="EasyOcrHealthCheckOptions.ProbeInterval"/>) and
    /// reports the execution provider that actually resolved. Use this when "the files are on disk" is
    /// not enough — a truncated model, a half-copied cache volume or a GPU provider that fails at session
    /// initialization all pass the file check and then fail every request.
    /// <para>
    /// The probe never downloads anything: if the models for <paramref name="languages"/> are not cached
    /// yet, the check reports that instead of probing. When <see cref="AddEasyOcrWarmUp"/> is also
    /// registered, the check reports "not ready" while warm-up is running and surfaces a warm-up failure.
    /// </para>
    /// </summary>
    /// <param name="builder">The health-checks builder (from <c>services.AddHealthChecks()</c>).</param>
    /// <param name="probeOptions">Deep-probe settings (off by default — set <see cref="EasyOcrHealthCheckOptions.DeepProbe"/>).</param>
    /// <param name="languages">Languages whose models must be present, and which the probe exercises. Required: recognition needs a language pack.</param>
    /// <param name="name">Health check name. Defaults to <c>easyocr</c>.</param>
    /// <param name="failureStatus">Status reported when models are missing. Defaults to <see cref="HealthStatus.Degraded"/>.</param>
    /// <example>
    /// <code>
    /// builder.Services.AddHealthChecks()
    ///     .AddEasyOcrHealthCheck(new EasyOcrHealthCheckOptions { DeepProbe = true }, new[] { "en" }, name: "easyocr-ready");
    /// </code>
    /// </example>
    public static IHealthChecksBuilder AddEasyOcrHealthCheck(
        this IHealthChecksBuilder builder,
        EasyOcrHealthCheckOptions probeOptions,
        IEnumerable<string> languages,
        string name = "easyocr",
        HealthStatus failureStatus = HealthStatus.Degraded)
    {
        ArgumentNullException.ThrowIfNull(probeOptions);
        return AddEasyOcrHealthCheckCore(builder, languages, name, failureStatus, probeOptions);
    }

    private static IHealthChecksBuilder AddEasyOcrHealthCheckCore(
        IHealthChecksBuilder builder,
        IEnumerable<string>? languages,
        string name,
        HealthStatus failureStatus,
        EasyOcrHealthCheckOptions? probeOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var langs = languages?.ToArray() ?? Array.Empty<string>();

        EasyOcrHealthCheck Create(IServiceProvider sp) => new(
            sp.GetService<EasyOcrServiceOptions>() ?? new EasyOcrServiceOptions(),
            langs,
            failureStatus,
            // Optional on purpose: without AddEasyOcrSharp there is nothing to probe with, and the check
            // then behaves exactly as it always has (file presence only).
            sp.GetService<IEasyOcrService>(),
            probeOptions,
            sp.GetService<EasyOcrWarmUpState>());

        // Kept so apps that inject EasyOcrHealthCheck directly (or resolve it to build their own
        // response writer) keep working.
        builder.Services.AddSingleton(Create);

        // The registration holds its OWN lazily-created instance rather than resolving the singleton
        // above. Two reasons: the health-check system invokes this factory on every probe run, so a
        // fresh instance each time would throw away the deep probe's cached verdict and run OCR on every
        // poll; and an app that registers a shallow "live" check plus a deep "ready" check would
        // otherwise have both names resolve the single last-registered singleton, silently giving the
        // liveness probe the readiness probe's configuration.
        EasyOcrHealthCheck? instance = null;
        var gate = new object();
        return builder.Add(new HealthCheckRegistration(
            name,
            sp =>
            {
                lock (gate)
                {
                    return instance ??= Create(sp);
                }
            },
            failureStatus,
            tags: new[] { "ocr", "easyocr" }));
    }
}
