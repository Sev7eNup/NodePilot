using NodePilot.Core.Interfaces;
using NodePilot.Engine;
using NodePilot.Scheduler.Cluster;

namespace NodePilot.Api.Hosting;

/// <summary>
/// HostedService that listens for cluster-leadership-loss and fences this node by
/// cancelling every workflow execution it's currently running. Without this, an old
/// leader that lost the lease (network partition, GC pause, slow disk) would keep
/// writing to <c>WorkflowExecutions</c>/<c>StepExecutions</c> while the new leader
/// already runs the same orphans through its recovery sweep — split-brain on the
/// run-row level.
/// <para>
/// Only registered in cluster mode; single-node mode never fires <c>OnLeadershipLost</c>.
/// </para>
/// </summary>
public sealed class ClusterFencingHost : IHostedService
{
    private readonly ClusterLeaderService _leader;
    private readonly ILogger<ClusterFencingHost> _logger;

    public ClusterFencingHost(
        ClusterLeaderService leader,
        ILogger<ClusterFencingHost> logger)
    {
        _leader = leader;
        _logger = logger;
        // Subscribe in the constructor so we never miss an event between DI build-up
        // and StartAsync. The leader BackgroundService tick can already fire by then.
        _leader.OnLeadershipLost += OnLost;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _leader.OnLeadershipLost -= OnLost;
        return Task.CompletedTask;
    }

    private void OnLost()
    {
        // Fire-and-forget cancellation: the leader event runs on the renew-loop thread
        // and must not block. CancelAllLocalAsync only triggers CancellationTokenSources
        // — the engine's main loops drain themselves; we don't await their completion.
        _ = Task.Run(async () =>
        {
            try
            {
                var cancelled = await WorkflowEngine.CancelAllLocalAsync(CancellationToken.None);
                _logger.LogWarning(
                    "Lost cluster leadership — fenced {Count} local execution(s) by cancellation. " +
                    "The new leader will adopt them on its next recovery sweep.",
                    cancelled);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fence local executions on leadership loss.");
            }
        });
    }
}
