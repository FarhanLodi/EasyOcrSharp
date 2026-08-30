using System.Diagnostics;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Services;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Unit tests for the concurrency gate / queue-timeout / operation-timeout primitive behind
/// <see cref="EasyOcrServiceOptions.MaxConcurrentOperations"/>, <see cref="EasyOcrServiceOptions.QueueTimeout"/>
/// and <see cref="EasyOcrServiceOptions.OperationTimeout"/>.
/// <para>
/// Everything here is in-memory: no model is downloaded, no ONNX session is created, and no test sleeps
/// for a fixed duration where a handshake will do — the whole class runs in well under a second. Timing
/// assertions use generous upper bounds so a loaded CI box cannot fail them, while still proving the
/// fast-path property they exist for.
/// </para>
/// </summary>
public class OperationGovernorTests
{
    private const string Op = "extract";

    /// <summary>
    /// The longest wait .NET's timer infrastructure accepts, <c>uint.MaxValue - 1</c> milliseconds
    /// (~49.7 days), measured rather than assumed: both <see cref="SemaphoreSlim.WaitAsync(TimeSpan, CancellationToken)"/>
    /// on its contended path and <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> accept exactly
    /// this value and throw <see cref="ArgumentOutOfRangeException"/> one millisecond above it.
    /// </summary>
    private static readonly TimeSpan MaxSupportedWait = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Builds the governor through <see cref="EasyOcrServiceOptions.CreateGovernor"/> rather than its own
    /// constructor, so these tests exercise the same path production does — including the option defaults.
    /// </summary>
    private static OperationGovernor Governor(
        int capacity = 0,
        TimeSpan? queueTimeout = null,
        TimeSpan? operationTimeout = null) =>
        new EasyOcrServiceOptions
        {
            MaxConcurrentOperations = capacity,
            QueueTimeout = queueTimeout ?? Timeout.InfiniteTimeSpan,
            OperationTimeout = operationTimeout ?? TimeSpan.Zero,
        }.CreateGovernor();

