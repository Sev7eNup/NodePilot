using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Hosting;
using NodePilot.Data.Availability;
using NodePilot.TestCommons;
using Npgsql;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

public sealed class DatabaseAvailabilityProbeTests
{
    [Fact]
    public async Task IdleTick_CancelsAndObservesTheLosingProbeRequestWaiter()
    {
        var availability = new TrackingAvailability(Booted());
        var transport = new FakeTransport();
        const int shortTicks = 5_000;
        var reachedBlockingTick = NewSignal();
        var delayCalls = 0;
        Task Delay(TimeSpan _, CancellationToken ct)
        {
            if (Interlocked.Increment(ref delayCalls) <= shortTicks)
                return Task.CompletedTask;

            reachedBlockingTick.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        using var probe = CreateProbe(
            availability,
            () => transport,
            delay: Delay);

        await probe.StartAsync(TestContext.Current.CancellationToken);
        await reachedBlockingTick.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => availability.ActiveProbeWaiters == 1,
            TestContext.Current.CancellationToken);

        availability.MaximumActiveProbeWaiters.Should().Be(1,
            "each tick must cancel and await the WhenAny loser before starting another waiter");

        await StopAsync(probe);
        availability.ActiveProbeWaiters.Should().Be(0);
    }

    [Fact]
    public async Task SuccessfulProbe_ClearsApplicationPoolOncePerUnavailableEpisode()
    {
        var availability = Booted(successesToRecover: 2);
        var delays = new StepDelay();
        var clears = 0;
        using var probe = CreateProbe(
            availability,
            () => new FakeTransport(),
            clearApplicationPool: _ =>
            {
                Interlocked.Increment(ref clears);
                return Task.CompletedTask;
            },
            delay: delays.WaitAsync);

        await probe.StartAsync(TestContext.Current.CancellationToken);
        await delays.EnteredAsync(1, TestContext.Current.CancellationToken);

        availability.ReportUnreachable(DatabaseOutageReason.Unreachable);
        await delays.EnteredAsync(2, TestContext.Current.CancellationToken);

        availability.State.Should().Be(DatabaseAvailabilityState.Unavailable);
        clears.Should().Be(1);

        delays.Release();
        await delays.EnteredAsync(3, TestContext.Current.CancellationToken);

        availability.State.Should().Be(DatabaseAvailabilityState.Available);
        clears.Should().Be(1, "the second recovery success belongs to the same outage episode");

        availability.ReportUnreachable(DatabaseOutageReason.Unreachable);
        await delays.EnteredAsync(4, TestContext.Current.CancellationToken);

        clears.Should().Be(2, "a later Unavailable transition is a new outage episode");
        await StopAsync(probe);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SuccessfulProbe_WhenUnavailableNotificationIsDelayed_ClearsPoolOncePerEpisode(
        int successesToRecover)
    {
        // The tracker publishes its state before invoking subscribers. A slow subscriber registered
        // ahead of the probe must not leave the probe using the previous episode number while it can
        // already observe Unavailable.
        var availability = Booted(successesToRecover);
        var transitionEntered = NewSignal();
        var releaseTransition = NewSignal();
        availability.StateChanged += state =>
        {
            if (state is not DatabaseAvailabilityState.Unavailable) return;
            transitionEntered.TrySetResult();
            releaseTransition.Task.GetAwaiter().GetResult();
        };

        var delays = new StepDelay();
        var clears = 0;
        using var probe = CreateProbe(
            availability,
            () => new FakeTransport(),
            clearApplicationPool: _ =>
            {
                Interlocked.Increment(ref clears);
                return Task.CompletedTask;
            },
            delay: delays.WaitAsync);

        await probe.StartAsync(TestContext.Current.CancellationToken);
        await delays.EnteredAsync(1, TestContext.Current.CancellationToken);

        var open = Task.Run(
            () => availability.ReportUnreachable(DatabaseOutageReason.Unreachable),
            TestContext.Current.CancellationToken);
        await transitionEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        delays.Release();
        await delays.EnteredAsync(2, TestContext.Current.CancellationToken);
        clears.Should().Be(1);

        releaseTransition.TrySetResult();
        await open.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => availability.State is DatabaseAvailabilityState.Available,
            TestContext.Current.CancellationToken);

        clears.Should().Be(1, "one actual outage episode owns one application-pool clear");

        // A delayed callback from episode 1 must not advance the pool-clear claim past a later real
        // episode. Threshold 1 is the adversarial case: recovery can complete while the original
        // Unavailable callback is still blocked.
        availability.ReportUnreachable(DatabaseOutageReason.Unreachable);
        await WaitUntilAsync(
            () => Volatile.Read(ref clears) == 2,
            TestContext.Current.CancellationToken);

        delays.Release();
        await WaitUntilAsync(
            () => availability.State is DatabaseAvailabilityState.Available,
            TestContext.Current.CancellationToken);
        clears.Should().Be(2, "each actual episode must be claimed exactly once");
        await StopAsync(probe);
    }

