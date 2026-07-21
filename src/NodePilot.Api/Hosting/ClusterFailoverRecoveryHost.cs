using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Engine.Execution;

namespace NodePilot.Api.Hosting;

/// <summary>
/// HostedService that listens for cluster-leadership transitions and runs a one-shot
/// orphan-recovery sweep each time the local node becomes leader. Co-located with the
/// cluster setup because there is no other reasonable home for "the bridge between
/// cluster events and DB recovery". Single-node mode never wires this — the boot-time
/// recovery path in Program.cs covers single-node restarts.
/// </summary>
public sealed class ClusterFailoverRecoveryHost : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClusterStateProvider _cluster;
    private readonly ILogger<ClusterFailoverRecoveryHost> _logger;

    public ClusterFailoverRecoveryHost(
        IServiceScopeFactory scopeFactory,
        IClusterStateProvider cluster,
        ILogger<ClusterFailoverRecoveryHost> logger)
    {
        _scopeFactory = scopeFactory;
        _cluster = cluster;
        _logger = logger;
        // Subscribe in the ctor (not in StartAsync) so we cannot miss the very first
        // OnLeadershipAcquired event: HostedService start order is undefined, so the
        // ClusterLeaderService tick may run between this object's construction and
        // its StartAsync — in which case the only acquire event of the boot would
        // otherwise have fired into a still-empty handler list, and orphan rows would
        // sit non-terminal until the next leader change.
        _cluster.OnLeadershipAcquired += OnAcquired;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cluster.OnLeadershipAcquired -= OnAcquired;
        return Task.CompletedTask;
    }

    private void OnAcquired(long epoch)
    {
        // The cluster-leader event fires on the BackgroundService thread; offload the DB
        // work onto the thread pool so we don't block the next renew tick. Errors are
        // logged but never propagated.
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
                var recovered = await StartupRecovery.RecoverOrphanedExecutionsAsync(
                    db, _logger, CancellationToken.None,
                    ourNodeId: _cluster.NodeId, leaseEpoch: epoch);
                _logger.LogInformation(
                    "Cluster failover recovery completed: epoch={Epoch}, recovered={Count}", epoch, recovered);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Cluster failover recovery failed for epoch {Epoch} — orphans may remain non-terminal until the next leader event.",
                    epoch);
            }
        });
    }
}
