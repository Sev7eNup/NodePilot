using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Data;

namespace NodePilot.Api.Hubs;

/// <summary>
/// Periodically aborts SignalR connections whose authenticating JWT has been revoked
/// (<c>RevokedTokens</c> table) or whose user has been deactivated (<c>User.IsActive=false</c>).
///
/// Without this, a long-lived WebSocket connection keeps the pre-revocation authorization
/// alive for hours: <see cref="Microsoft.AspNetCore.SignalR.Hub"/> authenticates once at
/// handshake time and does NOT re-evaluate auth per frame — so calling /auth/logout or
/// toggling IsActive in the UI only affects REST traffic, while a listening attacker keeps
/// receiving live step output through the hub until the 12 h token lifetime finally expires.
/// Audit M2.
///
/// Implementation: every 30 s, read the union of (revoked jtis, deactivated user ids) from
/// the DB, ask <see cref="ExecutionHub.FindConnectionsToDrop"/> which live connections match,
/// and call <c>HubLifetimeManager.DisconnectAsync</c> on them. Legit clients automatically
/// reconnect and fail the handshake because their token is now invalid — which is exactly
/// the desired bounce-to-login flow.
/// </summary>
public class HubRevocationSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<ExecutionHub> _hub;
    private readonly ILogger<HubRevocationSweeper> _logger;
    private readonly TimeSpan _interval;

    public HubRevocationSweeper(
        IServiceScopeFactory scopeFactory,
        IHubContext<ExecutionHub> hub,
        ILogger<HubRevocationSweeper> logger,
        IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(
            config.GetValue<int?>("Security:HubRevocationSweepSeconds") ?? 30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small warm-up delay so the hub lifetime manager is fully up.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HubRevocationSweeper iteration failed");
            }
            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Performs one revocation sweep — exposed internally so tests can invoke the sweep
    /// directly without spinning up the BackgroundService and waiting through the 10s
    /// warm-up delay. Same pattern as <see cref="Security.RevokedTokensCleanupService"/>.
    /// </summary>
    internal async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        var authorization = scope.ServiceProvider.GetRequiredService<ExternalAuthorizationEvaluator>();

        var connectedUserIds = ExecutionHub.GetConnectedUserIds();
        if (connectedUserIds.Count == 0) return;

        // Only load tokens that haven't yet expired — expired tokens fail JwtBearer validation
        // on the handshake anyway, so there's nothing to disconnect for them.
        var now = DateTime.UtcNow;
        var revokedJtis = (await db.RevokedTokens.AsNoTracking()
            .Where(r => r.ExpiresAt >= now)
            .Select(r => r.Jti)
            .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        var userStates = await db.Users.AsNoTracking()
            .Where(u => connectedUserIds.Contains(u.Id))
            .ToListAsync(ct);
        // Proactively expire connections whose authorization deadline falls before the
        // next sweep. This keeps the hard 15-minute bound even with polling jitter.
        var nextSweep = now + _interval;
        var connectedSessionIds = ExecutionHub.GetConnectedSessionIds();
        var serverSessions = await db.AuthSessions.AsNoTracking()
            .Where(session => connectedSessionIds.Contains(session.Id))
            .ToDictionaryAsync(
                session => session.Id,
                session => new ExecutionHub.ServerSessionState(
                    session.UserId,
                    session.CurrentJti,
                    session.ExpiresAt,
                    session.RevokedAt),
                ct);
        var invalidUsers = userStates
            .Where(u => !u.IsActive || u.IsTombstoned)
            .Select(u => u.Id)
            .ToHashSet();
        var externalUsers = userStates.Where(u => u.Provider != AuthProvider.Local
                                               && !invalidUsers.Contains(u.Id))
            .ToList();
        var evaluations = await authorization.EvaluateManyAsync(externalUsers, nextSweep, ct);
        foreach (var user in externalUsers)
        {
            if (!evaluations[user.Id].IsCurrent) invalidUsers.Add(user.Id);
        }
        var currentSecurityStamps = userStates
            .Where(u => !invalidUsers.Contains(u.Id))
            .ToDictionary(u => u.Id, u => u.SecurityStamp);

        var targets = ExecutionHub.FindConnectionsToDrop(
            revokedJtis,
            invalidUsers,
            currentSecurityStamps,
            serverSessions,
            nextSweep).ToList();
        if (targets.Count == 0) return;

        // Two-step termination (Audit M-3):
        //   1. best-effort "forceDisconnect" client event so a cooperating client can
        //      tear down cleanly and route the user to /login;
        //   2. then HubCallerContext.Abort() on the server side to hang up the transport
        //      regardless of whether the client cooperated.
        //
        // HubLifetimeManager does not expose a public DisconnectAsync on .NET 10, so we
        // route through the Context stored in ExecutionHub._connectionAuth. A malicious
        // client that ignores forceDisconnect no longer keeps receiving live step output
        // after revocation — Abort() closes the underlying connection immediately.
        foreach (var connId in targets)
        {
            try
            {
                await _hub.Clients.Client(connId).SendAsync("forceDisconnect",
                    new { reason = "token_revoked_or_user_deactivated" }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not notify connection {ConnId} before disconnect", connId);
            }

            try
            {
                var hubCtx = ExecutionHub.TryGetContext(connId);
                hubCtx?.Abort();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not abort connection {ConnId}", connId);
            }
            finally
            {
                // Belt-and-suspenders: if OnDisconnectedAsync doesn't fire promptly (e.g.
                // the transport is already half-dead), make sure a subsequent sweep won't
                // re-process this id.
                ExecutionHub.ForgetConnection(connId);
            }
        }

        _logger.LogInformation(
            "HubRevocationSweeper: disconnected {Count} SignalR connection(s) for revoked tokens / deactivated users",
            targets.Count);
    }
}