    [Fact]
    public async Task SuccessfulProbe_WhileArmed_DoesNotClearApplicationPool()
    {
        var availability = Booted(successesToRecover: 1);
        var delays = new StepDelay();
        var clears = 0;
        using var probe = CreateProbe(
            availability,
            () => new FakeTransport(),
            clearApplicationPool: _ =>
            {
                Interlocked.Increment(ref clears);
                return Task.CompletedTask;
            },
            delay: delays.WaitAsync);

        await probe.StartAsync(TestContext.Current.CancellationToken);
        await delays.EnteredAsync(1, TestContext.Current.CancellationToken);

        availability.Arm();
        await delays.EnteredAsync(2, TestContext.Current.CancellationToken);

        availability.State.Should().Be(DatabaseAvailabilityState.Available);
        clears.Should().Be(0, "Armed is adjudication, not a real outage episode");
        await StopAsync(probe);
    }

    [Fact]
    public async Task OpenThatIgnoresCancellation_IsHardLimitedAndReportedAsUnreachable()
    {
        var availability = Booted(failuresToOpen: 1);
        var never = NewSignal();
        var disposeStarted = NewSignal();
        var unavailable = OnUnavailable(availability);
        using var probe = CreateProbe(
            availability,
            () => new FakeTransport(
                open: _ => never.Task,
                dispose: () =>
                {
                    disposeStarted.TrySetResult();
                    return ValueTask.CompletedTask;
                }),
            openTimeout: TimeSpan.FromMilliseconds(30));

        await probe.StartAsync(TestContext.Current.CancellationToken);
        await unavailable.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await disposeStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        availability.CurrentOutage!.Reason.Should().Be(DatabaseOutageReason.Unreachable);
        never.Task.IsCompleted.Should().BeFalse("the provider deliberately ignored cancellation");
        await StopAsync(probe);
    }

    [Fact]
    public async Task CommandFailure_IsReportedBeforeBoundedDispose_AndCleanupTimeoutIsCounted()
    {
        var availability = Booted(failuresToOpen: 1);
        var commandNever = NewSignal();
        var disposeNever = NewSignal();
        DatabaseAvailabilityState? stateAtDispose = null;
        var transport = new FakeTransport(
            execute: (_, _) => commandNever.Task,
            dispose: () =>
            {
                stateAtDispose = availability.State;
                return new ValueTask(disposeNever.Task);
            });
        using var measurement = ListenForCleanupTimeout("connection_dispose");
        using var probe = CreateProbe(
            availability,
            () => transport,
            commandTimeout: TimeSpan.FromMilliseconds(30),
            cleanupTimeout: TimeSpan.FromMilliseconds(30));

        await probe.StartAsync(TestContext.Current.CancellationToken);
        var tags = await measurement.Result.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        stateAtDispose.Should().Be(DatabaseAvailabilityState.Unavailable,
            "classification and breaker reporting must happen before potentially wedged cleanup");
        availability.CurrentOutage!.Reason.Should().Be(DatabaseOutageReason.Wedged);
        tags.Should().Contain("provider", "postgres");
        tags.Should().Contain("operation", "connection_dispose");
        commandNever.Task.IsCompleted.Should().BeFalse();
        disposeNever.Task.IsCompleted.Should().BeFalse();
        await StopAsync(probe);
    }

