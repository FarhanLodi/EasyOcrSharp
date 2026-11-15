using System.Diagnostics;
using System.Diagnostics.Metrics;
using EasyOcrSharp.Structure.Engine;
using Xunit;

namespace EasyOcrSharp.Tests.StructureEngine;

public sealed class StructureStageDiagnosticsTests
{
    [Fact]
    public void Stage_records_duration_and_closes_trace_even_when_work_throws()
    {
        var measurements = new List<(double Value, string? Stage)>();
        Activity? completed = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == StructureDiagnostics.MeterName && instrument.Name == "paddleocr.stage.duration")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "stage" && Equals(tag.Value, "diagnostic_test"))
                    measurements.Add((value, tag.Value as string));
        });
        listener.Start();
        using var tracing = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StructureDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "paddleocr.stage.diagnostic_test") completed = activity;
            },
        };
        ActivitySource.AddActivityListener(tracing);
        var parent = Activity.Current;
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var stage = StructureDiagnostics.MeasureStage("diagnostic_test");
            throw new InvalidOperationException("simulated failure");
        }));
        var measurement = Assert.Single(measurements);
        Assert.True(measurement.Value >= 0);
        Assert.Equal("diagnostic_test", measurement.Stage);
        Assert.NotNull(completed);
        Assert.Same(parent, Activity.Current);
        Assert.Empty(completed.TagObjects);
    }
}
