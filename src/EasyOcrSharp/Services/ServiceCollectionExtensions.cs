using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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

        services.TryAddSingleton(options);
        services.AddSingleton<IEasyOcrService>(sp =>
        {
            var logger = sp.GetService<ILogger<EasyOcrService>>();
            return new EasyOcrService(options, logger);
        });

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
    {
        var langs = languages?.ToArray() ?? Array.Empty<string>();
        builder.Services.AddSingleton(sp =>
        {
            var options = sp.GetService<EasyOcrServiceOptions>() ?? new EasyOcrServiceOptions();
            return new EasyOcrHealthCheck(options, langs, failureStatus);
        });
        return builder.Add(new HealthCheckRegistration(
            name,
            sp => sp.GetRequiredService<EasyOcrHealthCheck>(),
            failureStatus,
            tags: new[] { "ocr", "easyocr" }));
    }
}
