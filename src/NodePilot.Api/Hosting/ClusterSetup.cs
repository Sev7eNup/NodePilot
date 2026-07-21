using Microsoft.AspNetCore.Http;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Cluster;
using NodePilot.Scheduler.Cluster;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Wires the active/passive HA cluster components based on <c>Cluster:Enabled</c>.
/// <list type="bullet">
///   <item><description><c>Cluster:Enabled=false</c> (default): registers a no-op
///   <see cref="SingleNodeClusterStateProvider"/>. <c>IsLeader=true</c> always — every
///   downstream consumer behaves as if there is no cluster.</description></item>
///   <item><description><c>Cluster:Enabled=true</c>: registers the real
///   <see cref="ClusterLeaderService"/> as both <see cref="IHostedService"/> (drives the
///   renew loop) and <see cref="IClusterStateProvider"/> (read by every other
///   gate-on-leader component).</description></item>
/// </list>
/// </summary>
public static class ClusterSetup
{
    public static IServiceCollection AddNodePilotCluster(this IServiceCollection services,
        IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool>("Cluster:Enabled");
        if (!enabled)
        {
            var nodeId = configuration["Cluster:NodeId"];
            services.AddSingleton<IClusterStateProvider>(_ => new SingleNodeClusterStateProvider(nodeId));
            return services;
        }

        services.AddSingleton<ClusterLeaderService>();
        services.AddSingleton<IClusterStateProvider>(sp => sp.GetRequiredService<ClusterLeaderService>());
        services.AddHostedService(sp => sp.GetRequiredService<ClusterLeaderService>());

        // Failover-recovery handler: every time leadership changes hands, sweep any
        // non-terminal WorkflowExecution row whose OwnerNodeId is NOT us — those are
        // either dead-leader leftovers or pre-cluster legacy rows. Wired here so the
        // service registration stays in one file; the bound delegate captures
        // IServiceScopeFactory and IClusterStateProvider lazily.
        services.AddHostedService<ClusterFailoverRecoveryHost>();

        // Fencing handler: when this node *loses* leadership, cancel every workflow
        // execution it's still running so the new leader's recovery sweep can adopt
        // the orphans without DB write-races against the dead leader.
        services.AddHostedService<ClusterFencingHost>();
        return services;
    }

    /// <summary>
    /// Pure leader-health computation, extracted from the <c>/healthz/leader</c> endpoint
    /// for unit-testability. Returns 200 only when ALL fail-closed conditions hold:
    /// <c>IsLeader</c>, <c>LastSuccessfulRenewAt</c> within TTL, and <c>LeaseExpiresAt</c>
    /// in the future. The <paramref name="now"/> parameter is the comparison clock —
    /// passed in (instead of read from <see cref="DateTime.UtcNow"/>) so tests can pin time.
    /// </summary>
    public static IResult ComputeLeaderHealth(IClusterStateProvider cluster, TimeSpan leaseTtl, DateTime now)
    {
        var lastRenew = cluster.LastSuccessfulRenewAt;
        var renewWithinTtl = lastRenew.HasValue && (now - lastRenew.Value) < leaseTtl;
        var leaseFutureValid = cluster.LeaseExpiresAt.HasValue && cluster.LeaseExpiresAt.Value > now;
        var healthy = cluster.IsLeader && renewWithinTtl && leaseFutureValid;

        if (healthy)
        {
            return Results.Ok(new
            {
                status = "leader",
                nodeId = cluster.NodeId,
                leaseExpiresAt = cluster.LeaseExpiresAt,
                leaseEpoch = cluster.LeaseEpoch,
                lastRenewAt = lastRenew
            });
        }

        var reason = !cluster.IsLeader ? "not_leader"
                   : !renewWithinTtl ? "renew_stale"
                   : "lease_expired";
        return Results.Json(new
        {
            status = cluster.IsLeader ? "leader_unhealthy" : "follower",
            nodeId = cluster.NodeId,
            reason
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
