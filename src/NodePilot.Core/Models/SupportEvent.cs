namespace NodePilot.Core.Models;

/// <summary>
/// Structured DB projection of a Serilog event logged with the <c>SupportLog=true</c> scope.
/// Written by the custom sink in <c>NodePilot.Api.Logging.SupportEventDbSink</c> through a
/// bounded channel and a background flush.
///
/// <para>Makes the events of the plain-text support log queryable as a table (filtering,
/// sorting, cursor pagination, export) without blocking the logging hot path and without a
/// second logging path per log source.</para>
///
/// <para>Not audit-grade: events are dropped when the channel is full or the database is
/// unavailable. The forensic record lives in <c>AuditLog</c>, and the plain-text file sink
/// covers the gaps in this table.</para>
/// </summary>
public class SupportEvent
{
    public Guid Id { get; set; }

    /// <summary>UTC timestamp of the Serilog event itself (not when it was inserted into the
    /// DB).</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Serilog log level as an int (Verbose=0, Debug=1, Information=2, Warning=3, Error=4,
    /// Fatal=5).</summary>
    public int Level { get; set; }

    /// <summary>
    /// Event-type discriminator taken from the scope property <c>support.event_type</c>.
    /// Values: <c>USER_LOG</c>, <c>EXECUTION_STARTED|SUCCEEDED|FAILED|CANCELLED</c>,
    /// <c>STEP_FAILED</c>, <c>AUDIT</c>, <c>SYSTEM_BOOT</c>, <c>MIGRATION_APPLIED</c>.
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Rendered Serilog message (templates resolved), redacted, max 8 KiB.</summary>
    public string Message { get; set; } = string.Empty;

    public Guid? WorkflowId { get; set; }

    /// <summary>Workflow name captured from the scope, frozen at write time — stays correct even if
    /// the workflow is renamed later.</summary>
    public string? WorkflowName { get; set; }

    public Guid? ExecutionId { get; set; }

    /// <summary>8-hex-character prefix of the ExecutionId, denormalized for human-readable grouping
    /// in the UI.</summary>
    public string? ExecutionShort { get; set; }

    public string? StepId { get; set; }

    public string? StepLabel { get; set; }

    public string? ActivityType { get; set; }

    /// <summary>Username frozen at write time (e.g. for audit events) — stays interpretable after
    /// the user is renamed or deleted.</summary>
    public string? UserName { get; set; }

    public Guid? UserId { get; set; }

    public string? TraceId { get; set; }

    public string? SpanId { get; set; }

    /// <summary>
    /// JSON-serialized "long-tail" properties — everything that doesn't get its own
    /// dedicated column (e.g. <c>duration_sec</c>, <c>steps_ok/failed/skipped</c>,
    /// <c>event.action</c>, <c>migration_count</c>). Redacted, max 8 KiB.
    /// </summary>
    public string? PropertiesJson { get; set; }
}
