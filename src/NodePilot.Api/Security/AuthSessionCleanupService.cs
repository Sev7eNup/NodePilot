using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Interfaces;
using NodePilot.Data;

namespace NodePilot.Api.Security;

/// <summary>Leader-only retention for expired or long-revoked server-side sessions.</summary>
public sealed class AuthSessionCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<AuthSessionCleanupService> logger,
    IClusterStateProvider cluster) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (cluster.IsLeader)
            {
                try
                {
                    var deleted = await SweepOnceAsync(stoppingToken);
                    if (deleted > 0)
                        logger.LogInformation("AuthSessionCleanup: removed {Count} expired sessions", deleted);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "AuthSessionCleanup sweep failed");
                }
            }

            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        var cutoff = DateTime.UtcNow;
        var sessions = await db.AuthSessions
            .Where(s => s.ExpiresAt < cutoff
                     || (s.RevokedAt != null && s.RevokedAt < cutoff.AddDays(-7)))
            .ExecuteDeleteAsync(ct);
        var oidcTickets = await db.OidcLoginTickets
            .Where(ticket => ticket.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(ct);
        return sessions + oidcTickets;
    }
}