    [Fact]
    public async Task PoolCleanupThatIgnoresCancellation_IsHardLimitedAndDoesNotBlockRecovery()
    {
        var availability = Booted(successesToRecover: 2);
        var cleanupNever = NewSignal();
        var delays = new StepDelay();
        var clearAttempts = 0;
        using var measurement = ListenForCleanupTimeout("application_pool_clear");
        using var probe = CreateProbe(
            availability,
            () => new FakeTransport(),
            clearApplicationPool: _ =>
            {
                Interlocked.Increment(ref clearAttempts);
                return cleanupNever.Task;
            },
            delay: delays.WaitAsync,
            cleanupTimeout: TimeSpan.FromMilliseconds(30));

        await probe.StartAsync(TestContext.Current.CancellationToken);
        await delays.EnteredAsync(1, TestContext.Current.CancellationToken);
        availability.ReportUnreachable(DatabaseOutageReason.Unreachable);
        var tags = await measurement.Result.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await delays.EnteredAsync(2, TestContext.Current.CancellationToken);
        availability.State.Should().Be(DatabaseAvailabilityState.Unavailable);

        delays.Release();
        await delays.EnteredAsync(3, TestContext.Current.CancellationToken);

        tags.Should().Contain("operation", "application_pool_clear");
        availability.State.Should().Be(DatabaseAvailabilityState.Available);
        clearAttempts.Should().Be(1,
            "a timed-out clear is still the one attempt for that outage episode");
        cleanupNever.Task.IsCompleted.Should().BeFalse();
        await StopAsync(probe);
    }

