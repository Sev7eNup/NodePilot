using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Notifications;

namespace NodePilot.Scheduler.Notifications;

/// <summary>
/// Shared body of the in-flight execution-age collectors (long-running, queued-long). Both scan
/// executions that have been sitting in ONE non-terminal status longer than <see cref="Threshold"/>
/// and emit one execution-scoped context per row; they differ only in the config key, the event type,
/// the scanned status and the EventKey prefix. Each flavour stays its own collector instance so its
/// EventKey shape keeps exactly one owner across collection AND crash recovery.
/// </summary>
internal abstract class ElapsedExecutionCollector : INotificationCollector
{
    private readonly IConfiguration _configuration;
    private readonly string _thresholdKey;
    private readonly NotificationEventType _eventType;
    private readonly ExecutionStatus _status;
    private readonly string _eventKeyPrefix;
    private readonly string _titlePrefix;
    private readonly string _elapsedVerb;

    // An execution older than this fires the flavour's event (once per execution).
    // Initialised from the flavour's config key; hot-reload overlaid per pass;
    // settable in tests via the dispatcher's forwarding property.
    internal TimeSpan Threshold { get; set; }

    protected ElapsedExecutionCollector(
        IConfiguration configuration,
        string thresholdKey,
        int defaultSeconds,
        NotificationEventType eventType,
        ExecutionStatus status,
        string eventKeyPrefix,
        string titlePrefix,
        string elapsedVerb)
    {
        _configuration = configuration;
        _thresholdKey = thresholdKey;
        _eventType = eventType;
        _status = status;
        _eventKeyPrefix = eventKeyPrefix;
        _titlePrefix = titlePrefix;
        _elapsedVerb = elapsedVerb;
        Threshold = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue(thresholdKey, defaultSeconds)));
    }

    public async Task<NotificationCollection?> CollectAsync(
        NodePilotDbContext db, IReadOnlyList<NotificationRule> enabledRules, DateTime now, CancellationToken ct)
    {
        // Hot-reload: overlay the threshold every pass so a live edit takes effect without a
        // restart. Only overlay when the key is explicitly set — tests that set the property
        // directly with an empty config keep their value.
        var seconds = _configuration.GetValue<int?>(_thresholdKey);
        if (seconds.HasValue) Threshold = TimeSpan.FromSeconds(Math.Max(1, seconds.Value));

        var rules = enabledRules
            .Where(r => NotificationRuleSemantics.RuleWants(r, _eventType))
            .ToList();
        if (rules.Count == 0) return null; // nothing to alert on → skip the scan entirely

        var cutoff = now - Threshold;
        // Local copy: the query closure must capture a local, not this collector instance.
        var status = _status;
        var batch = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Status == status && e.StartedAt <= cutoff)
            .OrderBy(e => e.StartedAt)
            .Take(ExecutionEventSupport.ScanBatchSize)
            .Select(ExecutionEventSupport.Projection)
            .ToListAsync(ct);
        if (batch.Count == 0) return null;

        var contexts = batch.Select(r => BuildContext(r, now)).ToList<NotificationContext>();
        return new NotificationCollection(rules, contexts);
    }

    public async Task<NotificationContext?> TryReconstructContextAsync(
        NodePilotDbContext db, string eventKey, CancellationToken ct)
    {
        // Shape: {prefix}:{guidN}. Re-derive from the (still in-flight) row. Without this branch a
        // crash-orphaned attempt would match no collector and be failed out (lost alert).
        var parts = eventKey.Split(':');
        if (parts.Length != 2 || parts[0] != _eventKeyPrefix || !Guid.TryParse(parts[1], out var execId)) return null;

        var row = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == execId)
            .Select(ExecutionEventSupport.Projection)
            .FirstOrDefaultAsync(ct);
        return row is null ? null : BuildContext(row, DateTime.UtcNow);
    }

    private NotificationContext BuildContext(ExecRow row, DateTime now)
    {
        var elapsedMs = (long)(now - row.StartedAt).TotalMilliseconds;
        return new NotificationContext(
            EventType: _eventType,
            Severity: NotificationSeverity.Warning,
            // No time/type segment → one occurrence per execution; the existence-check dedups across passes
            // so a still-in-flight job never re-alerts every 30s.
            EventKey: $"{_eventKeyPrefix}:{row.Id:N}",
            WorkflowId: row.WorkflowId,
            WorkflowName: row.WorkflowName,
            FolderId: row.FolderId,
            FolderPath: row.FolderPath,
            ExecutionId: row.Id,
            Status: _status.ToString(),
            ErrorMessage: null,
            DurationMs: elapsedMs,
            OccurredAt: now,
            TriggeredBy: row.TriggeredBy,
            CallDepth: row.ParentExecutionId.HasValue ? 1 : 0,
            IsSubWorkflow: row.ParentExecutionId.HasValue,
            TargetMachine: null,
            SourceKey: null,
            Title: $"{_titlePrefix}: {row.WorkflowName}",
            Summary: $"Execution has been {_elapsedVerb} for ~{(long)(now - row.StartedAt).TotalMinutes} min.",
            DeepLinkPath: $"/executions/{row.Id}");
    }
}
