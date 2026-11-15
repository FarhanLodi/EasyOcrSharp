using EasyOcrSharp.Structure.Engine;
using EasyOcrSharp.Structure.Engine.Services;
using Xunit;

namespace EasyOcrSharp.Tests.StructureEngine;

/// <summary>
/// Model-free tests for the structure service plumbing fixes mirrored from PaddleOcrNet 2.2.0: the
/// diagnostics version (the assembly's, not a hard-coded 1.0.0) and argument/disposal checks on
/// <c>DetectRegionsAsync(string)</c>.
/// </summary>
public class StructureServiceInfrastructureTests
{
    [Fact]
    public void Diagnostics_version_strips_build_metadata()
    {
        Assert.Equal("2.1.0", StructureDiagnostics.ResolveVersion("2.1.0+9488cdc", null));
        Assert.Equal("2.2.0-beta.1", StructureDiagnostics.ResolveVersion("2.2.0-beta.1", null));
        Assert.Equal("1.2.3", StructureDiagnostics.ResolveVersion(null, new Version(1, 2, 3, 4)));
        Assert.Equal("0.0.0", StructureDiagnostics.ResolveVersion(" ", null));

        Assert.NotEqual("1.0.0", StructureDiagnostics.ActivitySource.Version);
        Assert.DoesNotContain("+", StructureDiagnostics.ActivitySource.Version);
    }

    private static string EmptyCache()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"easyocrsharp-structure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task DetectRegionsAsync_path_validates_arguments_and_disposal()
    {
        var cache = EmptyCache();
        try
        {
            var service = new StructureService(new StructureServiceOptions
            {
                ModelCachePath = cache,
                ExecutionProvider = OcrExecutionProvider.Cpu,
            });

            await Assert.ThrowsAsync<ArgumentException>(() => service.DetectRegionsAsync(" "));
            await Assert.ThrowsAsync<FileNotFoundException>(() => service.DetectRegionsAsync(Path.Combine(cache, "missing.png")));

            await service.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.DetectRegionsAsync(Path.Combine(cache, "missing.png")));
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }
}
