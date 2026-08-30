using System.Collections;
using System.Diagnostics.Metrics;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyOcrSharp.Diagnostics;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Models;
using EasyOcrSharp.Services;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// The production metrics contract and the concurrency/timeout governor. Every test here is CI-safe: each
/// one exercises a code path that reaches the gate and the recorder without ever loading or downloading a
/// model, so a failing OCR box still reports a failing OCR box.
/// </summary>
/// <remarks>
/// The headline guard is <see cref="A_failing_operation_records_an_error_outcome_with_the_error_type"/>:
/// the instruments used to be incremented only on the success path, so a service failing every request
/// looked idle on a dashboard rather than broken.
/// </remarks>
public class OperationMetricsTests
{
    // ------------------------------------------------------------------ harness

    /// <summary>Collects <c>easyocr.operations</c> measurements and their tags off the public meter.</summary>
    private sealed class OperationMeter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Dictionary<string, string?>> _points = new();
        private readonly Lock _gate = new();

        public OperationMeter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == EasyOcrDiagnostics.MeterName && instrument.Name == "easyocr.operations")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var map = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var tag in tags) map[tag.Key] = tag.Value?.ToString();
                lock (_gate) _points.Add(map);
            });
            _listener.Start();
        }

        /// <summary>
        /// The measurements matching an operation (and optionally an outcome). Other test classes share
        /// these process-wide instruments, so every assertion filters rather than counting everything.
        /// </summary>
        public IReadOnlyList<Dictionary<string, string?>> Points(string operation, string? outcome = null)
        {
            lock (_gate)
            {
                return _points
                    .Where(p => p.GetValueOrDefault(EasyOcrDiagnostics.TagNames.Operation) == operation)
                    .Where(p => outcome is null || p.GetValueOrDefault(EasyOcrDiagnostics.TagNames.Outcome) == outcome)
                    .ToArray();
            }
        }

        public Dictionary<string, string?> Single(string operation, string outcome)
        {
            var matches = Points(operation, outcome);
            Assert.True(matches.Count > 0, $"no '{operation}' measurement tagged outcome='{outcome}' was recorded");
            return matches[0];
        }

        public void Dispose() => _listener.Dispose();
    }

    private static Image<Rgb24> Blank() => new(16, 16);

    private static readonly IReadOnlyList<OcrPoint>[] NoRegions = Array.Empty<IReadOnlyList<OcrPoint>>();

    /// <summary>
    /// Region source that parks the operation holding it — the only way to pin a governor slot without a
    /// model, since the public method enumerates the regions after it has been admitted through the gate.
    /// </summary>
    private sealed class ParkedRegions(ManualResetEventSlim entered, ManualResetEventSlim release)
        : IEnumerable<IReadOnlyList<OcrPoint>>
    {
        public IEnumerator<IReadOnlyList<OcrPoint>> GetEnumerator()
        {
            entered.Set();
            release.Wait();
            yield break;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Languages that take <paramref name="delay"/> to materialize, burning an operation's budget.</summary>
    private static IEnumerable<string> SlowLanguages(TimeSpan delay)
    {
        Thread.Sleep(delay);
        yield return "en";
    }

    // ------------------------------------------------------------------ metrics

    [Fact]
    public async Task A_successful_operation_records_success_with_the_operation_and_provider_tags()
    {
        using var meter = new OperationMeter();
        await using var ocr = new EasyOcrService();
        using var image = Blank();

        // Zero regions short-circuits before the recognizer, so this is a complete, successful operation
        // that never touches a model.
        var result = await ocr.RecognizeRegionsAsync(image, NoRegions, new[] { "en" });
        Assert.Empty(result.Lines);

        var point = meter.Single(EasyOcrDiagnostics.OperationNames.Recognize, EasyOcrDiagnostics.Outcomes.Success);
        Assert.True(
            Enum.TryParse<OcrExecutionProvider>(point[EasyOcrDiagnostics.TagNames.Provider], out _),
            $"provider tag was '{point[EasyOcrDiagnostics.TagNames.Provider]}'");
        Assert.False(point.ContainsKey(EasyOcrDiagnostics.TagNames.ErrorType));
    }

    [Fact]
    public async Task A_failing_operation_records_an_error_outcome_with_the_error_type()
    {
        using var meter = new OperationMeter();
        await using var ocr = new EasyOcrService();
        using var image = Blank();

        // An empty language list is rejected inside the pipeline — past the gate, before any model.
        await Assert.ThrowsAsync<ArgumentException>(
            () => ocr.ExtractTextFromImage(image, Array.Empty<string>()));

        var point = meter.Single(EasyOcrDiagnostics.OperationNames.Extract, EasyOcrDiagnostics.Outcomes.Error);
        Assert.Equal(typeof(ArgumentException).FullName, point[EasyOcrDiagnostics.TagNames.ErrorType]);
    }

    [Fact]
    public async Task A_canceled_operation_is_recorded_as_canceled_rather_than_an_error()
    {
        using var meter = new OperationMeter();
        await using var ocr = new EasyOcrService();
        using var image = Blank();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ocr.ExtractTextFromImage(image, new[] { "en" }, cancellationToken: cts.Token));

        var point = meter.Single(EasyOcrDiagnostics.OperationNames.Extract, EasyOcrDiagnostics.Outcomes.Canceled);
        Assert.StartsWith("System.OperationCanceledException", point[EasyOcrDiagnostics.TagNames.ErrorType] ?? "");
    }

    // ------------------------------------------------------------------ timeout

    [Fact]
    public async Task An_operation_timeout_surfaces_as_OcrTimeoutException_not_a_bare_cancellation()
    {
        using var meter = new OperationMeter();
        await using var ocr = new EasyOcrService(new EasyOcrServiceOptions
        {
            OperationTimeout = TimeSpan.FromMilliseconds(50),
        });
        using var image = Blank();

        // The orientation sweep materializes the language list and then checks the token before its first
        // pass, so a slow language source burns the budget without a model being loaded.
        var options = RecognitionOptions.Default with
        {
            Preprocessing = PreprocessingOptions.None with { DetectOrientation = true },
        };

        await Assert.ThrowsAsync<OcrTimeoutException>(
            () => ocr.ExtractTextFromImage(image, SlowLanguages(TimeSpan.FromMilliseconds(400)), options));

        // `timeout`, not `error`: a budget overrun gets its own outcome so that alerting on
        // `outcome=error` stays a signal for genuine faults. DiagnosticsContractTests.FailureCases pins the
        // same mapping, and this assertion previously contradicted it.
        var point = meter.Single(EasyOcrDiagnostics.OperationNames.Extract, EasyOcrDiagnostics.Outcomes.Timeout);
        Assert.Equal(typeof(OcrTimeoutException).FullName, point[EasyOcrDiagnostics.TagNames.ErrorType]);
    }

    [Fact]
    public async Task A_caller_cancellation_is_not_relabelled_as_a_timeout()
    {
        // Both arrive as the same exception type through the same linked token; only the lease can tell
        // them apart, and mislabelling a client disconnect as a server timeout would page the wrong team.
        var governor = new OperationGovernor(0, Timeout.InfiniteTimeSpan, TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource();
        using var lease = await governor.AcquireAsync(EasyOcrDiagnostics.OperationNames.Extract, cts.Token);
        await cts.CancelAsync();

        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(Timeout.Infinite, lease.Token));

        Assert.Same(canceled, lease.Translate(canceled));
    }

    // ------------------------------------------------------------------ concurrency

    [Fact]
    public async Task A_full_service_sheds_the_next_operation_with_OcrBusyException()
    {
        await using var ocr = new EasyOcrService(new EasyOcrServiceOptions
        {
            MaxConcurrentOperations = 1,
            QueueTimeout = TimeSpan.Zero,
        });
        using var image = Blank();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        // Task.Run because the parked operation blocks its thread synchronously inside the gate.
        var parked = Task.Run(() => ocr.RecognizeRegionsAsync(image, new ParkedRegions(entered, release), new[] { "en" }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "the first operation never took the slot");

        try
        {
            await Assert.ThrowsAsync<OcrBusyException>(
                () => ocr.RecognizeRegionsAsync(image, NoRegions, new[] { "en" }));
        }
        finally
        {
            release.Set();
            await parked;
        }
    }

    [Fact]
    public async Task The_default_configuration_gates_nothing()
    {
        var options = new EasyOcrServiceOptions();
        Assert.Equal(0, options.MaxConcurrentOperations);
        Assert.Equal(TimeSpan.Zero, options.OperationTimeout);

        var governor = options.CreateGovernor();
        Assert.False(governor.IsEnabled);

        // The caller's own token reaches the pipeline — no linked source, so nothing new can cancel it.
        using var cts = new CancellationTokenSource();
        using var lease = await governor.AcquireAsync(EasyOcrDiagnostics.OperationNames.Extract, cts.Token);
        Assert.Equal(cts.Token, lease.Token);
    }

    [Fact]
    public async Task Concurrent_operations_are_unaffected_by_the_defaults()
    {
        await using var ocr = new EasyOcrService();
        using var image = Blank();

        var operations = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => ocr.RecognizeRegionsAsync(image, NoRegions, new[] { "en" })))
            .ToArray();

        var results = await Task.WhenAll(operations);   // no OcrBusyException, no OcrTimeoutException
        Assert.All(results, r => Assert.Empty(r.Lines));
    }

    [Fact]
    public async Task Disposal_completes_while_an_operation_is_queued_behind_the_gate()
    {
        var ocr = new EasyOcrService(new EasyOcrServiceOptions { MaxConcurrentOperations = 1 });
        using var image = Blank();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var parked = Task.Run(() => ocr.RecognizeRegionsAsync(image, new ParkedRegions(entered, release), new[] { "en" }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "the first operation never took the slot");

        // Queued at the gate, so it is NOT registered as an in-flight operation: were it counted, disposal
        // would have to wait out the whole queue as well as the work actually running.
        var queued = Task.Run(() => ocr.RecognizeRegionsAsync(image, NoRegions, new[] { "en" }));

        var disposal = ocr.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted, "disposal must wait for the operation still touching the sessions");

        release.Set();
        await parked;
        Assert.Same(disposal, await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromSeconds(30))));
        await disposal;

        // The queued call gets its slot only after disposal has started, and is refused cleanly.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
    }
}
