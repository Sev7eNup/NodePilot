using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodePilot.Ai;
using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Data;
using Quartz;

namespace NodePilot.Api.Controllers;

[ApiController]
[Route("api/stats")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly NodePilotDbContext _db;
    private readonly IResourceAuthorizationService _authz;
    private readonly IClusterStateProvider? _cluster;
    private readonly IOptionsMonitor<LlmOptions>? _llmOptions;

    public DashboardController(NodePilotDbContext db,
        IResourceAuthorizationService authz,
        IClusterStateProvider? cluster = null,
        IOptionsMonitor<LlmOptions>? llmOptions = null)
    {
        _db = db;
        _authz = authz;
        _cluster = cluster;
        _llmOptions = llmOptions;
    }

    [HttpGet("dashboard")]
    public async Task<ActionResult<DashboardStats>> Get(CancellationToken ct, [FromQuery] int windowHours = 24)
    {
        // The hero/area/donut/success-trend charts honour the caller-selected window.
        // Allowed: 1/24/168/720. Out-of-range values clamp to a sane default rather than
        // rejecting — the dashboard is a glance surface, not a strict API contract.
        if (windowHours <= 0 || windowHours > 720) windowHours = 24;
        var now = DateTime.UtcNow;
        var sinceWindow = now.AddHours(-windowHours);
        var since7d = now.AddDays(-7);
        var longRunningCutoff = now.AddMinutes(-30);

        // RBAC: dashboard aggregates must respect folder permissions. Compute the
        // accessible-folder set once and reuse for every workflow + execution query
        // below. Global Admin gets the unrestricted set and skips filtering.
        var accessible = await _authz.GetAccessibleFolderIdsAsync(User, ct);
        var workflowQuery = _db.Workflows.AsNoTracking().AsQueryable();
        var execQuery = _db.WorkflowExecutions.AsNoTracking().AsQueryable();
        if (!accessible.IsUnrestricted)
        {
            if (accessible.FolderIds.Count == 0)
            {
                // User has zero folder access — return an empty dashboard rather than a
                // potentially confusing partial one.
                return Ok(EmptyStats(sinceWindow, windowHours, NormalizeProvider(_db.Database.ProviderName), GetClusterRole(), GetLlmEnabled()));
            }
            workflowQuery = workflowQuery.Where(w => accessible.FolderIds.Contains(w.FolderId));
            execQuery = execQuery.Where(e => accessible.FolderIds.Contains(e.Workflow.FolderId));
        }

        var workflows = await workflowQuery
            .Select(w => new { w.Id, w.Name, w.IsEnabled, w.DefinitionJson, w.CheckedOutByUserId, w.CheckedOutAt })
            .ToListAsync(ct);

        var machines = await _db.ManagedMachines.AsNoTracking()
            .Select(m => new { m.IsReachable })
            .ToListAsync(ct);

        var execsTotal = await execQuery.CountAsync(ct);

        // DB-side GROUP BY (Year/Month/Day/Hour, Status) — single aggregated query
        // returning at most `windowHours` × 5 rows (≤720 for the 30 d view, all small).
        // We keep the DB grouping at hour granularity because EF Core compiles
        // `e.StartedAt.Year` etc. natively on both SqlServer and Postgres; coarser-span
        // bucketing (for >24 h windows) is folded down in C# below to ≤24 display buckets.
        var hourlyAgg = await execQuery
            .Where(e => e.StartedAt >= sinceWindow)
            .GroupBy(e => new
            {
                e.StartedAt.Year,
                e.StartedAt.Month,
                e.StartedAt.Day,
                e.StartedAt.Hour,
            })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                g.Key.Day,
                g.Key.Hour,
                Total = g.Count(),
                Succeeded = g.Count(e => e.Status == ExecutionStatus.Succeeded),
                Failed = g.Count(e => e.Status == ExecutionStatus.Failed),
                Running = g.Count(e => e.Status == ExecutionStatus.Running),
                Cancelled = g.Count(e => e.Status == ExecutionStatus.Cancelled),
            })
            .ToListAsync(ct);

        // Display buckets: ≤24 buckets spanning [sinceWindow, now]. For windows ≤24 h
        // each bucket is one hour; for larger windows each bucket spans
        // windowHours/24 hours so the chart density stays constant regardless of range.
        var bucketCount = Math.Min(windowHours, 24);
        var minutesPerBucket = windowHours * 60 / bucketCount;
        var bucketStart = sinceWindow;
        var buckets = Enumerable.Range(0, bucketCount)
            .Select(i => new HourBucket(bucketStart.AddMinutes(i * minutesPerBucket), 0, 0, 0))
            .ToList();
        // Fold each hourly aggregate row into its containing display bucket. The first
        // aggregate hour can start before sinceWindow; clamp it into bucket 0 so the
        // first partial hour stays visible in the chart.
        foreach (var a in hourlyAgg)
        {
            var rowHour = new DateTime(a.Year, a.Month, a.Day, a.Hour, 0, 0, DateTimeKind.Utc);
            var bucketAnchor = rowHour < bucketStart ? bucketStart : rowHour;
            var idx = (int)Math.Floor((bucketAnchor - bucketStart).TotalMinutes / minutesPerBucket);
            if (idx < 0 || idx >= buckets.Count) continue;
            var b = buckets[idx];
            buckets[idx] = b with { Succeeded = b.Succeeded + a.Succeeded, Failed = b.Failed + a.Failed, Cancelled = b.Cancelled + a.Cancelled };
        }

        // Window totals drive the hero gauge + KPI cluster. Note: `Running` here counts
        // executions whose StartedAt falls inside the window and that are still Running —
        // the same semantics the prior 24 h view had (hour-bucket attribution by start).
        var countsWindow = new ExecutionCounts(
            hourlyAgg.Sum(a => a.Total),
            hourlyAgg.Sum(a => a.Succeeded),
            hourlyAgg.Sum(a => a.Failed),
            hourlyAgg.Sum(a => a.Running),
            hourlyAgg.Sum(a => a.Cancelled));

        var wfStats7d = await execQuery
            .Where(e => e.StartedAt >= since7d)
            .GroupBy(e => e.WorkflowId)
            .Select(g => new
            {
                WorkflowId = g.Key,
                RunCount = g.Count(),
                SuccessCount = g.Count(e => e.Status == ExecutionStatus.Succeeded),
                FailCount = g.Count(e => e.Status == ExecutionStatus.Failed)
            })
            .OrderByDescending(x => x.RunCount)
            .Take(5)
            .ToListAsync(ct);

        var wfNameById = workflows.ToDictionary(w => w.Id, w => w.Name);

        var topIds = wfStats7d.Select(s => s.WorkflowId).ToList();
        var topStatsRows = await _db.WorkflowStats.AsNoTracking()
            .Where(s => topIds.Contains(s.WorkflowId))
            .Select(s => new { s.WorkflowId, s.AvgDurationMsWindow, s.P95DurationMsWindow })
            .ToListAsync(ct);
        var topStatsById = topStatsRows.ToDictionary(s => s.WorkflowId);

        var topWorkflows = wfStats7d.Select(s =>
        {
            topStatsById.TryGetValue(s.WorkflowId, out var st);
            return new TopWorkflow(
                s.WorkflowId,
                wfNameById.GetValueOrDefault(s.WorkflowId, "(deleted)"),
                s.RunCount, s.SuccessCount, s.FailCount,
                st?.AvgDurationMsWindow, st?.P95DurationMsWindow);
        }).ToList();

        var running = await execQuery
            .Where(e => e.Status == ExecutionStatus.Running || e.Status == ExecutionStatus.Pending)
            .OrderByDescending(e => e.StartedAt)
            .Take(10)
            .Select(e => new { e.Id, e.WorkflowId, e.Status, e.StartedAt, e.TriggeredBy })
            .ToListAsync(ct);

        var runningInfos = running.Select(r => new RunningExecutionInfo(
            r.Id, r.WorkflowId,
            wfNameById.GetValueOrDefault(r.WorkflowId, "(deleted)"),
            r.Status.ToString(), r.StartedAt, r.TriggeredBy)).ToList();

        var recent = await execQuery
            .OrderByDescending(e => e.StartedAt)
            .Take(10)
            .Select(e => new { e.Id, e.WorkflowId, e.Status, e.StartedAt, e.CompletedAt, e.TriggeredBy })
            .ToListAsync(ct);

        var recentInfos = recent.Select(e => new RecentExecutionInfo(
            e.Id, e.WorkflowId,
            wfNameById.GetValueOrDefault(e.WorkflowId, "(deleted)"),
            e.Status.ToString(), e.StartedAt, e.CompletedAt,
            e.CompletedAt.HasValue
                ? (long?)(e.CompletedAt.Value - e.StartedAt).TotalMilliseconds
                : null,
            e.TriggeredBy)).ToList();

        // "Armed" = enabled workflow whose definition contains at least one non-manual
        // trigger node. For each armed workflow, derive a NextFireUtc (cron-only) plus
        // a NextFireKind tag so the UI can render "in 5m", "Poll 30s", or "event-driven".
        var armedTriggers = workflows
            .Where(w => w.IsEnabled)
            .Select(w =>
            {
                if (!WorkflowDefinitionDocument.TryParse(w.DefinitionJson, out var definition) || definition is null)
                    return null;

                var externalDescriptors = definition.TriggerDescriptors
                    .Where(t => !t.IsManual)
                    .ToList();
                var nonManual = externalDescriptors
                    .Select(t => t.ActivityType)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(t => t, StringComparer.Ordinal)
                    .ToList();
                if (nonManual.Count == 0) return null;

                DateTime? nextFire = null;
                string? kind = null;
                int? pollInterval = null;

                if (nonManual.Contains("scheduleTrigger"))
                {
                    var nextFires = ExtractScheduleNextFires(externalDescriptors, now);
                    if (nextFires.Count > 0)
                    {
                        nextFire = nextFires.Min();
                        kind = "cron";
                    }
                }
                if (kind is null && nonManual.Contains("databaseTrigger"))
                {
                    pollInterval = ExtractDatabasePollInterval(externalDescriptors);
                    kind = "polling";
                }
                if (kind is null)
                {
                    // Remaining trigger types (fileWatcher, eventLog, webhook) are all event-driven.
                    kind = "event-driven";
                }

                return new ArmedTriggerInfo(w.Id, w.Name, nonManual, nextFire, kind, pollInterval);
            })
            .Where(x => x is not null)
            .Select(x => x!)
            // Sort: cron with nearest NextFire first, then polling, then event-driven.
            .OrderBy(x => x.NextFireKind == "cron" ? 0 : x.NextFireKind == "polling" ? 1 : 2)
            .ThenBy(x => x.NextFireUtc ?? DateTime.MaxValue)
            .ThenBy(x => x.WorkflowName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Failing workflows in the last 7d, ranked by failure count.
        var failing7d = await execQuery
            .Where(e => e.StartedAt >= since7d && e.Status == ExecutionStatus.Failed)
            .GroupBy(e => e.WorkflowId)
            .Select(g => new
            {
                WorkflowId = g.Key,
                FailCount = g.Count(),
                LastFailureAt = (DateTime?)g.Max(e => e.StartedAt)
            })
            .OrderByDescending(x => x.FailCount)
            .Take(5)
            .ToListAsync(ct);

        var failingIds = failing7d.Select(f => f.WorkflowId).ToList();
        var runCountsForFailing = await execQuery
            .Where(e => e.StartedAt >= since7d && failingIds.Contains(e.WorkflowId))
            .GroupBy(e => e.WorkflowId)
            .Select(g => new { WorkflowId = g.Key, RunCount = g.Count() })
            .ToListAsync(ct);
        var runCountByWf = runCountsForFailing.ToDictionary(r => r.WorkflowId, r => r.RunCount);

        // Previous 7d window for trend arrows (day -14 → -7).
        var since14d = now.AddDays(-14);
        var prevFailing = await execQuery
            .Where(e => e.StartedAt >= since14d && e.StartedAt < since7d
                        && e.Status == ExecutionStatus.Failed && failingIds.Contains(e.WorkflowId))
            .GroupBy(e => e.WorkflowId)
            .Select(g => new { WorkflowId = g.Key, FailCount = g.Count() })
            .ToListAsync(ct);
        var prevRuns = await execQuery
            .Where(e => e.StartedAt >= since14d && e.StartedAt < since7d
                        && failingIds.Contains(e.WorkflowId))
            .GroupBy(e => e.WorkflowId)
            .Select(g => new { WorkflowId = g.Key, RunCount = g.Count() })
            .ToListAsync(ct);
        var prevFailByWf = prevFailing.ToDictionary(r => r.WorkflowId, r => r.FailCount);
        var prevRunByWf = prevRuns.ToDictionary(r => r.WorkflowId, r => r.RunCount);

        var failingWorkflows = failing7d.Select(f => new FailingWorkflow(
            f.WorkflowId,
            wfNameById.GetValueOrDefault(f.WorkflowId, "(deleted)"),
            f.FailCount,
            runCountByWf.GetValueOrDefault(f.WorkflowId, f.FailCount),
            f.LastFailureAt,
            prevFailByWf.GetValueOrDefault(f.WorkflowId, 0),
            prevRunByWf.GetValueOrDefault(f.WorkflowId, 0))).ToList();

        // Queue-depth metrics.
        var pendingCount = await execQuery.CountAsync(e => e.Status == ExecutionStatus.Pending, ct);
        var runningCount = await execQuery.CountAsync(e => e.Status == ExecutionStatus.Running, ct);
        var longRunningCount = await execQuery.CountAsync(
            e => e.Status == ExecutionStatus.Running && e.StartedAt < longRunningCutoff, ct);

        // Edit locks — Join Workflow.CheckedOutByUserId → Users.Username.
        var lockedWorkflows = workflows
            .Where(w => w.CheckedOutByUserId != null && w.CheckedOutAt != null)
            .ToList();
        var lockUserIds = lockedWorkflows.Select(w => w.CheckedOutByUserId!.Value).Distinct().ToList();
        var lockUsers = await _db.Users.AsNoTracking()
            .Where(u => lockUserIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Username })
            .ToListAsync(ct);
        var lockUserById = lockUsers.ToDictionary(u => u.Id, u => u.Username);
        var editLocks = lockedWorkflows
            .Select(w => new EditLockInfo(
                w.Id, w.Name,
                lockUserById.GetValueOrDefault(w.CheckedOutByUserId!.Value, "(unknown)"),
                w.CheckedOutAt!.Value))
            .OrderBy(l => l.LockedAt)
            .ToList();

        // Health heartbeats — stale-check is provider-neutral, done in C# after the load.
        var rawHb = await _db.SystemHealth.AsNoTracking()
            .Select(h => new { h.ServiceName, h.LastHeartbeatAt, h.ExpectedIntervalSeconds, h.Status })
            .ToListAsync(ct);
        var heartbeats = rawHb.Select(h => new HealthHeartbeatInfo(
            h.ServiceName, h.LastHeartbeatAt, h.ExpectedIntervalSeconds, h.Status,
            (now - h.LastHeartbeatAt).TotalSeconds > h.ExpectedIntervalSeconds * 3
        )).ToList();

        var dbProvider = NormalizeProvider(_db.Database.ProviderName);
        var clusterRole = GetClusterRole();

        // Recent audit feed — Admin-only, whitelist of high-signal actions.
        List<DashboardAuditEvent>? recentAudit = null;
        if (User.IsInRole("Admin"))
        {
            var auditActions = new[]
            {
                "WORKFLOW_PUBLISHED", "WORKFLOW_FORCE_UNLOCKED", "WORKFLOW_DELETED",
                "USER_ROLE_CHANGED", "USER_BREAK_GLASS_CHANGED", "USER_PASSWORD_RESET", "USER_DEACTIVATED",
                "CREDENTIAL_CREATED", "CREDENTIAL_DELETED",
                "GLOBAL_VARIABLE_CREATED", "GLOBAL_VARIABLE_DELETED",
                "LOGIN_LOCKED"
            };
            var auditRows = await _db.AuditLog.AsNoTracking()
                .Where(a => auditActions.Contains(a.Action))
                .OrderByDescending(a => a.Timestamp)
                .Take(8)
                .Select(a => new { a.Timestamp, a.UserId, a.Action, a.ResourceType, a.ResourceId })
                .ToListAsync(ct);
            var auditUserIds = auditRows.Where(a => a.UserId.HasValue).Select(a => a.UserId!.Value).Distinct().ToList();
            var auditUsers = await _db.Users.AsNoTracking()
                .Where(u => auditUserIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Username })
                .ToListAsync(ct);
            var auditUserById = auditUsers.ToDictionary(u => u.Id, u => u.Username);
            recentAudit = auditRows.Select(a => new DashboardAuditEvent(
                a.Timestamp,
                a.UserId.HasValue ? auditUserById.GetValueOrDefault(a.UserId.Value) : null,
                a.Action, a.ResourceType, a.ResourceId)).ToList();
        }

        var stats = new DashboardStats(
            workflows.Count,
            workflows.Count(w => w.IsEnabled),
            machines.Count,
            machines.Count(m => m.IsReachable),
            execsTotal,
            countsWindow,
            buckets,
            topWorkflows,
            runningInfos,
            recentInfos,
            armedTriggers,
            pendingCount, runningCount, longRunningCount,
            failingWorkflows,
            editLocks,
            heartbeats,
            dbProvider,
            clusterRole,
            recentAudit,
            GetLlmEnabled());

        return Ok(stats);
    }

    /// <summary>
    /// Whether the AI features are enabled (<c>Llm:Enabled</c>). Shown on the system-status
    /// banner as "AI activated" — this only reports whether the switch is on, not whether a
    /// working endpoint is actually configured.
    /// </summary>
    private bool GetLlmEnabled() => _llmOptions?.CurrentValue.Enabled ?? false;

    /// <summary>
    /// Scans a workflow definition for scheduleTrigger nodes, parses their cron expressions
    /// via Quartz and returns the next-fire UTCs. Malformed cron expressions are silently
    /// skipped — the workflow simply appears without a NextFireUtc on the dashboard.
    /// </summary>
    private static List<DateTime> ExtractScheduleNextFires(IEnumerable<WorkflowTriggerDescriptor> descriptors, DateTime nowUtc)
    {
        var result = new List<DateTime>();
        var cursor = new DateTimeOffset(nowUtc, TimeSpan.Zero);
        foreach (var descriptor in descriptors.Where(d => d.ActivityType == "scheduleTrigger"))
        {
            var config = descriptor.Config;
            if (config.ValueKind != JsonValueKind.Object) continue;
            if (!config.TryGetProperty("cronExpression", out var cronProp)) continue;
            var cron = cronProp.GetString();
            if (string.IsNullOrWhiteSpace(cron)) continue;

            try
            {
                var parsed = new CronExpression(cron);
                var next = parsed.GetNextValidTimeAfter(cursor);
                if (next.HasValue) result.Add(next.Value.UtcDateTime);
            }
            catch (FormatException) { /* skip malformed cron */ }
        }

        return result;
    }

    /// <summary>
    /// Returns the smallest <c>intervalSeconds</c> across all enabled databaseTrigger nodes
    /// in the workflow definition. Returns null when no usable interval is found.
    /// </summary>
    private static int? ExtractDatabasePollInterval(IEnumerable<WorkflowTriggerDescriptor> descriptors)
    {
        int? smallest = null;
        foreach (var descriptor in descriptors.Where(d => d.ActivityType == "databaseTrigger"))
        {
            var config = descriptor.Config;
            if (config.ValueKind != JsonValueKind.Object) continue;
            if (!config.TryGetProperty("intervalSeconds", out var intervalProperty)) continue;

            int interval = 0;
            var parsed = (intervalProperty.ValueKind == JsonValueKind.Number && intervalProperty.TryGetInt32(out interval))
                || (intervalProperty.ValueKind == JsonValueKind.String && int.TryParse(intervalProperty.GetString(), out interval));
            if (!parsed || interval <= 0) continue;
            if (smallest is null || interval < smallest.Value) smallest = interval;
        }

        return smallest;
    }

    private static string NormalizeProvider(string? providerName)
    {
        if (string.IsNullOrEmpty(providerName)) return "unknown";
        if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return "postgres";
        if (providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)) return "sqlserver";
        if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)) return "sqlite";
        return providerName;
    }

    private string? GetClusterRole()
    {
        // No-op provider (single-node mode) always reports IsLeader=true; the cluster
        // feature itself is config-gated, so we only surface a role string when an actual
        // cluster lease has been observed (LeaseEpoch > 0 or LeaseExpiresAt is set).
        if (_cluster is null) return null;
        if (_cluster.LeaseEpoch == 0 && _cluster.LeaseExpiresAt is null) return null;
        return _cluster.IsLeader ? "leader" : "standby";
    }

    private static DashboardStats EmptyStats(DateTime sinceWindow, int windowHours, string dbProvider, string? clusterRole, bool llmEnabled)
    {
        var bucketCount = Math.Min(windowHours, 24);
        var minutesPerBucket = windowHours * 60 / bucketCount;
        var bucketStart = sinceWindow;
        return new DashboardStats(
            0, 0, 0, 0, 0,
            new ExecutionCounts(0, 0, 0, 0, 0),
            Enumerable.Range(0, bucketCount).Select(i => new HourBucket(bucketStart.AddMinutes(i * minutesPerBucket), 0, 0, 0)).ToList(),
            [], [], [], [],
            0, 0, 0,
            [], [], [],
            dbProvider, clusterRole, null, llmEnabled);
    }
}
