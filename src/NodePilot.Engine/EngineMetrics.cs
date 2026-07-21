using System.Diagnostics.Metrics;
using NodePilot.Core.Telemetry;

namespace NodePilot.Engine;

/// <summary>
/// Central registry of engine-level metrics. All instruments live on a single
/// <see cref="System.Diagnostics.Metrics.Meter"/> so the OpenTelemetry pipeline can
/// subscribe once via <c>AddMeter(TelemetryConstants.Meters.Engine)</c>.
/// </summary>
public static class EngineMetrics
{
    public static readonly Meter Meter = new(TelemetryConstants.Meters.Engine, "1.0.0");

    // Workflow execution lifecycle
    public static readonly Counter<long> ExecutionsStarted = Meter.CreateCounter<long>(
        "nodepilot.executions.started", unit: "1", description: "Workflow executions that were started.");

    public static readonly Counter<long> ExecutionsCompleted = Meter.CreateCounter<long>(
        "nodepilot.executions.completed", unit: "1", description: "Workflow executions that reached a terminal state (Succeeded/Failed/Cancelled).");

    public static readonly UpDownCounter<long> ExecutionsActive = Meter.CreateUpDownCounter<long>(
        "nodepilot.executions.active", unit: "1", description: "Number of currently running workflow executions.");

    public static readonly Histogram<double> ExecutionDuration = Meter.CreateHistogram<double>(
        "nodepilot.execution.duration", unit: "ms", description: "End-to-end duration of a workflow execution.");

    public static readonly Histogram<long> ExecutionNodesExecuted = Meter.CreateHistogram<long>(
        "nodepilot.execution.nodes_executed", unit: "1", description: "Number of nodes that actually ran during a workflow execution.");

    public static readonly Histogram<long> ExecutionNodesSkipped = Meter.CreateHistogram<long>(
        "nodepilot.execution.nodes_skipped", unit: "1", description: "Number of nodes that were skipped (unreachable / disabled edges) per execution.");

    public static readonly Counter<long> Cancellations = Meter.CreateCounter<long>(
        "nodepilot.execution.cancellations", unit: "1", description: "Workflow executions that ended via cancellation.");

    // H-3 (security-audit finding): capacity-cap rejections. Spikes here indicate
    // resource-exhaustion pressure (a legitimate burst of triggers, a compromised
    // account, or a runaway webhook). Tagged by reason (global_cap / per_user_cap) so
    // the operator can see which limit actually kicked in.
    public static readonly Counter<long> ExecutionsRejected = Meter.CreateCounter<long>(
        "nodepilot.executions.rejected", unit: "1",
        description: "Workflow executions rejected by the engine capacity caps, tagged by reason.");

    // Step execution
    public static readonly Counter<long> StepsExecuted = Meter.CreateCounter<long>(
        "nodepilot.steps.executed", unit: "1", description: "Individual step executions, tagged by activity_type and status.");

    public static readonly Histogram<double> StepDuration = Meter.CreateHistogram<double>(
        "nodepilot.step.duration", unit: "ms", description: "Per-step execution duration, tagged by activity_type and status.");

    // Retry observability — the step-status histogram already shows "did this step succeed",
    // these break out the per-attempt cost so a flaky-but-eventually-succeeding step is
    // distinguishable from a clean first-shot success on the dashboard.
    public static readonly Counter<long> RetryAttempts = Meter.CreateCounter<long>(
        "nodepilot.step.retry.attempts", unit: "1",
        description: "Step retry attempts beyond the initial try, tagged by activity_type.");

    public static readonly Histogram<double> RetryBackoffDuration = Meter.CreateHistogram<double>(
        "nodepilot.step.retry.backoff.duration", unit: "ms",
        description: "Time spent in retry-policy backoff before re-running a failed step.");

    // Workflow DB-write observability. These metrics are emitted around the hot
    // SaveChanges calls in the execution path so load tests can separate "too many
    // writes" from "slow writes" without enabling verbose EF SQL logging.
    public static readonly Counter<long> DbSaveChanges = Meter.CreateCounter<long>(
        "nodepilot.db.save_changes", unit: "1",
        description: "SaveChanges calls issued by workflow execution paths, tagged by operation and status.");

    public static readonly Histogram<double> DbSaveChangesDuration = Meter.CreateHistogram<double>(
        "nodepilot.db.save_changes.duration", unit: "ms",
        description: "SaveChanges latency for workflow execution persistence.");

    public static readonly Histogram<long> DbSaveChangesRows = Meter.CreateHistogram<long>(
        "nodepilot.db.save_changes.rows", unit: "1",
        description: "EF state entries written by workflow execution SaveChanges calls.");

    // Audit-write observability — the writer is best-effort and swallows exceptions, so a
    // counter here is the only way to notice that the audit log silently stopped accepting
    // writes (DB outage, table corruption, retention sweep blocking).
    public static readonly Counter<long> AuditWrites = Meter.CreateCounter<long>(
        "nodepilot.audit.writes", unit: "1",
        description: "AuditWriter.LogAsync calls, tagged by result (success/failure).");

    public static readonly Histogram<double> AuditWriteDuration = Meter.CreateHistogram<double>(
        "nodepilot.audit.write.duration", unit: "ms",
        description: "AuditWriter.LogAsync persistence latency.");

    // Support-event DB projection: this drop counter is the only way to see that the
    // web viewer is falling behind (DB outage, flush loop overloaded, or the
    // 1024-item channel is full). Tag `reason` distinguishes the cause (channel_full =
    // the sink couldn't write; db_insert_failed = the flush loop couldn't complete
    // SaveChanges).
    public static readonly Counter<long> SupportEventsDropped = Meter.CreateCounter<long>(
        "nodepilot.support.events.dropped", unit: "1",
        description: "Support-Log DB-Projektion: verworfene Events (Channel-Voll oder DB-Failure).");

    public static readonly Counter<long> SupportEventsWritten = Meter.CreateCounter<long>(
        "nodepilot.support.events.written", unit: "1",
        description: "Support-Log DB-Projektion: erfolgreich in die DB-Tabelle geschriebene Events.");

    // OutputRedactor visibility — a sudden spike means a script started leaking secrets the
    // redactor catches; without this counter the only signal is "look how many '***' there
    // are in the log file".
    public static readonly Counter<long> RedactionHits = Meter.CreateCounter<long>(
        "nodepilot.output.redaction.hits", unit: "1",
        description: "Pattern matches the OutputRedactor masked, tagged by pattern_kind.");

    // Debug-session visibility — operators need to see "how many users are paused at a
    // breakpoint right now?" without polling StepExecution rows.
    public static readonly UpDownCounter<long> DebugSessionsActive = Meter.CreateUpDownCounter<long>(
        "nodepilot.debug.sessions.active", unit: "1",
        description: "Currently paused debug sessions across the engine.");

    public static readonly Histogram<double> DebugPauseDuration = Meter.CreateHistogram<double>(
        "nodepilot.debug.pause.duration", unit: "ms",
        description: "How long a step stayed paused at a breakpoint before resume / timeout.");

    public static readonly Counter<long> DebugResumeCommands = Meter.CreateCounter<long>(
        "nodepilot.debug.resume.commands", unit: "1",
        description: "Debug resume commands issued, tagged by mode (continue/stepOver/stop).");
}
