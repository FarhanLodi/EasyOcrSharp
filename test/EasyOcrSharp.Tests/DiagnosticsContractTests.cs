using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using EasyOcrSharp.Diagnostics;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Contract tests for the telemetry surface: the version stamped on every metric and span, and the
/// measurements <see cref="OcrOperationRecorder"/> emits.
/// <para>
/// Nothing here touches the network or a model. Every test tags its recorder with a name unique to that
/// test and filters captured measurements by it, so a real OCR running in a parallel test collection on
/// the same meter cannot pollute the assertions.
/// </para>
/// </summary>
public class DiagnosticsContractTests
{
    private const string Provider = "Cpu";

    /// <summary>A per-test operation name, so captured measurements can be attributed unambiguously.</summary>
    private static string UniqueOperation() => "test_" + Guid.NewGuid().ToString("N");

    private sealed record Measurement(string Instrument, double Value, Dictionary<string, string?> Tags)
    {
        public string? Tag(string key) => Tags.TryGetValue(key, out var value) ? value : null;
        public bool HasTag(string key) => Tags.ContainsKey(key);
    }

    /// <summary>
    /// Runs <paramref name="action"/> with a <see cref="MeterListener"/> attached to the library meter and
    /// returns only the measurements tagged with <paramref name="operation"/>.
    /// </summary>
    private static List<Measurement> Capture(string operation, Action action)
    {
        var captured = new List<Measurement>();
        var gate = new object();

        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == EasyOcrDiagnostics.MeterName) l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.Start();

        try
        {
            action();
        }
        finally
        {
            listener.Dispose();   // flush before reading, as the existing telemetry test does
        }

        lock (gate)
        {
            return captured
                .Where(m => m.Tag(EasyOcrDiagnostics.TagNames.Operation) == operation)
                .ToList();
        }

