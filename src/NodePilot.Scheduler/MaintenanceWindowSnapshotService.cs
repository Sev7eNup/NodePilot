using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Interfaces;

namespace NodePilot.Scheduler;

/// <summary>
/// Keeps the in-memory <see cref="IMaintenanceWindowEvaluator"/> snapshot fresh by reloading it
/// on a short interval. Deliberately NOT HA-leader-gated (unlike the retention sweeps): every
/// node serves manual/webhook/external traffic and must evaluate windows locally, and the
/// refresh is a read-only query so concurrent refreshes across nodes are harmless. Window CRUD
/// also calls <see cref="IMaintenanceWindowEvaluator.RefreshAsync"/> inline, so this loop is the
/// backstop that catches changes made on another node.
/// </summary>
public sealed class MaintenanceWindowSnapshotService : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    private readonly IMaintenanceWindowEvaluator _evaluator;
    private readonly ILogger<MaintenanceWindowSnapshotService> _logger;

    public MaintenanceWindowSnapshotService(
        IMaintenanceWindowEvaluator evaluator,
        ILogger<MaintenanceWindowSnapshotService> logger)
    {
        _evaluator = evaluator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MaintenanceWindowSnapshotService started (refresh every {Seconds}s).", RefreshInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _evaluator.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Fail-open philosophy: a failed refresh just leaves the previous snapshot in
                // place. Never throw out of the loop — that would kill the host's background
                // service and leave windows permanently stale.
                SchedulerMetrics.MaintenanceSnapshotRefreshErrors.Add(1);
                _logger.LogWarning(ex, "Maintenance-window snapshot refresh failed — keeping the previous snapshot.");
            }

            try { await Task.Delay(RefreshInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("MaintenanceWindowSnapshotService stopped.");
    }
}
