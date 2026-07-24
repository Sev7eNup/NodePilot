using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Audit;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler.Cluster;

/// <summary>
/// Holds and renews the active/passive leader lease in the <c>ClusterLeaders</c> table.
/// Both <see cref="BackgroundService"/> (drives the renew loop) and
/// <see cref="IClusterStateProvider"/> (read by every other component that gates work on
/// "am I the leader").
/// <para>
/// The lease is acquired and renewed via atomic <c>UPDATE ... WHERE</c> so two nodes
/// cannot both believe they are leader. A monotonic <see cref="ClusterLeader.LeaseEpoch"/>
/// is incremented on every acquire (not on renew) and serves as a fencing token in audit
/// events.
/// </para>
/// </summary>
public sealed class ClusterLeaderService : BackgroundService, IClusterStateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ClusterLeaderService> _logger;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _renewInterval;
    private readonly TimeSpan _dbTimeout;
    private readonly TimeSpan _renewGrace;
    private readonly TimeProvider _timeProvider;

    private readonly object _stateLock = new();
    private bool _isLeader;
    private DateTime? _leaseExpiresAt;
    private long _leaseEpoch;
    private DateTime? _lastSuccessfulRenewAt;
    private long? _lastSuccessfulRenewTimestamp;

    public string NodeId { get; }

    public bool IsLeader
    {
        get
        {
            lock (_stateLock)
            {
                if (!_isLeader || !_leaseExpiresAt.HasValue || !_lastSuccessfulRenewTimestamp.HasValue)
                    return false;
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var renewedWithinTtl = _timeProvider.GetElapsedTime(
                    _lastSuccessfulRenewTimestamp.Value, _timeProvider.GetTimestamp()) < _ttl;
                return renewedWithinTtl && _leaseExpiresAt.Value > now;
            }
        }
    }
    public DateTime? LeaseExpiresAt { get { lock (_stateLock) return _leaseExpiresAt; } }
    public long LeaseEpoch { get { lock (_stateLock) return _leaseEpoch; } }
    public DateTime? LastSuccessfulRenewAt { get { lock (_stateLock) return _lastSuccessfulRenewAt; } }

    public event Action<long>? OnLeadershipAcquired;
    public event Action? OnLeadershipLost;

    public ClusterLeaderService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ClusterLeaderService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var configuredNodeId = configuration["Cluster:NodeId"];
        NodeId = string.IsNullOrWhiteSpace(configuredNodeId) ? Environment.MachineName : configuredNodeId;

        _ttl = TimeSpan.FromSeconds(configuration.GetValue("Cluster:LeaseTtlSeconds", 30));
        _renewInterval = TimeSpan.FromSeconds(configuration.GetValue("Cluster:LeaseRenewSeconds", 10));
        _dbTimeout = TimeSpan.FromSeconds(configuration.GetValue("Cluster:LeaseDbTimeoutSeconds", 3));

        // Renew is allowed up to 5 s before the lease would have expired. This lets a leader
        // that briefly lost connectivity and recovered re-establish without a full failover
        // cycle. Outside the grace window the renew is rejected by the WHERE clause and the
        // node falls back to acquire.
        _renewGrace = TimeSpan.FromSeconds(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ClusterLeaderService starting. NodeId={NodeId}, TTL={TtlSec}s, RenewInterval={RenewSec}s, DbTimeout={DbSec}s",
            NodeId, _ttl.TotalSeconds, _renewInterval.TotalSeconds, _dbTimeout.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ClusterLeaderService tick failed; will retry next interval.");
                // On any error, treat it as "could not renew" and step down if we were leader.
                ApplyLeadershipChange(isLeader: false, leaseExpiresAt: null, epoch: null);
            }

            try { await Task.Delay(_renewInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        db.Database.SetCommandTimeout((int)_dbTimeout.TotalSeconds);

        // Use the DB clock as the single source of truth for lease comparisons. App clocks
        // can drift (NTP offset, container clock skew, VM pause) and a leader whose host
        // is a few seconds ahead would otherwise compute an ExpiresAt the standby reads as
        // "future" while the actual DB-now sees the same lease as expired — split-brain.
        // One extra round-trip per tick (~1 ms on a healthy connection) is cheap insurance.
        var nowDb = await ReadDbUtcNowAsync(db, ct);
        bool wasLeader;
        lock (_stateLock) { wasLeader = _isLeader; }

        // Renew path — only attempt if we currently believe we are the leader. The WHERE
        // clause double-checks ownership and grace window so we cannot accidentally renew
        // a lease that another node has taken over.
        if (wasLeader)
        {
            var newExpiresAt = nowDb + _ttl;
            var renewedRows = await db.Database.ExecuteSqlAsync($@"
                UPDATE ""ClusterLeaders""
                   SET ""ExpiresAt"" = {newExpiresAt},
                       ""LastRenewedAt"" = {nowDb}
                 WHERE ""Resource"" = 'primary'
                   AND ""OwnerNodeId"" = {NodeId}
                   AND ""ExpiresAt"" > {nowDb - _renewGrace}", ct);

            if (renewedRows == 1)
            {
                ApplyLeadershipChange(isLeader: true, leaseExpiresAt: newExpiresAt, epoch: null,
                    markRenewSuccess: true);
                return;
            }

            // Renew failed — lease expired or another node won. Fall through to acquire.
            _logger.LogWarning(
                "Leader renewal returned 0 rows — stepping down. Will attempt re-acquire on next tick.");
            ApplyLeadershipChange(isLeader: false, leaseExpiresAt: null, epoch: null);
        }

        // Acquire path — only succeeds if the existing lease is expired. EpochInc happens
        // database-side via LeaseEpoch + 1 so concurrent acquires race deterministically.
        var acquireExpiresAt = nowDb + _ttl;
        var acquiredRows = await db.Database.ExecuteSqlAsync($@"
            UPDATE ""ClusterLeaders""
               SET ""OwnerNodeId"" = {NodeId},
                   ""AcquiredAt"" = {nowDb},
                   ""ExpiresAt"" = {acquireExpiresAt},
                   ""LastRenewedAt"" = {nowDb},
                   ""LeaseEpoch"" = ""LeaseEpoch"" + 1
             WHERE ""Resource"" = 'primary'
               AND ""ExpiresAt"" < {nowDb}", ct);

        if (acquiredRows == 1)
        {
            // Re-read to capture the post-increment LeaseEpoch.
            var row = await db.ClusterLeaders.AsNoTracking()
                .SingleAsync(r => r.Resource == "primary", ct);
            _logger.LogInformation(
                "Acquired cluster lease. NodeId={NodeId}, LeaseEpoch={Epoch}, ExpiresAt={ExpiresAt:O}",
                NodeId, row.LeaseEpoch, row.ExpiresAt);
            await WriteLeadershipAcquiredAuditAsync(
                scope.ServiceProvider, db, row.LeaseEpoch, row.ExpiresAt, ct);
            ApplyLeadershipChange(isLeader: true, leaseExpiresAt: acquireExpiresAt,
                epoch: row.LeaseEpoch, markRenewSuccess: true);
        }
        // else: another node still holds a fresh lease — stay follower silently.
    }

    /// <summary>
    /// Persists and forwards the leadership transition after the lease update has committed.
    /// </summary>
    private async Task WriteLeadershipAcquiredAuditAsync(
        IServiceProvider services,
        NodePilotDbContext db,
        long leaseEpoch,
        DateTime expiresAt,
        CancellationToken ct)
    {
        try
        {
            var stager = services.GetService<IAuditStager>() ?? new AuditStager();
            var entry = stager.Build(
                AuditActions.ClusterLeadershipAcquired,
                AuditActor.System,
                resourceType: nameof(ClusterLeader),
                details: AuditDetails.Json(
                    ("nodeId", NodeId),
                    ("leaseEpoch", leaseEpoch),
                    ("expiresAt", expiresAt)));
            db.AuditLog.Add(entry);
            await db.SaveChangesAsync(ct);
            AuditEventForwarder.ForwardCommitted(_logger, entry);
        }
        catch (Exception ex)
        {
            // Lease acquisition is already committed by the atomic UPDATE. Audit persistence
            // must not make the node incorrectly believe that it is still a follower.
            _logger.LogWarning(ex,
                "Failed to persist cluster leadership acquisition audit. NodeId={NodeId}, LeaseEpoch={LeaseEpoch}",
                NodeId, leaseEpoch);
        }
    }

    /// <summary>
    /// Reads the database server's UTC clock so lease comparisons are anchored to the
    /// same authoritative time across all nodes. Falls back to <see cref="DateTime.UtcNow"/>
    /// for SQLite (in-memory test backend) and for any other provider whose dialect we
    /// haven't pinned — those paths never see a real cluster anyway.
    /// </summary>
    private static async Task<DateTime> ReadDbUtcNowAsync(NodePilotDbContext db, CancellationToken ct)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        string? sql = null;
        if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            sql = "SELECT (now() AT TIME ZONE 'UTC')";
        else if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            sql = "SELECT SYSUTCDATETIME()";

        if (sql is null) return DateTime.UtcNow;

        // GetDbConnection() returns EF's own connection — DO NOT dispose it; just
        // open if needed and close afterwards (mirrors EF's own scoped lifetime).
        var conn = db.Database.GetDbConnection();
        var opened = false;
        try
        {
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync(ct);
                opened = true;
            }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = db.Database.GetCommandTimeout().GetValueOrDefault(3);
            var raw = await cmd.ExecuteScalarAsync(ct);
            if (raw is DateTime dt) return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            if (raw is DateTimeOffset dto) return dto.UtcDateTime;
            return DateTime.UtcNow;
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }

    /// <summary>
    /// Centralizes state mutation under the lock and fires events outside the lock.
    /// </summary>
    private void ApplyLeadershipChange(bool isLeader, DateTime? leaseExpiresAt, long? epoch,
        bool markRenewSuccess = false)
    {
        bool wasLeader;
        bool nowLeader;
        long? newEpoch = null;
        lock (_stateLock)
        {
            wasLeader = _isLeader;
            _isLeader = isLeader;
            _leaseExpiresAt = leaseExpiresAt;
            if (epoch.HasValue) _leaseEpoch = epoch.Value;
            if (markRenewSuccess)
            {
                _lastSuccessfulRenewAt = _timeProvider.GetUtcNow().UtcDateTime;
                _lastSuccessfulRenewTimestamp = _timeProvider.GetTimestamp();
            }
            nowLeader = _isLeader;
            if (epoch.HasValue) newEpoch = epoch.Value;
        }

        if (!wasLeader && nowLeader)
        {
            try { OnLeadershipAcquired?.Invoke(newEpoch ?? 0); }
            catch (Exception ex) { _logger.LogWarning(ex, "OnLeadershipAcquired handler threw."); }
        }
        else if (wasLeader && !nowLeader)
        {
            try { OnLeadershipLost?.Invoke(); }
            catch (Exception ex) { _logger.LogWarning(ex, "OnLeadershipLost handler threw."); }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Best-effort lease release on graceful shutdown so the standby can take over
        // immediately instead of waiting for the TTL to expire.
        try
        {
            bool wasLeader;
            lock (_stateLock) { wasLeader = _isLeader; }
            if (wasLeader)
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
                db.Database.SetCommandTimeout((int)_dbTimeout.TotalSeconds);
                // Sentinel "expired-far-in-the-past" so the standby sees the lease as
                // immediately acquirable regardless of any app-clock skew between nodes.
                var expiredSentinel = DateTime.MinValue.ToUniversalTime();
                await db.Database.ExecuteSqlAsync($@"
                    UPDATE ""ClusterLeaders""
                       SET ""ExpiresAt"" = {expiredSentinel},
                           ""OwnerNodeId"" = ''
                     WHERE ""Resource"" = 'primary'
                       AND ""OwnerNodeId"" = {NodeId}", cancellationToken);
                _logger.LogInformation("Released cluster lease on shutdown. NodeId={NodeId}", NodeId);
                ApplyLeadershipChange(isLeader: false, leaseExpiresAt: null, epoch: null);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to release lease on shutdown (best-effort)."); }
        await base.StopAsync(cancellationToken);
    }
}
