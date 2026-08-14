using Microsoft.Extensions.Configuration;
using NodePilot.Core.Enums;

namespace NodePilot.Scheduler.Notifications;

/// <summary>
/// Queued-long collector: scans still-pending executions older than <c>Threshold</c>
/// and emits one workflow-scoped <see cref="NotificationEventType.ExecutionQueuedLong"/> per
/// execution. EventKey shape: <c>queuedlong:{guidN}</c> (existence-check dedups across passes).
/// </summary>
internal sealed class QueuedLongExecutionCollector : ElapsedExecutionCollector
{
    public QueuedLongExecutionCollector(IConfiguration configuration)
        : base(configuration,
            thresholdKey: "Alerting:QueuedLongSeconds",
            defaultSeconds: 300,
            eventType: NotificationEventType.ExecutionQueuedLong,
            status: ExecutionStatus.Pending,
            eventKeyPrefix: "queuedlong",
            titlePrefix: "Execution queued long",
            elapsedVerb: "pending")
    {
    }
}
