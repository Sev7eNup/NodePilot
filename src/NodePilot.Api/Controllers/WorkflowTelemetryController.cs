using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Data;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Read-only per-step telemetry for the designer: step-health sparklines, the coverage
/// heatmap, and duration/failure stats. Fourth member of the <c>api/workflows</c> controller
/// family — extracted from <c>ExecutionsController</c> (2026-07 coherence audit), which had
/// grown into a lifecycle+analytics mix; the HTTP surface is unchanged.
///
/// <para>All endpoints are folder-RBAC-gated reads (<see cref="ResourceOp.Read"/>); nothing
/// here mutates, so there is no audit surface.</para>
/// </summary>
[ApiController]
[Route("api/workflows")]
[Authorize]
public class WorkflowTelemetryController : ControllerBase
{
    private readonly NodePilotDbContext _db;
    private readonly IResourceAuthorizationService _authz;

    public WorkflowTelemetryController(NodePilotDbContext db, IResourceAuthorizationService authz)
    {
        _db = db;
        _authz = authz;
    }

    /// <summary>Returns recent execution outcomes per step for sparkline display in the designer.</summary>
    [HttpGet("{workflowId:guid}/step-health")]
    public async Task<ActionResult<Dictionary<string, List<StepHealthEntry>>>> GetStepHealth(
        Guid workflowId,
        [FromQuery] string? stepIds,
        [FromQuery] int limit = 8,
        CancellationToken ct = default)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflowId, ct);
        if (workflow is null) return NotFound();
        if (await this.RequireWorkflowAccessAsync(_authz, workflow, ResourceOp.Read, ct) is { } d) return d;

        limit = Math.Clamp(limit, 1, 20);
        var requestedIds = stepIds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];

        var recentExecIds = await _db.WorkflowExecutions
            .Where(e => e.WorkflowId == workflowId)
            .OrderByDescending(e => e.StartedAt)
            .Take(20)
            .Select(e => e.Id)
            .ToListAsync(ct);

        if (recentExecIds.Count == 0)
            return Ok(new Dictionary<string, List<StepHealthEntry>>());

        var query = _db.StepExecutions
            .Where(s => recentExecIds.Contains(s.WorkflowExecutionId));

        if (requestedIds.Length > 0)
            query = query.Where(s => requestedIds.Contains(s.StepId));

        var raw = await query
            .Select(s => new { s.StepId, Status = s.Status.ToString(), s.StartedAt })
            .ToListAsync(ct);

        var result = raw
            .GroupBy(s => s.StepId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(s => s.StartedAt)
                      .Take(limit)
                      .Select(s => new StepHealthEntry(s.Status, s.StartedAt))
                      .ToList());

        return Ok(result);
    }

    /// <summary>
    /// Coverage stats per step over the last <paramref name="windowDays"/>. Counts how often
    /// each step actually ran vs. was skipped vs. failed, plus the most-recent timestamps for
    /// each outcome. Skipped is split out from Failed because "this branch never ran in
    /// production" is operationally distinct from "this branch ran and broke" — both are
    /// useful, but the follow-up actions differ.
    /// </summary>
    [HttpGet("{workflowId:guid}/coverage")]
    public async Task<ActionResult<WorkflowCoverageResponse>> GetCoverage(
        Guid workflowId,
        [FromQuery] int windowDays = 30,
        CancellationToken ct = default)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflowId, ct);
        if (workflow is null) return NotFound();
        if (await this.RequireWorkflowAccessAsync(_authz, workflow, ResourceOp.Read, ct) is { } d) return d;

        windowDays = Math.Clamp(windowDays, 1, 365);
        var cutoff = DateTime.UtcNow.AddDays(-windowDays);

        // Mirror step-stats' execution cap (900 most-recent runs in window) so a workflow
        // with millions of fires doesn't melt the DB on a coverage request. Rare in
        // practice for manual workflows; trigger-driven workflows can hit it on tight
        // schedules. The cap is silently applied — the response carries OldestExecutionInWindow
        // so the UI can display "covering N executions back to <date>".
        var executions = await _db.WorkflowExecutions
            .Where(e => e.WorkflowId == workflowId && e.StartedAt >= cutoff)
            .OrderByDescending(e => e.StartedAt)
            .Take(900)
            .Select(e => new { e.Id, e.StartedAt })
            .ToListAsync(ct);

        if (executions.Count == 0)
            return Ok(new WorkflowCoverageResponse(workflowId, windowDays, 0, null, Array.Empty<NodeCoverageStats>()));

        var execIds = executions.Select(e => e.Id).ToList();
        var oldest = executions.Min(e => e.StartedAt);

        // GROUP BY step + status — DB-side aggregation. Then post-process in memory to
        // pivot into per-step stats with named buckets. Both providers map this cleanly.
        var grouped = await _db.StepExecutions
            .Where(s => execIds.Contains(s.WorkflowExecutionId))
            .GroupBy(s => new { s.StepId, s.Status })
            .Select(g => new
            {
                g.Key.StepId,
                g.Key.Status,
                Count = g.Count(),
                MaxStarted = g.Max(s => s.StartedAt),
            })
            .ToListAsync(ct);

        var byStep = new Dictionary<string, NodeCoverageStats>(StringComparer.Ordinal);
        foreach (var row in grouped)
        {
            byStep.TryGetValue(row.StepId, out var existing);
            int executed = existing?.ExecutedCount ?? 0;
            int failed = existing?.FailedCount ?? 0;
            int skipped = existing?.SkippedCount ?? 0;
            DateTime? lastExecuted = existing?.LastExecutedAt;
            DateTime? lastSucceeded = existing?.LastSucceededAt;
            DateTime? lastFailed = existing?.LastFailedAt;

            // Treat both Succeeded and Failed as "this step actually ran" — coverage answers
            // "did the engine reach this node?", not "did it succeed". Cancelled is folded
            // into Skipped because cancellation typically means a junction race / kill switch
            // — the step did not produce a result.
            switch (row.Status)
            {
                case ExecutionStatus.Succeeded:
                    executed += row.Count;
                    if (row.MaxStarted is { } sStart && (lastSucceeded is null || sStart > lastSucceeded))
                        lastSucceeded = sStart;
                    if (row.MaxStarted is { } sExec && (lastExecuted is null || sExec > lastExecuted))
                        lastExecuted = sExec;
                    break;
                case ExecutionStatus.Failed:
                    executed += row.Count;
                    failed += row.Count;
                    if (row.MaxStarted is { } fStart && (lastFailed is null || fStart > lastFailed))
                        lastFailed = fStart;
                    if (row.MaxStarted is { } fExec && (lastExecuted is null || fExec > lastExecuted))
                        lastExecuted = fExec;
                    break;
                case ExecutionStatus.Skipped:
                case ExecutionStatus.Cancelled:
                    skipped += row.Count;
                    break;
            }

            byStep[row.StepId] = new NodeCoverageStats(
                row.StepId, executed, failed, skipped, lastExecuted, lastSucceeded, lastFailed);
        }

        return Ok(new WorkflowCoverageResponse(
            workflowId, windowDays, executions.Count, oldest, byStep.Values.ToList()));
    }

    /// <summary>
    /// Aggregated per-step stats for the last <paramref name="windowDays"/>: avg/p95/last
    /// duration + failure rate. Used by the designer for performance annotations on hover
    /// and the failure heatmap overlay.
    /// </summary>
    [HttpGet("{workflowId:guid}/step-stats")]
    public async Task<ActionResult<Dictionary<string, StepStats>>> GetStepStats(
        Guid workflowId,
        [FromQuery] int windowDays = 30,
        CancellationToken ct = default)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflowId, ct);
        if (workflow is null) return NotFound();
        if (await this.RequireWorkflowAccessAsync(_authz, workflow, ResourceOp.Read, ct) is { } d) return d;

        windowDays = Math.Clamp(windowDays, 1, 365);
        var cutoff = DateTime.UtcNow.AddDays(-windowDays);

        var execIds = await _db.WorkflowExecutions
            .Where(e => e.WorkflowId == workflowId && e.StartedAt >= cutoff)
            .OrderByDescending(e => e.StartedAt)
            .Take(900)
            .Select(e => e.Id)
            .ToListAsync(ct);

        if (execIds.Count == 0)
            return Ok(new Dictionary<string, StepStats>());

        // Query 1: DB-side GROUP BY for total + failed counts. This is provider-portable —
        // EF Core translates `g.Count()` and the conditional `Count(predicate)` on both
        // supported providers (SQL Server, Postgres) and on the SQLite test backend.
        // Replaces the previous approach which materialised every step row only to
        // count + group in C#.
        var counts = await _db.StepExecutions
            .Where(s => execIds.Contains(s.WorkflowExecutionId)
                     && s.StartedAt != null && s.CompletedAt != null)
            .GroupBy(s => s.StepId)
            .Select(g => new
            {
                StepId = g.Key,
                TotalRuns = g.Count(),
                FailedRuns = g.Count(s => s.Status == ExecutionStatus.Failed),
            })
            .ToListAsync(ct);

        if (counts.Count == 0)
            return Ok(new Dictionary<string, StepStats>());

        // Query 2: a narrow (StepId, StartedAt, CompletedAt) projection. The duration
        // subtraction runs here in C# because DateTime subtraction can't be translated to
        // SQL on SQLite — on Postgres / SQL Server `g.Average((c-s).TotalMilliseconds)` could
        // run directly inside the GROUP BY, but we stay provider-agnostic. The narrow
        // projection shrinks the per-row size from ~1 KB down to ~24 bytes, so the 30-day
        // window stays manageable even for high-volume workflows. Sorting in SQL guarantees
        // that the first tuple per StepId is the "most recent" duration.
        var perStepRows = await _db.StepExecutions
            .Where(s => execIds.Contains(s.WorkflowExecutionId)
                     && s.StartedAt != null && s.CompletedAt != null)
            .OrderByDescending(s => s.StartedAt)
            .Select(s => new { s.StepId, s.StartedAt, s.CompletedAt })
            .ToListAsync(ct);

        var perStepStats = new Dictionary<string, (List<double> sorted, double last, double sum)>();
        foreach (var row in perStepRows)
        {
            var ms = (row.CompletedAt!.Value - row.StartedAt!.Value).TotalMilliseconds;
            if (!perStepStats.TryGetValue(row.StepId, out var entry))
            {
                // First (= newest, due to DESC sort) row for this StepId — capture as Last.
                entry = (new List<double>(), ms, 0);
                perStepStats[row.StepId] = entry;
            }
            if (ms >= 0)
            {
                entry.sorted.Add(ms);
                entry.sum += ms;
                perStepStats[row.StepId] = entry;
            }
        }
        // Sort once per step — cheaper than maintaining a sorted set incrementally.
        foreach (var key in perStepStats.Keys.ToList())
        {
            var entry = perStepStats[key];
            entry.sorted.Sort();
            perStepStats[key] = entry;
        }

        var result = counts.ToDictionary(c => c.StepId, c =>
        {
            perStepStats.TryGetValue(c.StepId, out var stepStats);
            var sorted = stepStats.sorted;
            var avg = sorted is { Count: > 0 } ? stepStats.sum / sorted.Count : 0;
            var p95 = sorted is { Count: > 0 }
                ? sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(sorted.Count * 0.95))]
                : 0;
            return new StepStats(
                TotalRuns: c.TotalRuns,
                FailedRuns: c.FailedRuns,
                FailureRate: c.TotalRuns > 0 ? (double)c.FailedRuns / c.TotalRuns : 0,
                AvgDurationMs: (long)avg,
                P95DurationMs: (long)p95,
                LastDurationMs: (long)stepStats.last);
        });

        return Ok(result);
    }
}
