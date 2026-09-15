using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Data.Availability;
using NodePilot.Engine.Execution;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Runs and resumes bounded orphan recovery while the node owns the acquired epoch.
/// </summary>
public sealed class ClusterFailoverRecoveryHost : IHostedService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClusterStateProvider _cluster;
    private readonly ILogger<ClusterFailoverRecoveryHost> _logger;
    private readonly IDatabaseAvailability _availability;
    private readonly TimeSpan _batchTimeout;
    private readonly object _gate = new();
    private CancellationTokenSource? _epochCancellation;
    private Task _recoveryTask = Task.CompletedTask;
    private long? _activeEpoch;
    private bool _stopped;

    public ClusterFailoverRecoveryHost(
        IServiceScopeFactory scopeFactory,
        IClusterStateProvider cluster,
        ILogger<ClusterFailoverRecoveryHost> logger,
        IDatabaseAvailability availability,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _cluster = cluster;
        _logger = logger;
        _availability = availability;
        var leaseTimeoutSeconds = configuration.GetValue("Cluster:LeaseDbTimeoutSeconds", 3);
        _batchTimeout = TimeSpan.FromSeconds(leaseTimeoutSeconds > 0
            ? Math.Min(2, leaseTimeoutSeconds * (2.0 / 3)) : 2);
        // Subscribe in the ctor (not in StartAsync) so we cannot miss the very first
        // OnLeadershipAcquired event: HostedService start order is undefined, so the
        // ClusterLeaderService tick may run between this object's construction and
        // its StartAsync — in which case the only acquire event of the boot would
        // otherwise have fired into a still-empty handler list, and orphan rows would
        // sit non-terminal until the next leader change.
        _cluster.OnLeadershipAcquired += OnAcquired;
        _cluster.OnLeadershipLost += OnLost;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cluster.OnLeadershipAcquired -= OnAcquired;
        _cluster.OnLeadershipLost -= OnLost;
        Task task;
        lock (_gate)
        {
            _stopped = true;
            _epochCancellation?.Cancel();
            task = _recoveryTask;
        }
        await task.WaitAsync(cancellationToken);
    }

    private void OnAcquired(long epoch)
    {
        lock (_gate)
        {
            if (_stopped || _activeEpoch == epoch && !_recoveryTask.IsCompleted) return;
            _epochCancellation?.Cancel();
            var previous = _recoveryTask;
            var cancellation = new CancellationTokenSource();
            _epochCancellation = cancellation;
            _activeEpoch = epoch;
            _recoveryTask = Task.Run(async () =>
            {
                try
                {
                    // A cancelled attempt must release its transaction before the next epoch starts.
                    await previous;
                    await RecoverEpochAsync(epoch, cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Cluster recovery task stopped unexpectedly. Epoch={Epoch}", epoch);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_epochCancellation, cancellation)) _epochCancellation = null;
                        cancellation.Dispose();
                    }
                }
            });
        }
    }

    private void OnLost()
    {
        lock (_gate) _epochCancellation?.Cancel();
    }

    private async Task RecoverEpochAsync(long epoch, CancellationToken ct)
    {
        var totalRecovered = 0;
        while (!ct.IsCancellationRequested && _cluster.IsLeader && _cluster.LeaseEpoch == epoch)
        {
            if (!await _availability.WaitUntilServableAsync(ct)) return;
            if (!_cluster.IsLeader || _cluster.LeaseEpoch != epoch) return;
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
                var recovered = await StartupRecovery.RecoverOrphanedExecutionsAsync(
                    db, _logger, ct, ourNodeId: _cluster.NodeId, leaseEpoch: epoch,
                    clusterBatchTimeout: _batchTimeout, availability: _availability);
                totalRecovered += recovered;
                if (ct.IsCancellationRequested || !_cluster.IsLeader || _cluster.LeaseEpoch != epoch) return;
                if (await db.ClusterLeaders.AsNoTracking().AnyAsync(l => l.Resource == "primary"
                    && l.OwnerNodeId == _cluster.NodeId && l.LeaseEpoch == epoch && l.ExpiresAt > DateTime.UtcNow, ct))
                    _logger.LogInformation(
                        "Cluster failover recovery completed: epoch={Epoch}, recovered={Count}", epoch, totalRecovered);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (ClusterRecoveryDeferredException ex)
            {
                totalRecovered += ex.CompletedExecutions;
                _logger.LogWarning(ex,
                    "Cluster recovery deferred after {Count} committed executions; retrying in 5 seconds. Epoch={Epoch}",
                    ex.CompletedExecutions, epoch);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cluster recovery failed; retrying in 5 seconds while epoch {Epoch} is owned", epoch);
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    public void Dispose()
    {
        _cluster.OnLeadershipAcquired -= OnAcquired;
        _cluster.OnLeadershipLost -= OnLost;
        lock (_gate)
        {
            _stopped = true;
            _epochCancellation?.Cancel();
        }
    }
}
