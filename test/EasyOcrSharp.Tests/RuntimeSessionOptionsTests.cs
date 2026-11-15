using EasyOcrSharp.Internal;
using EasyOcrSharp.Services;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// <see cref="EasyOcrServiceOptions.AllowIntraOpSpinning"/> and <see cref="EasyOcrServiceOptions.CudnnConvAlgoSearch"/>
/// reach every ONNX Runtime session the service builds, and leave ONNX Runtime's defaults untouched while
/// they are null (the default).
/// </summary>
public class RuntimeSessionOptionsTests
{
    [Fact]
    public void Both_knobs_default_to_the_runtime_default()
    {
        var options = new EasyOcrServiceOptions();
        Assert.Null(options.AllowIntraOpSpinning);
        Assert.Null(options.CudnnConvAlgoSearch);

        var engine = options.ToEngineOptions();
        Assert.Null(engine.AllowIntraOpSpinning);
        Assert.Null(engine.CudnnConvAlgoSearch);

        // Nothing is added to the session and the CUDA provider is appended with its own defaults.
        Assert.Empty(ExecutionProviderResolver.SessionConfigEntriesFor(engine));
        Assert.Null(ExecutionProviderResolver.CudaProviderOptionsFor(engine));
    }

    [Fact]
    public void Options_map_onto_the_engine_configuration()
    {
        var engine = new EasyOcrServiceOptions
        {
            AllowIntraOpSpinning = false,
            CudnnConvAlgoSearch = CudnnConvolutionAlgorithmSearch.Heuristic,
        }.ToEngineOptions();

        Assert.False(engine.AllowIntraOpSpinning);
        Assert.Equal(CudnnConvolutionAlgorithmSearch.Heuristic, engine.CudnnConvAlgoSearch);
    }

    [Theory]
    [InlineData(true, "1")]
    [InlineData(false, "0")]
    public void Spinning_choice_becomes_the_session_config_entry(bool spin, string expected)
    {
        var entries = ExecutionProviderResolver.SessionConfigEntriesFor(new EngineOptions { AllowIntraOpSpinning = spin });

        var entry = Assert.Single(entries);
        Assert.Equal("session.intra_op.allow_spinning", entry.Key);
        Assert.Equal(expected, entry.Value);
    }

    [Theory]
    [InlineData(CudnnConvolutionAlgorithmSearch.Exhaustive, "EXHAUSTIVE")]
    [InlineData(CudnnConvolutionAlgorithmSearch.Heuristic, "HEURISTIC")]
    [InlineData(CudnnConvolutionAlgorithmSearch.Default, "DEFAULT")]
    public void Cudnn_search_maps_to_the_cuda_provider_option(CudnnConvolutionAlgorithmSearch search, string expected)
    {
        var values = ExecutionProviderResolver.CudaProviderOptionsFor(new EngineOptions { CudnnConvAlgoSearch = search });

        Assert.NotNull(values);
        Assert.Equal(expected, values!["cudnn_conv_algo_search"]);
        Assert.Equal("0", values["device_id"]);   // the same device the parameterless append uses
        Assert.Equal(2, values.Count);
    }

    [Fact]
    public void An_undefined_search_value_falls_back_to_the_runtime_default()
        => Assert.Equal("EXHAUSTIVE", ExecutionProviderResolver.CudnnSearchValue((CudnnConvolutionAlgorithmSearch)42));

    [Theory]
    [InlineData(OcrExecutionProvider.Cpu, true)]
    [InlineData(OcrExecutionProvider.Cpu, false)]
    [InlineData(OcrExecutionProvider.Cuda, true)]
    [InlineData(OcrExecutionProvider.Cuda, false)]
    public void Sessions_build_with_the_knobs_set_and_report_truthfully(OcrExecutionProvider provider, bool perBoxParallel)
    {
        // ONNX Runtime accepts the config entry, and a CUDA request with a cuDNN choice still never throws:
        // on a host without CUDA it degrades to CPU with a hint, exactly like the plain append.
        var engine = new EngineOptions
        {
            AllowIntraOpSpinning = false,
            CudnnConvAlgoSearch = CudnnConvolutionAlgorithmSearch.Heuristic,
        };

        var result = ExecutionProviderResolver.BuildSessionOptionsWithStatus(provider, engine, logger: null, perBoxParallel);
        using var options = result.Options;

        if (result.ActiveProvider == provider)
        {
            Assert.Null(result.ProviderFailureHint);
        }
        else
        {
            Assert.Equal(OcrExecutionProvider.Cpu, result.ActiveProvider);
            Assert.False(string.IsNullOrWhiteSpace(result.ProviderFailureHint));
        }
    }

    [Fact]
    public void Structure_engine_options_carry_the_knobs_and_default_to_null()
    {
        var unset = new EasyOcrSharp.Structure.Engine.Services.StructureServiceOptions().ToEngineOptions();
        Assert.Null(unset.AllowIntraOpSpinning);
        Assert.Null(unset.CudnnConvAlgoSearch);

        var set = new EasyOcrSharp.Structure.Engine.Services.StructureServiceOptions
        {
            AllowIntraOpSpinning = false,
            CudnnConvAlgoSearch = CudnnConvolutionAlgorithmSearch.Default,
        }.ToEngineOptions();
        Assert.False(set.AllowIntraOpSpinning);
        Assert.Equal(CudnnConvolutionAlgorithmSearch.Default, set.CudnnConvAlgoSearch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Structure_engine_sessions_build_with_the_knobs_set(bool cuda)
    {
        var provider = cuda
            ? EasyOcrSharp.Structure.Engine.Services.OcrExecutionProvider.Cuda
            : EasyOcrSharp.Structure.Engine.Services.OcrExecutionProvider.Cpu;
        var engine = new EasyOcrSharp.Structure.Engine.StructureEngineOptions
        {
            AllowIntraOpSpinning = false,
            CudnnConvAlgoSearch = CudnnConvolutionAlgorithmSearch.Heuristic,
        };

        var result = EasyOcrSharp.Structure.Engine.ExecutionProviderResolver.BuildSessionOptionsWithStatus(provider, engine, logger: null);
        using var options = result.Options;

        // On a host without CUDA the attach fails over to CPU with a hint; it must never throw.
        if (result.ActiveProvider != provider)
        {
            Assert.Equal(EasyOcrSharp.Structure.Engine.Services.OcrExecutionProvider.Cpu, result.ActiveProvider);
            Assert.False(string.IsNullOrWhiteSpace(result.ProviderFailureHint));
        }
    }

    [Fact]
    public void Service_constructs_with_the_knobs_set()
    {
        using var service = new EasyOcrService(new EasyOcrServiceOptions
        {
            ExecutionProvider = OcrExecutionProvider.Cpu,
            AllowIntraOpSpinning = false,
            CudnnConvAlgoSearch = CudnnConvolutionAlgorithmSearch.Heuristic,
        });

        Assert.Equal(OcrExecutionProvider.Cpu, service.ActiveExecutionProvider);
    }
}
