using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EasyOcrSharp.Services;

/// <summary>
/// Options used when registering <see cref="EasyOcrService"/> in a DI container.
/// </summary>
public sealed class EasyOcrServiceOptions
{
    /// <summary>Optional model cache directory (defaults to LocalAppData or EASYOCRSHARP_CACHE).</summary>
    public string? ModelCachePath { get; set; }

    /// <summary>Request the CUDA execution provider (needs EasyOcrSharp.Gpu). Falls back to CPU.</summary>
    public bool UseGpu { get; set; }
}

/// <summary>
/// Dependency-injection helpers for registering EasyOcrSharp.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEasyOcrService"/> (implemented by <see cref="EasyOcrService"/>) as a
    /// singleton. ONNX sessions are expensive to create and thread-safe to reuse, so a singleton is
    /// the recommended lifetime.
    /// </summary>
    public static IServiceCollection AddEasyOcrSharp(
        this IServiceCollection services,
        Action<EasyOcrServiceOptions>? configure = null)
    {
        var options = new EasyOcrServiceOptions();
        configure?.Invoke(options);

        services.AddSingleton<IEasyOcrService>(sp =>
        {
            var logger = sp.GetService<ILogger<EasyOcrService>>();
            return new EasyOcrService(options.ModelCachePath, logger, options.UseGpu);
        });

        return services;
    }
}