    /// <summary>Completes when <paramref name="token"/> is cancelled — no polling, no fixed sleep.</summary>
    private static Task WhenCancelled(CancellationToken token)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => tcs.TrySetResult());
        return tcs.Task;
    }

    /// <summary>Asserts a task is still pending after a short grace period (it is blocked, not merely slow).</summary>
    private static async Task AssertStillPending(Task task, string because)
    {
        var winner = await Task.WhenAny(task, Task.Delay(150));
        Assert.False(ReferenceEquals(winner, task), because);
    }

    // ------------------------------------------------------------------ pass-through (the default)

    /// <summary>
    /// The default configuration must be a true no-op: no semaphore, no linked token source, no timer.
    /// Handing back the caller's own token (rather than a linked clone) is the observable proof that
    /// nothing was allocated — and it is what keeps the ungoverned pipeline exactly as it was.
    /// </summary>
    [Fact]
    public async Task Default_options_enforce_nothing_and_hand_back_the_callers_own_token()
    {
        using var governor = Governor();
        Assert.False(governor.IsEnabled);

        using var cts = new CancellationTokenSource();
        using var lease = await governor.AcquireAsync(Op, cts.Token);

        // CancellationToken equality compares the underlying source, so this fails for a linked token.
        Assert.Equal(cts.Token, lease.Token);

        // And with no caller token at all, none is manufactured.
        using var none = await governor.AcquireAsync(Op, CancellationToken.None);
        Assert.False(none.Token.CanBeCanceled);
    }

    /// <summary>
    /// Pass-through means genuinely unbounded: sixty-four simultaneous leases with nothing released must
    /// all be admitted. A gate that accidentally defaulted to some finite capacity would deadlock here.
    /// </summary>
    [Fact]
    public async Task Pass_through_acquire_never_blocks_however_many_leases_are_open()
    {
        using var governor = Governor();
        var leases = new List<OperationLease>();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 64; i++) leases.Add(await governor.AcquireAsync(Op, CancellationToken.None));
        sw.Stop();

        Assert.Equal(64, leases.Count);
        Assert.True(sw.ElapsedMilliseconds < 2_000, $"Pass-through acquire should not block; took {sw.ElapsedMilliseconds} ms.");
        foreach (var lease in leases) lease.Dispose();
    }

    // ------------------------------------------------------------------ capacity

    /// <summary>
    /// The gate must hold the line at exactly N. This drives it with far more callers than slots and
    /// records the high-water mark of simultaneously held leases — the invariant that actually bounds
    /// peak memory, since every concurrent OCR run allocates its own tensors.
    /// </summary>
    [Fact]
    public async Task Concurrent_callers_never_exceed_the_configured_capacity()
    {
        const int capacity = 3;
        using var governor = Governor(capacity);

        int current = 0, highWater = 0;

        var workers = Enumerable.Range(0, 40).Select(async _ =>
        {
            var lease = await governor.AcquireAsync(Op, CancellationToken.None);
            var now = Interlocked.Increment(ref current);
            int seen;
            while (now > (seen = Volatile.Read(ref highWater)))
            {
                if (Interlocked.CompareExchange(ref highWater, now, seen) == seen) break;
            }

            await Task.Yield();
            await Task.Yield();

            // Decrement *before* releasing the slot: releasing first would let the next caller in and
            // count itself while this one is still tallied, inventing an over-count that never happened.
            Interlocked.Decrement(ref current);
            lease.Dispose();
        }).ToArray();

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, Volatile.Read(ref current));
        Assert.InRange(Volatile.Read(ref highWater), 1, capacity);
    }

    /// <summary>A caller beyond capacity waits, and is admitted the moment a holder releases.</summary>
    [Fact]
    public async Task A_queued_caller_is_admitted_as_soon_as_the_holder_releases()
    {
        using var governor = Governor(capacity: 1, queueTimeout: TimeSpan.FromSeconds(30));

        var holder = await governor.AcquireAsync(Op, CancellationToken.None);
        var waiter = governor.AcquireAsync(Op, CancellationToken.None).AsTask();

        await AssertStillPending(waiter, "The only slot is held, so the second caller must still be queued.");

        holder.Dispose();

        using var admitted = await waiter.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(admitted);
    }

    // ------------------------------------------------------------------ load shedding

    /// <summary>
    /// The whole point of <c>QueueTimeout = Zero</c> is a <b>fail-fast</b> refusal, not an eventual one:
    /// a front end can only answer 503 while the queue is still short. So this asserts both the exception
    /// and that it arrives essentially immediately.
    /// </summary>
    [Fact]
    public async Task A_full_gate_with_a_zero_queue_timeout_sheds_immediately()
    {
        using var governor = Governor(capacity: 1, queueTimeout: TimeSpan.Zero);
        using var holder = await governor.AcquireAsync(Op, CancellationToken.None);

        var sw = Stopwatch.StartNew();
        var busy = await Assert.ThrowsAsync<OcrBusyException>(
            async () => await governor.AcquireAsync(Op, CancellationToken.None));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1_000,
            $"Shedding must be immediate, not merely eventual; took {sw.ElapsedMilliseconds} ms.");

        // The message is the only channel this exception has — there are no Capacity/QueueTimeout
        // properties — so it must name both knobs an operator would reach for, and the operation.
        Assert.Contains("MaxConcurrentOperations", busy.Message, StringComparison.Ordinal);
        Assert.Contains("QueueTimeout", busy.Message, StringComparison.Ordinal);
        Assert.Contains(Op, busy.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ slot accounting

    /// <summary>
    /// A slot leaked once is a slot lost forever: capacity silently shrinks until the service wedges. Five
    /// hundred cycles then prove the count is <i>exactly</i> one — one acquire succeeds, the next is shed.
    /// </summary>
    [Fact]
    public async Task Slots_are_not_leaked_across_many_acquire_release_cycles()
    {
        using var governor = Governor(capacity: 1, queueTimeout: TimeSpan.Zero);

        for (int i = 0; i < 500; i++)
        {
            using var lease = await governor.AcquireAsync(Op, CancellationToken.None);
        }

        using var final = await governor.AcquireAsync(Op, CancellationToken.None);
        await Assert.ThrowsAsync<OcrBusyException>(
            async () => await governor.AcquireAsync(Op, CancellationToken.None));
    }

    /// <summary>
    /// The mirror-image bug, and the nastier one: a double release <i>inflates</i> capacity permanently,
    /// so a service configured for four concurrent runs quietly starts allowing five, six, seven — the
    /// limit erodes under exactly the load it was added to survive. Disposing twice must release once.
    /// </summary>
    [Fact]
    public async Task Disposing_a_lease_twice_releases_only_one_slot()
    {
        using var governor = Governor(capacity: 1, queueTimeout: TimeSpan.Zero);

        var lease = await governor.AcquireAsync(Op, CancellationToken.None);
        lease.Dispose();
        lease.Dispose();   // must be a no-op

        using var reacquired = await governor.AcquireAsync(Op, CancellationToken.None);
        await Assert.ThrowsAsync<OcrBusyException>(
            async () => await governor.AcquireAsync(Op, CancellationToken.None));
    }

    // ------------------------------------------------------------------ cancellation

    /// <summary>
    /// A caller who hangs up while queued must surface as cancellation, never as <see cref="OcrBusyException"/>:
    /// the two mean opposite things to a request handler (499 vs 503) and to an SLO dashboard. The slot
    /// must also survive — a cancelled waiter never held one.
    /// </summary>
    [Fact]
    public async Task Cancelling_while_queued_surfaces_cancellation_not_busy()
    {
        using var governor = Governor(capacity: 1, queueTimeout: Timeout.InfiniteTimeSpan);
        var holder = await governor.AcquireAsync(Op, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var waiter = governor.AcquireAsync(Op, cts.Token).AsTask();
        await AssertStillPending(waiter, "The waiter should be queued behind the only slot.");

        cts.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.IsNotType<OcrBusyException>(error);

        // The abandoned wait must not have consumed the slot.
        holder.Dispose();
        using var next = await governor.AcquireAsync(Op, CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(next);
    }

    /// <summary>
    /// An already-cancelled token is rejected before any slot is taken — otherwise a burst of abandoned
    /// requests would each occupy a slot just long enough to starve the live ones. Checked ahead of the
    /// "is anything enabled?" short-circuit, so it holds for the ungoverned default too.
    /// </summary>
    [Fact]
    public async Task An_already_cancelled_token_is_rejected_without_consuming_a_slot()
    {
        using var governor = Governor(capacity: 1, queueTimeout: TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await governor.AcquireAsync(Op, cts.Token));

        // Both halves of the proof: one acquire succeeds (nothing was consumed) and the next is shed
        // (nothing was released either).
        using var lease = await governor.AcquireAsync(Op, CancellationToken.None);
        await Assert.ThrowsAsync<OcrBusyException>(
            async () => await governor.AcquireAsync(Op, CancellationToken.None));
    }

    /// <summary>The same fast rejection applies with no gate configured at all.</summary>
    [Fact]
    public async Task An_already_cancelled_token_is_rejected_even_by_a_pass_through_governor()
    {
        using var governor = Governor();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await governor.AcquireAsync(Op, cts.Token));
    }

    // ------------------------------------------------------------------ operation timeout

    /// <summary>
    /// An operation budget cancels the lease token on expiry, and <c>Translate</c> turns the resulting
    /// cancellation into <see cref="OcrTimeoutException"/> — the distinction a handler needs to answer 504
    /// (this input was too slow, quarantine it) rather than 499 (the caller left).
    /// </summary>
    [Fact]
    public async Task An_expired_operation_budget_cancels_the_lease_and_translates_to_OcrTimeout()
    {
        using var governor = Governor(operationTimeout: TimeSpan.FromMilliseconds(40));

        // A timeout alone is enough to make the governor active, with no concurrency limit set.
        Assert.True(governor.IsEnabled);

        using var lease = await governor.AcquireAsync(Op, CancellationToken.None);
        Assert.True(lease.Token.CanBeCanceled);

        await WhenCancelled(lease.Token).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(lease.Token.IsCancellationRequested);

        var cancellation = new OperationCanceledException(lease.Token);
        var translated = lease.Translate(cancellation);

        var timeout = Assert.IsType<OcrTimeoutException>(translated);
        Assert.Same(cancellation, timeout.InnerException);
        Assert.Contains("OperationTimeout", timeout.Message, StringComparison.Ordinal);
        Assert.Contains(Op, timeout.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason this type exists. When the <i>caller's</i> token is the one that fired, the exception
    /// must come back untouched — same instance, same type. Relabelling a client disconnect as a service
    /// timeout inflates the error budget and sends someone hunting a slow page that was never slow.
    /// </summary>
    [Fact]
    public async Task Caller_cancellation_is_returned_unchanged_and_never_becomes_a_timeout()
    {
        using var governor = Governor(operationTimeout: TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource();

        using var lease = await governor.AcquireAsync(Op, cts.Token);
        Assert.NotEqual(cts.Token, lease.Token);   // a linked token, because a budget is configured

        cts.Cancel();
        Assert.True(lease.Token.IsCancellationRequested);   // cancellation propagates through the link

        var cancellation = new OperationCanceledException(cts.Token);
        Assert.Same(cancellation, lease.Translate(cancellation));
    }

    /// <summary>
    /// When both fired, the caller wins. A client that hung up gets the answer it asked for even if the
    /// budget also lapsed on the way out — otherwise every shutdown would manufacture phantom timeouts.
    /// </summary>
    [Fact]
    public async Task When_both_the_caller_and_the_budget_fire_the_caller_takes_precedence()
    {
        using var governor = Governor(operationTimeout: TimeSpan.FromMilliseconds(30));
        using var cts = new CancellationTokenSource();

        using var lease = await governor.AcquireAsync(Op, cts.Token);
        await WhenCancelled(lease.Token).WaitAsync(TimeSpan.FromSeconds(30));   // the budget expired
        cts.Cancel();                                                          // and then the caller left

        var cancellation = new OperationCanceledException(lease.Token);
        Assert.Same(cancellation, lease.Translate(cancellation));
    }

    /// <summary>
    /// With a gate but no budget, the caller's token is still handed straight back — no linked source is
    /// allocated per operation — and a cancellation is never reinterpreted.
    /// </summary>
    [Fact]
    public async Task A_gate_without_a_budget_still_hands_back_the_callers_own_token()
    {
        using var governor = Governor(capacity: 2);
        Assert.True(governor.IsEnabled);

        using var cts = new CancellationTokenSource();
        using var lease = await governor.AcquireAsync(Op, cts.Token);

        Assert.Equal(cts.Token, lease.Token);

        var cancellation = new OperationCanceledException();
        Assert.Same(cancellation, lease.Translate(cancellation));
    }

    // ------------------------------------------------------------------ option coercion

    /// <summary>
    /// A misconfigured budget must not fault every OCR call. A non-positive
    /// <see cref="EasyOcrServiceOptions.OperationTimeout"/> means "no timeout", leaving the governor inert.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3600)]
    public async Task A_non_positive_operation_timeout_means_no_timeout(int seconds)
    {
        using var governor = Governor(operationTimeout: TimeSpan.FromSeconds(seconds));
        Assert.False(governor.IsEnabled);

        using var cts = new CancellationTokenSource();
        using var lease = await governor.AcquireAsync(Op, cts.Token);
        Assert.Equal(cts.Token, lease.Token);
    }

    /// <summary>
    /// Likewise a negative <see cref="EasyOcrServiceOptions.QueueTimeout"/>: it is coerced to "wait
    /// forever", not handed to <see cref="SemaphoreSlim"/> (which would throw on it) and not mistaken for
    /// <see cref="TimeSpan.Zero"/>'s "reject immediately". Construction never throws for any of these.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(-5)]
    public async Task A_negative_queue_timeout_is_coerced_to_infinite_rather_than_thrown(int seconds)
    {
        using var governor = Governor(capacity: 1, queueTimeout: TimeSpan.FromSeconds(seconds));
        using var lease = await governor.AcquireAsync(Op, CancellationToken.None);
        Assert.NotNull(lease);
    }

    /// <summary><see cref="Timeout.InfiniteTimeSpan"/> — the documented default — is accepted as-is.</summary>
    [Fact]
    public async Task An_infinite_queue_timeout_is_accepted()
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, new EasyOcrServiceOptions().QueueTimeout);

        using var governor = Governor(capacity: 1, queueTimeout: Timeout.InfiniteTimeSpan);
        using var lease = await governor.AcquireAsync(Op, CancellationToken.None);
        Assert.NotNull(lease);
    }

    /// <summary>A negative concurrency limit is clamped to "unlimited" rather than throwing.</summary>
    [Fact]
    public async Task A_negative_concurrency_limit_is_clamped_to_unlimited()
    {
        using var governor = Governor(capacity: -4);
        Assert.False(governor.IsEnabled);

        using var a = await governor.AcquireAsync(Op, CancellationToken.None);
        using var b = await governor.AcquireAsync(Op, CancellationToken.None);
        Assert.NotNull(b);
    }

    /// <summary>
    /// The other end of the clamp, and the subtler half. <see cref="SemaphoreSlim.WaitAsync(TimeSpan, CancellationToken)"/>
    /// rejects an over-long timeout, and it only validates that when it actually has to queue — the
    /// uncontended fast path never arms a timer. So an unclamped <c>QueueTimeout = TimeSpan.MaxValue</c>
    /// (an easy way to write "wait forever" if you do not know <see cref="Timeout.InfiniteTimeSpan"/> is the
    /// sanctioned spelling) would sail through dev and every uncontended test, then throw
    /// <see cref="ArgumentOutOfRangeException"/> out of the OCR call the first time the gate was genuinely
    /// full — the back-pressure path failing at exactly the moment it exists for, and the caller getting an
    /// argument exception instead of <see cref="OcrBusyException"/>.
    /// <para>
    /// Coercing it to an infinite wait is what makes that impossible, so this drives the contended path
    /// specifically: the uncontended acquire proves nothing.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0)]      // TimeSpan.MaxValue
    [InlineData(1)]      // exactly one millisecond past the ceiling — the boundary itself
    [InlineData(2)]      // comfortably past it
    public async Task A_queue_timeout_beyond_the_supported_range_is_coerced_to_an_infinite_wait(int which)
    {
        var queueTimeout = which switch
        {
            0 => TimeSpan.MaxValue,
            1 => MaxSupportedWait + TimeSpan.FromMilliseconds(1),
            _ => TimeSpan.FromDays(60),
        };
        Assert.True(queueTimeout > MaxSupportedWait);

        using var governor = Governor(capacity: 1, queueTimeout: queueTimeout);

        Assert.Equal(Timeout.InfiniteTimeSpan, governor.EffectiveQueueTimeout);

        var holder = await governor.AcquireAsync(Op, CancellationToken.None);
        var waiter = governor.AcquireAsync(Op, CancellationToken.None).AsTask();

        // Contended: it must queue rather than fault, and rather than shed.
        await AssertStillPending(waiter, "An over-long queue timeout must become an infinite wait, not a fault.");

        holder.Dispose();
        using var admitted = await waiter.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(admitted);
    }

    /// <summary>
    /// A long-but-supported queue timeout is left alone and still queues normally, so the clamp above is
    /// genuinely about the unsupported extreme and does not quietly swallow ordinary large waits.
    /// </summary>
    /// <remarks>
    /// The 40-day and boundary cases are the regression guard for a clamp set to the wrong constant. The
    /// ceiling here is <c>uint.MaxValue - 1</c> milliseconds (~49.7 days) — the timer limit behind both
    /// <see cref="SemaphoreSlim.WaitAsync(TimeSpan, CancellationToken)"/> and
    /// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> — and NOT <c>int.MaxValue</c>
    /// milliseconds (~24.9 days). A clamp placed at the smaller value looks harmless because coercion to
    /// an infinite wait behaves identically in most tests, but it silently converts every configured wait
    /// between 24.9 and 49.7 days into "queue forever", turning a bounded shed into an unbounded queue —
    /// the exact failure the timeout exists to prevent.
    /// </remarks>
    [Theory]
    [InlineData(20)]     // comfortably inside every candidate ceiling
    [InlineData(40)]     // past int.MaxValue ms, inside the real ceiling — must NOT be coerced
    [InlineData(-1)]     // the boundary value itself: exactly uint.MaxValue-1 ms
    public async Task A_long_but_supported_queue_timeout_queues_normally_when_the_gate_is_full(int days)
    {
        var queueTimeout = days < 0 ? MaxSupportedWait : TimeSpan.FromDays(days);
        Assert.True(queueTimeout <= MaxSupportedWait);

        using var governor = Governor(capacity: 1, queueTimeout: queueTimeout);

        // The discriminating assertion. Queue-and-admit below passes either way — a coerced infinite wait
        // queues just as happily — so only the effective value can tell an honoured 40-day wait from one
        // the clamp swallowed. This is what fails if the ceiling is ever set back to int.MaxValue ms.
        Assert.Equal(queueTimeout, governor.EffectiveQueueTimeout);

        var holder = await governor.AcquireAsync(Op, CancellationToken.None);
        var waiter = governor.AcquireAsync(Op, CancellationToken.None).AsTask();

        await AssertStillPending(waiter, "A supported queue timeout should queue, not fault and not shed.");

        holder.Dispose();
        using var admitted = await waiter.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(admitted);
    }

    /// <summary>
    /// The same ceiling applies to <c>OperationTimeout</c>, which reaches
    /// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>. That sink is stricter than the queue
    /// wait in one important way: it validates eagerly, so an over-long budget threw
    /// <see cref="ArgumentOutOfRangeException"/> out of <em>every</em> acquire — contended or not, and even
    /// with no concurrency limit configured at all, because building the lease is what arms the timer.
    /// The clamp has to cover both knobs, not just the queue wait.
    /// </summary>
    [Theory]
    [InlineData(0)]      // TimeSpan.MaxValue
    [InlineData(1)]      // one millisecond past the ceiling
    public async Task An_operation_timeout_beyond_the_supported_range_does_not_fault_the_acquire(int which)
    {
        var operationTimeout = which == 0 ? TimeSpan.MaxValue : MaxSupportedWait + TimeSpan.FromMilliseconds(1);

        // No concurrency limit: this is purely about the timeout knob, and proves the fault was never
        // confined to the gated path.
        using var governor = Governor(operationTimeout: operationTimeout);

        using var lease = await governor.AcquireAsync(Op, CancellationToken.None);

        // Coerced to "no budget", so the caller's own token is handed back untouched rather than a linked
        // source. Asserting CanBeCanceled (not merely IsCancellationRequested) is what pins the coercion:
        // a linked source would also report "not cancelled yet" and let the bug through.
        Assert.False(lease.Token.CanBeCanceled);
    }

    /// <summary>
    /// A budget just inside the ceiling is honoured rather than swallowed, so the clamp cannot be
    /// implemented by simply discarding every large value.
    /// </summary>
    [Fact]
    public async Task An_operation_timeout_at_the_supported_ceiling_is_honoured()
    {
        using var governor = Governor(operationTimeout: MaxSupportedWait);

        using var lease = await governor.AcquireAsync(Op, CancellationToken.None);

        // A real budget was armed, so the lease must be running on its own linked token rather than
        // handing back the caller's (which is what "no timeout configured" does).
        Assert.True(lease.Token.CanBeCanceled);
        Assert.False(lease.Token.IsCancellationRequested);
    }

    // ------------------------------------------------------------------ disposal

    /// <summary>
    /// A disposed governor refuses new work, and the error names the type the caller actually holds —
    /// <see cref="EasyOcrService"/> — rather than leaking an internal semaphore's name into their logs.
    /// </summary>
    [Fact]
    public async Task A_disposed_governor_refuses_new_operations()
    {
        var governor = Governor(capacity: 1);
        governor.Dispose();

        var error = await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await governor.AcquireAsync(Op, CancellationToken.None));
        Assert.Equal(nameof(EasyOcrService), error.ObjectName);
    }

    /// <summary>
    /// Disposing the governor while an operation is still in flight must not break that operation's
    /// release path — the semaphore is deliberately left undisposed so an in-flight lease can still return
    /// its slot without an ObjectDisposedException surfacing from deep inside the pipeline.
    /// </summary>
    [Fact]
    public async Task A_lease_taken_before_disposal_still_releases_cleanly()
    {
        var governor = Governor(capacity: 1);
        var lease = await governor.AcquireAsync(Op, CancellationToken.None);

        governor.Dispose();
        lease.Dispose();     // must not throw
        lease.Dispose();     // still idempotent after the governor is gone
    }

    /// <summary>Disposing the governor twice is safe.</summary>
    [Fact]
    public void Disposing_the_governor_twice_is_safe()
    {
        var governor = Governor(capacity: 1);
        governor.Dispose();
        governor.Dispose();
    }
}
