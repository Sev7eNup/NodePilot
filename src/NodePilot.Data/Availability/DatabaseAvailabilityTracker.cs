using Microsoft.Extensions.Logging;

namespace NodePilot.Data.Availability;

/// <summary>
/// The single shared instance of <see cref="IDatabaseAvailability"/>. Registered as a singleton and
/// written from several threads at once (pooled DbContext interceptors plus the probe), so every
/// transition happens under one lock and every published field is read from a snapshot taken
/// inside it.
///
/// <para>Thresholds are resolved once in the constructor because they are boot config
/// (<c>Database:Probe:*</c>); reading configuration inside the lock would put I/O on the path that
/// every failing query takes.</para>
/// </summary>
public sealed class DatabaseAvailabilityTracker : IDatabaseAvailability
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly ILogger<DatabaseAvailabilityTracker> _logger;
    private readonly int _probeSuccessesToRecover;
    private readonly int _probeFailuresToOpen;

    private DatabaseAvailabilityState _state = DatabaseAvailabilityState.Booting;
    private DateTime _outageSinceUtc;
    private DatabaseOutageReason _reason;
    private int _consecutiveProbeFailures;
    private int _consecutiveProbeSuccesses;
    private long _outageEpisodeId;

    // Completed while servable; replaced with a fresh incomplete source the moment the breaker
    // opens.
    private TaskCompletionSource _servable =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Bounded level-triggered signal. A TaskCompletionSource needs a reset after each await; a wake
    // racing between completion and that reset is then overwritten and lost. SemaphoreSlim(0, 1)
    // keeps one pending request until the probe consumes it and coalesces duplicate reports safely.
    private readonly SemaphoreSlim _probeRequested = new(0, 1);

    public DatabaseAvailabilityTracker(
        ILogger<DatabaseAvailabilityTracker> logger,
        int probeSuccessesToRecover = 2,
        int probeFailuresToOpen = 2,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _probeSuccessesToRecover = Math.Max(1, probeSuccessesToRecover);
        _probeFailuresToOpen = Math.Max(1, probeFailuresToOpen);
        _time = timeProvider ?? TimeProvider.System;

        // Booting is servable: the boot block itself needs the database, and the HTTP pipeline does
        // not exist yet, so there is nothing to seal off.
        _servable.TrySetResult();
    }

    public DatabaseAvailabilityState State
    {
        get { lock (_gate) return _state; }
    }

    public bool IsServable
    {
        get { lock (_gate) return _state is not DatabaseAvailabilityState.Unavailable; }
    }

    public DatabaseOutage? CurrentOutage
    {
        get
        {
            lock (_gate)
            {
                return BuildCurrentOutage();
            }
        }
    }

    public DatabaseAvailabilitySnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new DatabaseAvailabilitySnapshot(
                    _state,
                    BuildCurrentOutage(),
                    _outageEpisodeId);
            }
        }
    }

    public event Action<DatabaseAvailabilityState>? StateChanged;
    public event Action<long>? OutageRecovered;

    public void MarkBootComplete()
    {
        DatabaseAvailabilityState? transitioned = null;
        lock (_gate)
        {
            if (_state is DatabaseAvailabilityState.Booting)
            {
                _state = DatabaseAvailabilityState.Available;
                transitioned = _state;
            }
        }
        RaiseIfChanged(transitioned);
    }

    public void ReportUnreachable(DatabaseOutageReason reason)
    {
        DatabaseAvailabilityState? transitioned = null;
        var wasAlreadyOpen = false;
        var reasonChanged = false;

        lock (_gate)
        {
            // Inert while booting. DatabaseReadinessGate exists precisely because the database is
            // often
            // late at boot; letting its failed probes open the breaker would switch off retries for
            // the
            // migration that follows.
            if (_state is DatabaseAvailabilityState.Booting) return;

            if (_state is DatabaseAvailabilityState.Unavailable)
            {
                wasAlreadyOpen = true;
                // A changed reason is worth surfacing even mid-outage: "unreachable" turning into
                // "rejected by server" is the difference between waiting and fixing a password.
                reasonChanged = ShouldReplaceOutageReason(_reason, reason);
                if (reasonChanged) _reason = reason;
            }
            else
            {
                _state = DatabaseAvailabilityState.Unavailable;
                _outageEpisodeId++;
                _outageSinceUtc = _time.GetUtcNow().UtcDateTime;
                _reason = reason;
                _consecutiveProbeFailures = 0;
                _consecutiveProbeSuccesses = 0;
                _servable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                transitioned = _state;
            }
        }

        if (transitioned is not null)
        {
            _logger.LogWarning(
                "Database unavailable ({Reason}). Requests are answered with 503 until a probe succeeds.",
                reason);
        }
        else if (wasAlreadyOpen && reasonChanged)
        {
            _logger.LogWarning("Database still unavailable; cause changed to {Reason}.", reason);
        }

        RaiseIfChanged(transitioned);
        WakeProbe();
    }

    public void Arm()
    {
        DatabaseAvailabilityState? transitioned = null;
        lock (_gate)
        {
            if (_state is DatabaseAvailabilityState.Available)
            {
                _state = DatabaseAvailabilityState.Armed;
                _consecutiveProbeFailures = 0;
                _consecutiveProbeSuccesses = 0;
                transitioned = _state;
            }
        }
        RaiseIfChanged(transitioned);
        WakeProbe();
    }

    public void ReportProbeSucceeded(long observedOutageEpisodeId = -1)
    {
        DatabaseAvailabilityState? transitioned = null;
        TimeSpan outageDuration = default;
        var wasOutage = false;
        var recoveredEpisodeId = 0L;

        lock (_gate)
        {
            // The probe captured its snapshot after SELECT 1. If an interceptor opened a newer
            // outage
            // between that observation and this publication, the success says nothing about the new
            // episode and must not consume its recovery threshold (especially when the threshold is
            // 1).
            if (_state is DatabaseAvailabilityState.Unavailable
                && observedOutageEpisodeId >= 0
                && observedOutageEpisodeId != _outageEpisodeId)
                return;

            _consecutiveProbeFailures = 0;

            if (_state is DatabaseAvailabilityState.Available or DatabaseAvailabilityState.Booting)
            {
                _consecutiveProbeSuccesses = _probeSuccessesToRecover;
                return;
            }

            _consecutiveProbeSuccesses++;
            if (_consecutiveProbeSuccesses < _probeSuccessesToRecover) return;

            wasOutage = _state is DatabaseAvailabilityState.Unavailable;
            if (wasOutage)
            {
                outageDuration = _time.GetUtcNow().UtcDateTime - _outageSinceUtc;
                recoveredEpisodeId = _outageEpisodeId;
            }

            _state = DatabaseAvailabilityState.Available;
            _servable.TrySetResult();
            transitioned = _state;
        }

        if (wasOutage)
        {
            _logger.LogInformation(
                "Database available again after {OutageSeconds:n0}s. Normal operation resumes.",
                outageDuration.TotalSeconds);
        }

        RaiseIfChanged(transitioned);
        if (wasOutage) RaiseOutageRecovered(recoveredEpisodeId);
    }

    public void ReportProbeFailed(DatabaseOutageReason reason)
    {
        var shouldOpen = false;
        var reasonChanged = false;

        lock (_gate)
        {
            _consecutiveProbeSuccesses = 0;
            _consecutiveProbeFailures++;

            if (_state is DatabaseAvailabilityState.Unavailable)
            {
                reasonChanged = ShouldReplaceOutageReason(_reason, reason);
                if (reasonChanged) _reason = reason;
            }
            else
            {
                // A connection-class or rejection answer is unambiguous: the probe holds its own
                // dedicated
                // connection, so there is no pool handout to misread and no second opinion to wait
                // for.
                // Open on the first observation. Only the ambiguous answer — the server accepted a
                // connection but did not finish the statement — waits for the threshold.
                shouldOpen = reason is not DatabaseOutageReason.Wedged
                          || _consecutiveProbeFailures >= _probeFailuresToOpen;
            }
        }

        if (reasonChanged)
            _logger.LogWarning("Database still unavailable; probe changed the cause to {Reason}.", reason);

        // Outside the lock on purpose: ReportUnreachable takes it again, and it logs while holding
        // it
        // is the one thing this class must never do on the path of a failing query.
        if (shouldOpen) ReportUnreachable(reason);
    }

    private static bool ShouldReplaceOutageReason(
        DatabaseOutageReason current,
        DatabaseOutageReason reported)
        => reported is not DatabaseOutageReason.Unknown
           && current is not DatabaseOutageReason.RejectedByServer
           && current != reported;

    private DatabaseOutage? BuildCurrentOutage() =>
        _state is DatabaseAvailabilityState.Unavailable
            ? new DatabaseOutage(
                _outageSinceUtc,
                _reason,
                _consecutiveProbeFailures,
                _outageEpisodeId)
            : null;

    public async Task<bool> WaitUntilServableAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task servable;
            lock (_gate)
            {
                if (_state is not DatabaseAvailabilityState.Unavailable) return true;
                servable = _servable.Task;
            }

            if (cancellationToken.IsCancellationRequested) return false;

            // WaitAsync rather than WhenAny + an infinite Task.Delay: the WhenAny loser kept a
            // timer
            // and a token registration alive until the token fired — with process-lifetime stopping
            // tokens, that is one leaked pair per waiter per outage. WaitAsync tears its
            // registration
            // down on either outcome. The catch keeps the interface promise: this method never
            // throws,
            // because every hosted-service gate is written as `if (!await …) break;` and an
            // escaping
            // OperationCanceledException would stop the host.
            try
            {
                await servable.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    public async Task WaitForProbeRequestAsync(CancellationToken cancellationToken)
    {
        // Completing normally on cancellation is intentional: the probe loop re-checks its stopping
        // token immediately. WaitAsync removes its token registration on either outcome.
        try
        {
            await _probeRequested.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }

    private void WakeProbe()
    {
        if (_probeRequested.CurrentCount != 0) return;
        try
        {
            _probeRequested.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another reporting thread published the coalesced request after CurrentCount was read.
        }
    }

    private void RaiseIfChanged(DatabaseAvailabilityState? transitioned)
    {
        if (transitioned is null) return;
        try
        {
            StateChanged?.Invoke(transitioned.Value);
        }
        catch (Exception ex)
        {
            // A subscriber must never be able to break the breaker: this runs on the thread of
            // whatever
            // query just failed, which may be a hosted-service loop with StopHost semantics.
            _logger.LogWarning(ex, "A database-availability subscriber threw; ignoring.");
        }
    }

    private void RaiseOutageRecovered(long episodeId)
    {
        try
        {
            OutageRecovered?.Invoke(episodeId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A database-recovery subscriber threw; ignoring.");
        }
    }
}
