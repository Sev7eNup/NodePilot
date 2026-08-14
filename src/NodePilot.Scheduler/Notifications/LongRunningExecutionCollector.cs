using Microsoft.Extensions.Configuration;
using NodePilot.Core.Enums;

namespace NodePilot.Scheduler.Notifications;

/// <summary>
/// Long-running collector: scans STILL-RUNNING executions older than <c>Threshold</c>
/// and emits an <see cref="NotificationEventType.ExecutionRunningLong"/> context per execution.
/// It is execution-scoped (carries a real WorkflowId → Global/Folders/Workflows scope), NOT a
/// gauge — the per-(rule,route,EventKey) existence-check on <c>runlong:{execId}</c> fires each
/// rule at most once per execution (no per-execution signal-state row to leak). Re-runs every
/// pass so a newly-crossed execution is picked up; finished executions simply drop out of the
/// RUNNING scan.
/// </summary>
internal sealed class LongRunningExecutionCollector : ElapsedExecutionCollector
{
    public LongRunningExecutionCollector(IConfiguration configuration)
        : base(configuration,
            thresholdKey: "Alerting:LongRunningSeconds",
            defaultSeconds: 600,
            eventType: NotificationEventType.ExecutionRunningLong,
            status: ExecutionStatus.Running,
            eventKeyPrefix: "runlong",
            titlePrefix: "Execution running long",
            elapsedVerb: "running")
    {
    }
}