        void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var map = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var tag in tags) map[tag.Key] = tag.Value?.ToString();
            lock (gate) captured.Add(new Measurement(instrument.Name, value, map));
        }
    }

    private static Measurement Single(List<Measurement> measurements, string instrument)
        => Assert.Single(measurements, m => m.Instrument == instrument);

    // ------------------------------------------------------------------ version consistency

    /// <summary>
    /// Walks up from the compiled test source and from the output directory looking for the library
    /// csproj. Returns null when neither is available (binaries copied to another machine), so the caller
    /// can skip rather than fail.
    /// </summary>
    private static string? FindLibraryCsproj([CallerFilePath] string? testSourcePath = null)
    {
        var starts = new[] { Path.GetDirectoryName(testSourcePath), AppContext.BaseDirectory };

        foreach (var start in starts)
        {
            if (string.IsNullOrEmpty(start) || !Directory.Exists(start)) continue;

            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "src", "EasyOcrSharp", "EasyOcrSharp.csproj");
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// REGRESSION GUARD. The version constant behind the meter and activity source is hand-maintained and
    /// silently drifted: it read "2.2.1" while the package shipped as 3.0.1, so every metric and every span
    /// a 3.x deployment emitted was labelled with a version two releases old. Nothing fails when that
    /// happens — the data is simply wrong, and stays wrong until someone correlates a dashboard against a
    /// deployment and does not believe what they see.
    /// <para>
    /// Asserted against the ActivitySource and Meter rather than the private constant, because those are
    /// what actually reach an exporter.
    /// </para>
    /// </summary>
    [SkippableFact]
    public void Telemetry_version_matches_the_package_version()
    {
        var csproj = FindLibraryCsproj();
        Skip.If(csproj is null, "src/EasyOcrSharp/EasyOcrSharp.csproj not reachable from the test output — nothing to compare against.");

        var packageVersion = XDocument.Load(csproj)
            .Root?
            .Elements("PropertyGroup")
            .Elements("Version")
            .FirstOrDefault()?
            .Value
            .Trim();

        Skip.If(string.IsNullOrWhiteSpace(packageVersion), $"No <Version> element found in {csproj}.");

        Assert.Equal(packageVersion, EasyOcrDiagnostics.ActivitySource.Version);
        Assert.Equal(packageVersion, EasyOcrDiagnostics.Meter.Version);
    }

    /// <summary>The meter and activity source must stay named alike; exporters are configured by name.</summary>
    [Fact]
    public void Meter_and_activity_source_names_match_the_published_constants()
    {
        Assert.Equal(EasyOcrDiagnostics.MeterName, EasyOcrDiagnostics.Meter.Name);
        Assert.Equal(EasyOcrDiagnostics.ActivitySourceName, EasyOcrDiagnostics.ActivitySource.Name);
    }

    /// <summary>
    /// Two entry points sharing an operation name would silently merge their latency series — a 40 ms
    /// region recognition averaged into a 4 s document analysis produces a number describing neither.
    /// </summary>
    [Fact]
    public void Operation_and_outcome_names_are_unique_and_non_empty()
    {
        static string[] ConstantsOf(Type type) => type
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        foreach (var type in new[] { typeof(EasyOcrDiagnostics.OperationNames), typeof(EasyOcrDiagnostics.Outcomes), typeof(EasyOcrDiagnostics.TagNames) })
        {
            var values = ConstantsOf(type);
            Assert.NotEmpty(values);
            Assert.All(values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
            Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
        }
    }

    // ------------------------------------------------------------------ what Dispose emits

    /// <summary>A completed operation emits exactly one count and one duration sample, tagged alike.</summary>
    [Fact]
    public void Disposing_a_recorder_emits_operations_and_duration()
    {
        var operation = UniqueOperation();

        var measurements = Capture(operation, () =>
        {
            using var recorder = EasyOcrDiagnostics.Begin(operation, Provider);
            recorder.Success();
        });

        var operations = Single(measurements, "easyocr.operations");
        Assert.Equal(1, operations.Value);
        Assert.Equal(Provider, operations.Tag(EasyOcrDiagnostics.TagNames.Provider));
        Assert.Equal(EasyOcrDiagnostics.Outcomes.Success, operations.Tag(EasyOcrDiagnostics.TagNames.Outcome));

        var duration = Single(measurements, "easyocr.duration");
        Assert.True(duration.Value >= 0, "Duration must be a non-negative millisecond figure.");
        Assert.Equal(EasyOcrDiagnostics.Outcomes.Success, duration.Tag(EasyOcrDiagnostics.TagNames.Outcome));

        // A success carries no error.type — an always-present tag would break error-rate queries.
        Assert.False(operations.HasTag(EasyOcrDiagnostics.TagNames.ErrorType));
    }

    /// <summary>
    /// THE DELIBERATE DEFAULT. Metrics used to be incremented only on the success path, so a deployment
    /// failing 100% of requests reported <i>zero operations</i> — indistinguishable from an idle service,
    /// and the single most important signal an operator could be denied. A recorder disposed without
    /// <c>Success()</c> — the shape a thrown exception leaves behind — must record an error.
    /// </summary>
    [Fact]
    public void A_recorder_disposed_without_Success_records_an_error()
    {
        var operation = UniqueOperation();

        var measurements = Capture(operation, () =>
        {
            using var recorder = EasyOcrDiagnostics.Begin(operation, Provider);
            // No Success(), no Failure() — exactly what an exception escaping the operation leaves behind.
        });

        var operations = Single(measurements, "easyocr.operations");
        Assert.Equal(1, operations.Value);
        Assert.Equal(EasyOcrDiagnostics.Outcomes.Error, operations.Tag(EasyOcrDiagnostics.TagNames.Outcome));

        // Nothing classified the failure, so no error.type is invented.
        Assert.False(operations.HasTag(EasyOcrDiagnostics.TagNames.ErrorType));
    }

    /// <summary>
    /// An operation that actually throws still emits a data point, even though the throw happened before
    /// any bookkeeping — the whole point of doing the work in <c>Dispose</c>.
    /// </summary>
    [Fact]
    public void An_operation_that_throws_still_emits_a_measurement()
    {
        var operation = UniqueOperation();

        var measurements = Capture(operation, () =>
        {
            try
            {
                using var recorder = EasyOcrDiagnostics.Begin(operation, Provider);
                throw new InvalidOperationException("boom");
            }
            catch (InvalidOperationException)
            {
                // swallowed: the assertion is that the measurement escaped anyway
            }
        });

        var operations = Single(measurements, "easyocr.operations");
        Assert.Equal(EasyOcrDiagnostics.Outcomes.Error, operations.Tag(EasyOcrDiagnostics.TagNames.Outcome));
    }

    // ------------------------------------------------------------------ failure classification

    public static TheoryData<string, string> FailureCases() => new()
    {
        // A cancellation is a caller decision, not a fault: counting shutdown or client-disconnect traffic
        // as errors turns every deploy into an error-rate spike.
        { "canceled", EasyOcrDiagnostics.Outcomes.Canceled },
        { "task-canceled", EasyOcrDiagnostics.Outcomes.Canceled },
        // Shedding and budget overruns get their own outcomes rather than folding into `error`, because
        // they call for different responses and one shared error rate cannot express that. OcrBusyException
        // is the concurrency limit doing its job under a burst — "add capacity", not "wake someone" — so a
        // traffic spike the service handled exactly as configured must not fire the error-budget alert.
        // OcrTimeoutException points at a single pathological input to quarantine. Only what is left is a
        // genuine fault, which is what makes alerting on `outcome=error` alone meaningful.
        { "ocr-timeout", EasyOcrDiagnostics.Outcomes.Timeout },
        { "ocr-busy", EasyOcrDiagnostics.Outcomes.Shed },
        { "other", EasyOcrDiagnostics.Outcomes.Error },
    };

    private static Exception MakeException(string key) => key switch
    {
        "canceled" => new OperationCanceledException(),
        "task-canceled" => new TaskCanceledException(),
        "ocr-timeout" => new OcrTimeoutException("too slow"),
        "ocr-busy" => new OcrBusyException("all slots busy"),
        _ => new InvalidOperationException("boom"),
    };

    /// <summary>
    /// <c>Failure</c> must map each exception onto the right outcome and record the exception type under
    /// the OpenTelemetry-conventional <c>error.type</c> key, so existing error dashboards pick it up
    /// without library-specific configuration.
    /// </summary>
    [Theory]
    [MemberData(nameof(FailureCases))]
    public void Failure_classifies_the_outcome_and_records_the_error_type(string exceptionKey, string expectedOutcome)
    {
        var operation = UniqueOperation();
        var exception = MakeException(exceptionKey);

        var measurements = Capture(operation, () =>
        {
            using var recorder = EasyOcrDiagnostics.Begin(operation, Provider);
            recorder.Failure(exception);
        });

        var operations = Single(measurements, "easyocr.operations");
        Assert.Equal(expectedOutcome, operations.Tag(EasyOcrDiagnostics.TagNames.Outcome));
        Assert.Equal(exception.GetType().FullName, operations.Tag(EasyOcrDiagnostics.TagNames.ErrorType));

        // The duration sample carries the same tag set, so latency can be sliced by outcome.
        var duration = Single(measurements, "easyocr.duration");
        Assert.Equal(expectedOutcome, duration.Tag(EasyOcrDiagnostics.TagNames.Outcome));
    }

    /// <summary>
    /// <c>Canceled()</c> exists for the case with no exception to inspect — a consumer that walks away
    /// from a streaming enumeration part-way through. Abandoning a stream is a choice, not a fault.
    /// </summary>
    [Fact]
    public void Canceled_records_the_cancellation_outcome_without_an_error_type()
    {
        var operation = UniqueOperation();

        var measurements = Capture(operation, () =>
        {
            using var recorder = EasyOcrDiagnostics.Begin(operation, Provider);
            recorder.Canceled();
        });

        var operations = Single(measurements, "easyocr.operations");
        Assert.Equal(EasyOcrDiagnostics.Outcomes.Canceled, operations.Tag(EasyOcrDiagnostics.TagNames.Outcome));
        Assert.False(operations.HasTag(EasyOcrDiagnostics.TagNames.ErrorType));
    }

    // ------------------------------------------------------------------ line / page counters

    /// <summary>Line and page counts accumulate across calls and are emitted once, on disposal.</summary>
    [Fact]
    public void AddLines_and_AddPages_feed_their_own_instruments()
    {
        var operation = UniqueOperation();

        var measurements = Capture(operation, () =>
        {
            using var recorder = EasyOcrDiagnostics.Begin(operation, Provider);
            recorder.AddLines(3).AddLines(4).AddPages(2);
            recorder.Success();
        });

        Assert.Equal(7, Single(measurements, "easyocr.lines").Value);
        Assert.Equal(2, Single(measurements, "easyocr.pages").Value);

        // And they carry the same tag set as the operation itself.
        Assert.Equal(Provider, Single(measurements, "easyocr.lines").Tag(EasyOcrDiagnostics.TagNames.Provider));
    }

    /// <summary>
    /// A zero count must emit nothing at all. A blank page that dutifully reports "0 lines" creates a
    /// permanent zero-valued series per tag combination in the backend — cardinality and storage spent on
    /// a data point that says nothing a missing point would not.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Zero_or_negative_line_and_page_counts_are_not_recorded(int count)
    {
        var operation = UniqueOperation();

        var measurements = Capture(operation, () =>
        {
            using var recorder = EasyOcrDiagnostics.Begin(operation, Provider);
            recorder.AddLines(count).AddPages(count);
            recorder.Success();
        });

        Assert.DoesNotContain(measurements, m => m.Instrument == "easyocr.lines");
        Assert.DoesNotContain(measurements, m => m.Instrument == "easyocr.pages");

        // The operation itself is still counted — only the empty sub-counters are suppressed.
        Assert.Equal(1, Single(measurements, "easyocr.operations").Value);
    }

    /// <summary>An operation that never counts lines or pages emits neither instrument.</summary>
    [Fact]
    public void An_operation_with_no_lines_or_pages_emits_neither_instrument()
    {
        var operation = UniqueOperation();

        var measurements = Capture(operation, () =>
        {
            using var recorder = EasyOcrDiagnostics.Begin(operation, Provider);
            recorder.Success();
        });

        // Anchored on a positive assertion so a broken tag filter cannot make this pass vacuously.
        Assert.Equal(1, Single(measurements, "easyocr.operations").Value);
        Assert.DoesNotContain(measurements, m => m.Instrument == "easyocr.lines");
        Assert.DoesNotContain(measurements, m => m.Instrument == "easyocr.pages");
    }

    // ------------------------------------------------------------------ idempotence

    /// <summary>
    /// Disposing twice must not double-count. A duplicated data point is worse than a missing one: it
    /// silently doubles a throughput graph, and nothing in the pipeline can tell it was an accident.
    /// </summary>
    [Fact]
    public void Dispose_is_idempotent()
    {
        var operation = UniqueOperation();

        var measurements = Capture(operation, () =>
        {
            var recorder = EasyOcrDiagnostics.Begin(operation, Provider);
            recorder.AddLines(5);
            recorder.Success();
            recorder.Dispose();
            recorder.Dispose();   // must record nothing further
        });

        Assert.Equal(1, Single(measurements, "easyocr.operations").Value);
        Assert.Equal(5, Single(measurements, "easyocr.lines").Value);
        Single(measurements, "easyocr.duration");
    }

    /// <summary>
    /// Exactly one operation data point per recorder lifetime, whatever the outcome. This is the balance
    /// property that lets a rate of <c>easyocr.operations</c> be trusted as a request rate.
    /// </summary>
    [Fact]
    public void Every_recorder_lifetime_contributes_exactly_one_operation_count()
    {
        var operation = UniqueOperation();

        var measurements = Capture(operation, () =>
        {
            for (int i = 0; i < 5; i++)
            {
                using var recorder = EasyOcrDiagnostics.Begin(operation, Provider);
                if (i % 2 == 0) recorder.Success(); else recorder.Failure(new InvalidOperationException());
            }
        });

        var counts = measurements.Where(m => m.Instrument == "easyocr.operations").ToList();
        Assert.Equal(5, counts.Count);
        Assert.All(counts, m => Assert.Equal(1, m.Value));
        Assert.Equal(5, measurements.Count(m => m.Instrument == "easyocr.duration"));
    }

    // ------------------------------------------------------------------ languages (span only)

    /// <summary>
    /// Records a span for one recorder lifetime and returns it, with a listener scoped to this test.
    /// </summary>
    private static Activity? RecordSpan(string operation, Action<OcrOperationRecorder> configure)
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == EasyOcrDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        var activity = EasyOcrDiagnostics.ActivitySource.StartActivity(operation);
        using (var recorder = EasyOcrDiagnostics.Begin(operation, Provider).Annotate(activity))
        {
            configure(recorder);
        }

        activity?.Stop();
        return activity;
    }

    /// <summary>
    /// The languages tag is written only when the operation actually resolved some. An always-present tag
    /// with an empty value is a filter that silently matches everything.
    /// </summary>
    [Fact]
    public void The_languages_tag_is_present_on_the_span_only_when_set()
    {
        var withLanguages = RecordSpan(UniqueOperation(), r => r.WithLanguages(new[] { "en", "fr" }).Success());
        Assert.NotNull(withLanguages);
        Assert.Equal("en,fr", withLanguages.GetTagItem(EasyOcrDiagnostics.TagNames.Languages));

        var withoutLanguages = RecordSpan(UniqueOperation(), r => r.Success());
        Assert.NotNull(withoutLanguages);
        Assert.Null(withoutLanguages.GetTagItem(EasyOcrDiagnostics.TagNames.Languages));

        // A null list is treated the same as never calling it.
        var nullLanguages = RecordSpan(UniqueOperation(), r => r.WithLanguages(null).Success());
        Assert.NotNull(nullLanguages);
        Assert.Null(nullLanguages.GetTagItem(EasyOcrDiagnostics.TagNames.Languages));
    }

    /// <summary>
    /// Languages reach the span but deliberately NOT the metric tag set: a per-language-combination series
    /// multiplies the cardinality of every instrument, and the trace already carries the detail for anyone
    /// who needs to drill in. This pins that split so it cannot be "tidied" into the shared tag list.
    /// </summary>
    [Fact]
    public void Languages_do_not_reach_the_metric_tag_set()
    {
        var operation = UniqueOperation();

        var measurements = Capture(operation, () =>
        {
            using var recorder = EasyOcrDiagnostics.Begin(operation, Provider);
            recorder.WithLanguages(new[] { "en", "fr" }).Success();
        });

        Assert.NotEmpty(measurements);
        Assert.All(measurements, m => Assert.False(m.HasTag(EasyOcrDiagnostics.TagNames.Languages)));
    }

    // ------------------------------------------------------------------ span status

    /// <summary>
    /// Only a genuine failure colours the span red. Marking cancelled calls as errors buries the failures
    /// worth looking at under a wall of client disconnects.
    /// </summary>
    [Fact]
    public void Only_a_failure_sets_the_span_status_to_error()
    {
        var failed = RecordSpan(UniqueOperation(), r => r.Failure(new InvalidOperationException("boom")));
        Assert.NotNull(failed);
        Assert.Equal(ActivityStatusCode.Error, failed.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, failed.GetTagItem(EasyOcrDiagnostics.TagNames.ErrorType));

        var canceled = RecordSpan(UniqueOperation(), r => r.Failure(new OperationCanceledException()));
        Assert.NotNull(canceled);
        Assert.NotEqual(ActivityStatusCode.Error, canceled.Status);

        var succeeded = RecordSpan(UniqueOperation(), r => r.Success());
        Assert.NotNull(succeeded);
        Assert.NotEqual(ActivityStatusCode.Error, succeeded.Status);
    }

    /// <summary>
    /// The span carries the same operation/provider/outcome tags as the metrics, so a dashboard and a
    /// trace view can be filtered identically — the stated reason the recorder owns both.
    /// </summary>
    [Fact]
    public void The_span_carries_the_same_core_tags_as_the_metrics()
    {
        var operation = UniqueOperation();
        var span = RecordSpan(operation, r => r.Success());

        Assert.NotNull(span);
        Assert.Equal(operation, span.GetTagItem(EasyOcrDiagnostics.TagNames.Operation));
        Assert.Equal(Provider, span.GetTagItem(EasyOcrDiagnostics.TagNames.Provider));
        Assert.Equal(EasyOcrDiagnostics.Outcomes.Success, span.GetTagItem(EasyOcrDiagnostics.TagNames.Outcome));
    }

    /// <summary>A recorder with no span attached must still emit its metrics and never dereference null.</summary>
    [Fact]
    public void A_recorder_without_a_span_still_emits_metrics()
    {
        var operation = UniqueOperation();

        var measurements = Capture(operation, () =>
        {
            using var recorder = EasyOcrDiagnostics.Begin(operation, Provider).Annotate(null);
            recorder.Success();
        });

        Assert.Equal(1, Single(measurements, "easyocr.operations").Value);
    }
}
