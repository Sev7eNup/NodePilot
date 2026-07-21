namespace NodePilot.Core.Models;

/// <summary>
/// Structured DB projection of a Serilog event logged with the <c>SupportLog=true</c> scope.
/// Populated by the custom sink in <c>NodePilot.Api.Logging.SupportEventDbSink</c> via a
/// bounded channel + background flush.
///
/// <para>Design goal: make the same events that land in the plain-text support log also
/// available as an indexable table for the enterprise viewer (filtering, sorting, cursor
/// pagination, export) — without blocking the hot path and without requiring each log
/// source to write to a second logging path.</para>
///
/// <para>Not audit-grade: on a full channel or DB outage, events are dropped best-effort
/// (drop-newest + an OTel counter). Forensic guarantees still live in <c>AuditLog</c>;
/// the plain-text file sink is the fallback for when this DB column has gaps.</para>
/// </summary>
public class SupportEvent
{
    public Guid Id { get; set; }

    /// <summary>UTC timestamp of the Serilog event itself (not when it was inserted into the DB).</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Serilog log level as an int (Verbose=0, Debug=1, Information=2, Warning=3, Error=4, Fatal=5).</summary>
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

    /// <summary>Workflow name captured from the scope, frozen at write time — stays correct even if the workflow is renamed later.</summary>
    public string? WorkflowName { get; set; }

    public Guid? ExecutionId { get; set; }

    /// <summary>8-hex-character prefix of the ExecutionId, denormalized for human-readable grouping in the UI.</summary>
    public string? ExecutionShort { get; set; }

    public string? StepId { get; set; }

    public string? StepLabel { get; set; }

    public string? ActivityType { get; set; }

    /// <summary>Username frozen at write time (e.g. for audit events) — stays interpretable after the user is renamed or deleted.</summary>
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
