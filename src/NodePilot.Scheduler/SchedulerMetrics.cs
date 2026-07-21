using System.Diagnostics;
using System.Diagnostics.Metrics;
using NodePilot.Core.Telemetry;

namespace NodePilot.Scheduler;

/// <summary>
/// Scheduler / trigger-orchestrator metrics and the shared ActivitySource.
/// Uses literal names to keep this project decoupled from NodePilot.Telemetry
/// (kept in sync with <c>TelemetryConstants.Sources.Scheduler</c> /
/// <c>TelemetryConstants.Meters.Scheduler</c>).
/// </summary>
public static class SchedulerMetrics
{
    public static readonly ActivitySource Source = new("NodePilot.Scheduler");
    public static readonly Meter Meter = new("NodePilot.Scheduler", "1.0.0");

    public static readonly Counter<long> TriggersFired = Meter.CreateCounter<long>(
        "nodepilot.triggers.fired", unit: "1", description: "Number of times a trigger fired a workflow execution (tagged by trigger_type).");

    public static readonly Histogram<double> OrchestratorSyncDuration = Meter.CreateHistogram<double>(
        "nodepilot.trigger.orchestrator.sync.duration", unit: "ms", description: "Duration of a single TriggerOrchestrator sync pass.");

    public static readonly Counter<long> OrchestratorSyncChanges = Meter.CreateCounter<long>(
        "nodepilot.trigger.orchestrator.sync.changes", unit: "1", description: "Add/update/remove changes applied per orchestrator sync pass, tagged by change type.");

    public static readonly Counter<long> OrchestratorSyncFailures = Meter.CreateCounter<long>(
        "nodepilot.trigger.orchestrator.sync.failures", unit: "1", description: "Orchestrator sync passes that threw an exception.");

    public static readonly Counter<long> TriggerRegistrationFailures = Meter.CreateCounter<long>(
        "nodepilot.trigger.registration.failures", unit: "1", description: "Trigger registrations that failed (tagged by trigger_type).");

    // Per-source poll/event metrics. The orchestrator only knows about sync passes — the
    // individual sources (DB poll, FileSystemWatcher, Quartz, EventLog) emit these so we
    // can answer "which trigger fires hottest?" and "where do we lose time polling?".
    public static readonly Histogram<double> TriggerPollDuration = Meter.CreateHistogram<double>(
        "nodepilot.trigger.poll.duration", unit: "ms",
        description: "Polling/dispatch duration of a single trigger source pass, tagged by trigger_type.");

    public static readonly Counter<long> TriggerPollErrors = Meter.CreateCounter<long>(
        "nodepilot.trigger.poll.errors", unit: "1",
        description: "Errors during a trigger source poll/event-handle pass, tagged by trigger_type and error_class.");

    public static readonly Counter<long> TriggerEvents = Meter.CreateCounter<long>(
        "nodepilot.trigger.events", unit: "1",
        description: "Raw events seen by a trigger source before any debounce/dispatch (tagged by trigger_type and event_kind). Useful to spot noisy file watchers.");

    // Retention / cleanup — rows deleted, sweep duration, sweep errors per service.
    public static readonly Counter<long> RetentionRowsDeleted = Meter.CreateCounter<long>(
        "nodepilot.retention.rows_deleted", unit: "1",
        description: "Rows deleted by retention services, tagged by service.");

    public static readonly Histogram<double> RetentionSweepDuration = Meter.CreateHistogram<double>(
        "nodepilot.retention.sweep.duration", unit: "ms",
        description: "Duration of a single retention sweep pass, tagged by service.");

    public static readonly Counter<long> RetentionSweepErrors = Meter.CreateCounter<long>(
        "nodepilot.retention.sweep.errors", unit: "1",
        description: "Retention sweep passes that threw an exception, tagged by service.");

    // Audit archive integrity — pinned via SHA-256 sidecars next to each gzipped batch
    // file. The verify pass walks the archive volume and counts drifts; one mismatch is
    // almost always a sign of silent corruption (bit-rot, partial overwrite, tampering)
    // and warrants paging the on-call.
    public static readonly Counter<long> AuditArchiveVerified = Meter.CreateCounter<long>(
        "nodepilot.audit_archive.verified", unit: "1",
        description: "Audit archive files whose SHA-256 matched their sidecar.");

    public static readonly Counter<long> AuditArchiveHashDrift = Meter.CreateCounter<long>(
        "nodepilot.audit_archive.hash_drift", unit: "1",
        description: "Audit archive files whose SHA-256 no longer matches the .sha256 sidecar — silent corruption or tampering.");

    public static readonly Counter<long> AuditArchiveSidecarMissing = Meter.CreateCounter<long>(
        "nodepilot.audit_archive.sidecar_missing", unit: "1",
        description: "Audit archive files with no .sha256 sidecar. Pre-Phase-3 files don't have one; new ones missing it indicate a write-time failure.");

    // Maintenance windows — trigger fires suppressed because a window blocks the workflow, and
    // failures of the periodic snapshot refresh that feeds the in-memory evaluator.
    public static readonly Counter<long> MaintenanceWindowBlocks = Meter.CreateCounter<long>(
        "nodepilot.maintenance_window.blocks", unit: "1",
        description: "Trigger fires suppressed by an active maintenance window, tagged by trigger_type.");

    public static readonly Counter<long> MaintenanceSnapshotRefreshErrors = Meter.CreateCounter<long>(
        "nodepilot.maintenance_window.snapshot_refresh_errors", unit: "1",
        description: "Failures of the maintenance-window snapshot refresh pass.");
}
