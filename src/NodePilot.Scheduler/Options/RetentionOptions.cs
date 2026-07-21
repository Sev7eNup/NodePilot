namespace NodePilot.Scheduler.Options;

/// <summary>
/// Shared settings for the five config-backed retention sweepers (Executions, AuditLog,
/// WorkflowVersions, Notifications, SupportEvents). Bound from the <c>Retention:*</c>
/// configuration section. Each sweeper reads its subsection off this parent POCO — registered
/// once so operators see a single cohesive block in appsettings.
/// (<c>IdempotencyKeyCleanupService</c> is deliberately not represented here: its sweep is
/// always-on with a fixed TTL/interval and has no config surface by design.)
/// </summary>
/// <remarks>
/// Defaults keep the sweepers on (opt-out, not opt-in) because a silent default-off would be
/// a subtle foot-gun: in 3-month production uptime the DB grows into the millions of rows per
/// table without them.
/// </remarks>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public ExecutionsRetentionOptions Executions { get; set; } = new();
    public AuditLogRetentionOptions AuditLog { get; set; } = new();
    public WorkflowVersionsRetentionOptions WorkflowVersions { get; set; } = new();
    public NotificationsRetentionOptions Notifications { get; set; } = new();
    public SupportEventsRetentionOptions SupportEvents { get; set; } = new();
}

public sealed class ExecutionsRetentionOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxAgeDays { get; set; } = 30;
    public int IntervalMinutes { get; set; } = 60;
    public int BatchSize { get; set; } = 500;
    /// <summary>
    /// Optional directory. If set, each sweep appends an NDJSON snapshot of deleted rows
    /// to <c>{ArchivePath}\executions-YYYYMMDD.ndjson</c> before the delete commits.
    /// Validated + write-probed once at first use.
    /// </summary>
    public string? ArchivePath { get; set; }
}

public sealed class AuditLogRetentionOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxAgeDays { get; set; } = 365;
    public int IntervalMinutes { get; set; } = 720;
    public int BatchSize { get; set; } = 1000;
    public string? ArchivePath { get; set; }
    /// <summary>
    /// How often the retention service walks <see cref="ArchivePath"/> and recomputes
    /// the SHA-256 of every <c>audit-*.ndjson.gz</c> against its <c>.sha256</c> sidecar.
    /// Detects silent corruption (bit-rot, partial overwrite, accidental edit) and emits
    /// a metric + warning per drifted file. Daily by default; raise on slow archive
    /// volumes, lower (e.g. 60) when running on flaky storage. Set to 0 to disable.
    /// </summary>
    public int VerifyIntervalMinutes { get; set; } = 1440;
    /// <summary>
    /// Soft cap on files inspected per verify pass. Audit archives accumulate roughly
    /// 2 files per day × <see cref="MaxAgeDays"/>; capping the per-pass scan keeps the
    /// duration bounded on archives with hundreds of thousands of historic files.
    /// Files beyond the cap are picked up on subsequent passes (oldest-first ordering).
    /// </summary>
    public int VerifyMaxFilesPerPass { get; set; } = 500;
}

public sealed class WorkflowVersionsRetentionOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxVersionsPerWorkflow { get; set; } = 50;
    public int IntervalMinutes { get; set; } = 1440;
    public int BatchSize { get; set; } = 500;
}

/// <summary>
/// Alerting delivery ledger + stale suppression states (<c>Retention:Notifications:*</c>).
/// Delete-where without archive — delivery attempts are operational telemetry, not audit-grade.
/// </summary>
public sealed class NotificationsRetentionOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxAgeDays { get; set; } = 90;
    public int IntervalMinutes { get; set; } = 360;
}

/// <summary>
/// Support-log DB projection (<c>Retention:SupportEvents:*</c>). Delete-where without archive —
/// support events are not audit-grade; compliance retention lives in the AuditLog sweeper.
/// MaxAgeDays default matches the file-based support-log retention (90d).
/// </summary>
public sealed class SupportEventsRetentionOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxAgeDays { get; set; } = 90;
    public int IntervalMinutes { get; set; } = 360;
}
