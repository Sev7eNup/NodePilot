using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Telemetry;
using NodePilot.Data;

namespace NodePilot.Api.Security;

/// <summary>
/// Periodically deletes <c>RevokedTokens</c> rows whose <c>ExpiresAt</c> has passed. Without
/// this the table grows linearly with logouts + refreshes forever, and since every
/// authenticated request hits <c>TokenValidityMiddleware</c> (which does an <c>AnyAsync</c>
/// on the table), latency would creep up over months.
///
/// A row is safe to delete once its <c>ExpiresAt</c> is in the past because JWT expiry is
/// also enforced by <c>JwtBearer</c> — a token past its exp fails validation before the
/// middleware even looks at the revocation list.
/// </summary>
public class RevokedTokensCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RevokedTokensCleanupService> _logger;
    private readonly TimeSpan _interval;
    private readonly NodePilot.Core.Interfaces.IClusterStateProvider _cluster;

    public RevokedTokensCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<RevokedTokensCleanupService> logger,
        IConfiguration config,
        NodePilot.Core.Interfaces.IClusterStateProvider cluster)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _cluster = cluster;
        // Default: once a day. Not time-critical — expired rows just take a little space until
        // the next sweep. Configurable for testing (shortened interval) or high-volume ops.
        _interval = TimeSpan.FromHours(
            config.GetValue<int?>("Security:RevokedTokensCleanupIntervalHours") ?? 24);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small startup delay to stay out of the host's cold-start critical path.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            // HA gate: leader-only — both nodes deleting the same expired rows is just wasted IO.
            if (!_cluster.IsLeader)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                var sw = Stopwatch.StartNew();
                var deleted = await SweepOnceAsync(stoppingToken);
                sw.Stop();
                if (deleted > 0)
                {
                    _logger.LogInformation("RevokedTokensCleanup: removed {Count} expired tokens", deleted);
                    ApiMetrics.RevokedTokensDeleted.Add(deleted);
                }
                ApiMetrics.RevokedTokensSweepDuration.Record(sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RevokedTokensCleanup sweep failed");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Performs a single sweep: deletes every <c>RevokedTokens</c> row whose
    /// <c>ExpiresAt</c> is in the past. Returns the number of rows deleted. Exposed
    /// internally so tests can verify the deletion semantics without spinning up the
    /// background loop and waiting through the startup delay.
    /// </summary>
    internal async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        var cutoff = DateTime.UtcNow;
        return await db.RevokedTokens
            .Where(r => r.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }
}