    [Fact]
    public async Task RejectedProbe_LogsErrorOnlyWhenTheOutageReasonTransitionsToRejected()
    {
        var availability = Booted();
        var logger = new CapturingLogger<DatabaseAvailabilityProbe>();
        var reachedSecondFailure = NewSignal();
        var delayCalls = 0;
        Task Delay(TimeSpan _, CancellationToken ct)
        {
            if (Interlocked.Increment(ref delayCalls) == 1)
                return Task.CompletedTask;

            reachedSecondFailure.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        using var probe = CreateProbe(
            availability,
            () => new FakeTransport(execute: (_, _) => Task.FromException(
                new PostgresException(
                    messageText: "password authentication failed",
                    severity: "FATAL",
                    invariantSeverity: "FATAL",
                    sqlState: "28P01"))),
            delay: Delay,
            logger: logger);

        await probe.StartAsync(TestContext.Current.CancellationToken);
        await reachedSecondFailure.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        logger.Entries.Count(entry => entry.Level is LogLevel.Error).Should().Be(1);
        availability.CurrentOutage!.Reason.Should().Be(DatabaseOutageReason.RejectedByServer);
        await StopAsync(probe);
    }

    private static DatabaseAvailabilityProbe CreateProbe(
        IDatabaseAvailability availability,
        Func<IDatabaseProbeTransport> createTransport,
        Func<CancellationToken, Task>? clearApplicationPool = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? openTimeout = null,
        TimeSpan? commandTimeout = null,
        TimeSpan? cleanupTimeout = null,
        ILogger<DatabaseAvailabilityProbe>? logger = null)
    {
        var runtime = new DatabaseProbeRuntime(
            Provider: "postgres",
            ConnectionString: "Host=unused;Pooling=false",
            ProviderCommandTimeoutSeconds: 1,
            OpenTimeout: openTimeout ?? TimeSpan.FromMilliseconds(250),
            CommandTimeout: commandTimeout ?? TimeSpan.FromMilliseconds(250),
            CleanupTimeout: cleanupTimeout ?? TimeSpan.FromMilliseconds(250),
            IdleInterval: TimeSpan.FromHours(1),
            OutageInterval: TimeSpan.FromHours(1),
            CreateTransport: createTransport,
            ClearApplicationPool: clearApplicationPool ?? (_ => Task.CompletedTask),
            Delay: delay ?? ((timeout, ct) => Task.Delay(timeout, ct)));

        return new DatabaseAvailabilityProbe(
            availability,
            logger ?? NullLogger<DatabaseAvailabilityProbe>.Instance,
            runtime);
    }

    private static DatabaseAvailabilityTracker Booted(
        int successesToRecover = 2,
        int failuresToOpen = 2)
    {
        var tracker = new DatabaseAvailabilityTracker(
            NullLogger<DatabaseAvailabilityTracker>.Instance,
            successesToRecover,
            failuresToOpen);
        tracker.MarkBootComplete();
        return tracker;
    }

    private static Task OnUnavailable(IDatabaseAvailability availability)
    {
        var signal = NewSignal();
        availability.StateChanged += state =>
        {
            if (state is DatabaseAvailabilityState.Unavailable)
                signal.TrySetResult();
        };
        return signal.Task;
    }

    private static async Task StopAsync(DatabaseAvailabilityProbe probe)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await probe.StopAsync(timeout.Token);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected probe state was not reached.");
            await Task.Delay(10, cancellationToken);
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CleanupMeasurement ListenForCleanupTimeout(string operation)
    {
        var result = new TaskCompletionSource<IReadOnlyDictionary<string, object?>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (instrument.Name is "nodepilot.database.probe_cleanup_timeouts")
                currentListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (value != 1) return;
            var captured = new Dictionary<string, object?>();
            foreach (var tag in tags) captured[tag.Key] = tag.Value;
            if (captured.TryGetValue("operation", out var measuredOperation)
                && Equals(measuredOperation, operation))
                result.TrySetResult(captured);
        });
        listener.Start();
        return new CleanupMeasurement(listener, result.Task);
    }

    private sealed record CleanupMeasurement(
        MeterListener Listener,
        Task<IReadOnlyDictionary<string, object?>> Result) : IDisposable
    {
        public void Dispose() => Listener.Dispose();
    }

    private sealed class StepDelay
    {
        private readonly Channel<byte> _permits = Channel.CreateUnbounded<byte>();
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _entered = new();
        private int _calls;

        public async Task WaitAsync(TimeSpan _, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            _entered.GetOrAdd(call, _ => NewSignal()).TrySetResult();
            await _permits.Reader.ReadAsync(cancellationToken);
        }

        public Task EnteredAsync(int call, CancellationToken cancellationToken) =>
            _entered.GetOrAdd(call, _ => NewSignal()).Task.WaitAsync(
                TimeSpan.FromSeconds(5), cancellationToken);

        public void Release() => _permits.Writer.TryWrite(0);
    }

    private sealed class FakeTransport(
        Func<CancellationToken, Task>? open = null,
        Func<int, CancellationToken, Task>? execute = null,
        Func<ValueTask>? dispose = null) : IDatabaseProbeTransport
    {
        private bool _isOpen;

        public bool IsOpen => _isOpen;

        public async Task OpenAsync(CancellationToken cancellationToken)
        {
            await (open?.Invoke(cancellationToken) ?? Task.CompletedTask);
            _isOpen = true;
        }

        public Task ExecuteProbeAsync(int commandTimeoutSeconds, CancellationToken cancellationToken) =>
            execute?.Invoke(commandTimeoutSeconds, cancellationToken) ?? Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _isOpen = false;
            return dispose?.Invoke() ?? ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingAvailability(DatabaseAvailabilityTracker inner)
        : IDatabaseAvailability
    {
        private int _activeProbeWaiters;
        private int _maximumActiveProbeWaiters;

        public int ActiveProbeWaiters => Volatile.Read(ref _activeProbeWaiters);
        public int MaximumActiveProbeWaiters => Volatile.Read(ref _maximumActiveProbeWaiters);
        public DatabaseAvailabilityState State => inner.State;
        public bool IsServable => inner.IsServable;
        public DatabaseOutage? CurrentOutage => inner.CurrentOutage;
        public DatabaseAvailabilitySnapshot Snapshot => inner.Snapshot;
        public event Action<DatabaseAvailabilityState>? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }
        public event Action<long>? OutageRecovered
        {
            add => inner.OutageRecovered += value;
            remove => inner.OutageRecovered -= value;
        }

        public void MarkBootComplete() => inner.MarkBootComplete();
        public void ReportUnreachable(DatabaseOutageReason reason) => inner.ReportUnreachable(reason);
        public void Arm() => inner.Arm();
        public void ReportProbeSucceeded(long observedOutageEpisodeId = -1) =>
            inner.ReportProbeSucceeded(observedOutageEpisodeId);
        public void ReportProbeFailed(DatabaseOutageReason reason) => inner.ReportProbeFailed(reason);
        public Task<bool> WaitUntilServableAsync(CancellationToken cancellationToken) =>
            inner.WaitUntilServableAsync(cancellationToken);

        public async Task WaitForProbeRequestAsync(CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activeProbeWaiters);
            UpdateMaximum(active);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The production tracker has the same no-throw cancellation contract.
            }
            finally
            {
                Interlocked.Decrement(ref _activeProbeWaiters);
            }
        }

        private void UpdateMaximum(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumActiveProbeWaiters);
                if (candidate <= current) return;
                if (Interlocked.CompareExchange(
                        ref _maximumActiveProbeWaiters, candidate, current) == current)
                    return;
            }
        }
    }
}
