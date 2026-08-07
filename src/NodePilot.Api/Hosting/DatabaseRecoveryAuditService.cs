using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Audit;
using NodePilot.Data;
using NodePilot.Data.Availability;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Persists the one audit event that is meaningful for a database outage: recovery. The outage
/// itself cannot be written while the database is unavailable, so trip events are deliberately not
/// consumed. Recovery callbacks only enqueue process-local episode identities; all database work
/// runs asynchronously on a fresh scope and <see cref="NodePilotDbContext"/>.
/// </summary>
internal sealed class DatabaseRecoveryAuditService : BackgroundService
{
    private sealed record RecoveryWork(long EpisodeId, Guid AuditId);

    private readonly IDatabaseAvailability _availability;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseRecoveryAuditService> _logger;
    // Recovery episode ids are strictly increasing. A one-slot wake-up channel plus a monotone
    // high-water mark therefore retains every episode without an unbounded per-episode queue.
    private readonly Channel<bool> _recoveryWakeups = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(capacity: 1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    private long _lastScheduledEpisodeId;
    private long _lastPersistedEpisodeId;
    private int _subscribed;

    public DatabaseRecoveryAuditService(
        IDatabaseAvailability availability,
        IServiceScopeFactory scopeFactory,
        ILogger<DatabaseRecoveryAuditService> logger)
    {
        _availability = availability;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _subscribed, 1) == 0)
            _availability.OutageRecovered += OnOutageRecovered;
        return base.StartAsync(cancellationToken);
    }

    private void OnOutageRecovered(long episodeId)
    {
        if (episodeId <= 0 || !AdvanceScheduledEpisode(episodeId, out var previousEpisodeId))
            return;

        if (previousEpisodeId == 0)
            Volatile.Write(ref _lastPersistedEpisodeId, episodeId - 1);
        _recoveryWakeups.Writer.TryWrite(true);
    }

    private bool AdvanceScheduledEpisode(long episodeId, out long previousEpisodeId)
    {
        while (true)
        {
            var current = Volatile.Read(ref _lastScheduledEpisodeId);
            if (episodeId <= current)
            {
                previousEpisodeId = current;
                return false;
            }
            if (Interlocked.CompareExchange(ref _lastScheduledEpisodeId, episodeId, current) == current)
            {
                previousEpisodeId = current;
                return true;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var _ in _recoveryWakeups.Reader.ReadAllAsync(stoppingToken))
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var nextEpisodeId = Volatile.Read(ref _lastPersistedEpisodeId) + 1;
                    if (nextEpisodeId > Volatile.Read(ref _lastScheduledEpisodeId))
                        break;

                    var persisted = await PersistAsync(
                        new RecoveryWork(nextEpisodeId, Guid.NewGuid()),
                        stoppingToken);
                    if (!persisted)
                        return;
                    Volatile.Write(ref _lastPersistedEpisodeId, nextEpisodeId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    private async Task<bool> PersistAsync(RecoveryWork recovery, CancellationToken ct)
    {
        var retryAttempt = 0;
        while (!ct.IsCancellationRequested)
        {
            if (!_availability.IsServable
                && !await _availability.WaitUntilServableAsync(ct))
                return false;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
                var existing = await db.AuditLog.AsNoTracking()
                    .SingleOrDefaultAsync(entry => entry.Id == recovery.AuditId, ct);
                if (existing is not null)
                {
                    // SaveChanges may have committed and then lost its acknowledgement. The stable
                    // audit id adjudicates that outcome without inserting a second episode row.
                    AuditEventForwarder.ForwardCommitted(_logger, existing);
                    return true;
                }

                var stager = scope.ServiceProvider.GetRequiredService<IAuditStager>();
                var entry = stager.Build(
                    AuditActions.DatabaseRecovered,
                    AuditActor.System,
                    resourceType: "Database",
                    details: AuditDetails.Json(("outageEpisodeId", recovery.EpisodeId)));
                entry.Id = recovery.AuditId;
                db.AuditLog.Add(entry);
                await db.SaveChangesAsync(ct);
                AuditEventForwarder.ForwardCommitted(_logger, entry);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (DbUpdateException ex) when (DbErrorClassifier.IsUniqueConstraintViolation(ex))
            {
                // A concurrent/unknown first commit already inserted the stable id. The next fresh
                // scope reads it and completes without duplication.
                retryAttempt = 0;
            }
            catch (Exception ex) when (IsConfirmedDatabaseOutage(ex))
            {
                _logger.LogWarning(ex,
                    "DATABASE_RECOVERED audit for outage episode {OutageEpisodeId} paused until database recovery.",
                    recovery.EpisodeId);
                retryAttempt = 0;
                if (!await _availability.WaitUntilServableAsync(ct))
                    return false;
            }
            catch (Exception ex)
            {
                var delay = RetryDelay(retryAttempt++);
                _logger.LogWarning(ex,
                    "DATABASE_RECOVERED audit for outage episode {OutageEpisodeId} failed; retrying on a fresh context in {RetryDelayMs}ms.",
                    recovery.EpisodeId,
                    delay.TotalMilliseconds);
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return false;
                }
            }
        }

        return false;
    }

    private bool IsConfirmedDatabaseOutage(Exception exception)
        => !_availability.IsServable
           && DbErrorClassifier.Classify(exception) is not DbFailureKind.None;

    private static TimeSpan RetryDelay(int attempt)
    {
        var exponent = Math.Min(Math.Max(attempt, 0), 5);
        return TimeSpan.FromMilliseconds(Math.Min(2_000, 100 * (1 << exponent)));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        _recoveryWakeups.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        Unsubscribe();
        _recoveryWakeups.Writer.TryComplete();
        base.Dispose();
    }

    private void Unsubscribe()
    {
        if (Interlocked.Exchange(ref _subscribed, 0) == 1)
            _availability.OutageRecovered -= OnOutageRecovered;
    }
}
