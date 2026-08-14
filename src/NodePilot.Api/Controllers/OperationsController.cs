using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Dtos;
using NodePilot.Api.Services;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Operations;
using NodePilot.Data;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Backs the live-ops Mission-Control view. Read-only: nodes + static call topology, the
/// live-running snapshot and recently finished executions for the timeline. Live deltas ride the
/// SignalR <c>ops-feed</c> group (RBAC-filtered there too); cancelling a run reuses
/// <c>POST /api/executions/{id}/cancel</c> — no new mutating endpoint here.
/// </summary>
[ApiController]
[Route("api/operations")]
[Authorize]
public class OperationsController : ControllerBase
{
    private readonly NodePilotDbContext _db;
    private readonly IResourceAuthorizationService _authz;
    private readonly IConfiguration? _configuration;
    private readonly WorkflowCallSiteCache _callSites;

    public OperationsController(NodePilotDbContext db, IResourceAuthorizationService authz,
        IConfiguration? configuration = null, WorkflowCallSiteCache? callSites = null)
    {
        _db = db;
        _authz = authz;
        _configuration = configuration;
        _callSites = callSites ?? new WorkflowCallSiteCache();
    }

    /// <summary>
    /// Overdue threshold for the live timeline. Mirrors <c>LongRunningExecutionCollector</c>
    /// exactly — same key, same default, same <c>Math.Max(1, …)</c> floor — so the console and
    /// the alerting rule cannot disagree about what "long-running" means. Read from raw
    /// <see cref="IConfiguration"/> per request because the Alerting section is hot-reloadable;
    /// an <c>IOptions&lt;T&gt;.Value</c> snapshot would freeze it at boot.
    /// </summary>
    private int OverdueSeconds()
        => Math.Max(1, _configuration?.GetValue("Alerting:LongRunningSeconds", 600) ?? 600);

    /// <summary>
    /// Selectable timeline windows, in minutes. Anything else clamps to <see cref="DefaultWindowMinutes"/>.
    /// Mirrored by <c>OPS_WINDOW_MINUTES</c> in the SPA's lib/opsTimeline.ts and named in the CLI and
    /// MCP option help — changing this list means changing all four.
    /// </summary>
    private static readonly int[] AllowedWindowMinutes = [30, 60];

    /// <summary>Window a caller gets when it names none, or names one that is not selectable.</summary>
    internal const int DefaultWindowMinutes = 30;

    /// <summary>
    /// Cap on returned settled runs. This is a RENDER budget, not a window budget — the same
    /// number at every window, because what it bounds is how many bars the console holds, and that
    /// does not care how far back the caller looked. Coverage beyond it is not this cap's job:
    /// whatever it cannot reach comes back aggregated in <see cref="OpsDensityLane"/>, so widening
    /// the window can no longer punch a hole that reads as "nothing ran".
    ///
    /// The number is the point where bars stop being individually readable, not an arbitrary
    /// round figure. A bar needs roughly 8 px of lane to be countable; a ~1500 px track therefore
    /// holds ~185 per lane, and a busy board runs ~24 lanes — about 4400 bars before the view is
    /// lying either way.
    ///
    /// Sitting just under that is what makes the widest offered window whole: on a measured 2980
    /// finished-runs-per-hour system the 1 h view fits inside the cap with headroom, where the
    /// previous 1000 covered only its newest ~20 minutes and handed the rest to the aggregate. That
    /// is the entire reason this number moved — lowering it back re-creates the original complaint
    /// on the first busy hour.
    ///
    /// Affordable only because settled bars no longer animate — see .np-ops-bar in index.css. The
    /// per-tick cost that the old cap was really guarding was a `left`/`width` CSS transition on
    /// every bar, which is a layout animation, not a compositor one.
    /// </summary>
    internal const int RecentCap = 4000;

    /// <summary>
    /// Bucket count the density aggregate aims for at any window — the reason widening the window
    /// costs nothing extra. At 30 min a bucket is 37 s, at 1 h it is 75 s; either way the console
    /// receives at most <c>lanes × 48</c> cells no matter how many runs are behind them.
    /// </summary>
    private const int DensityBucketTarget = 48;

    /// <summary>
    /// Row ceiling for the density scan. The aggregate is computed in memory rather than in SQL
    /// deliberately: portable date-part bucketing across Postgres, SQL Server AND the SQLite test
    /// backend is a translation minefield, and 20 000 narrow rows is a cheap indexed range read.
    /// At the 1 h window this is ~333 finished runs per minute sustained — well past the busiest
    /// real load — and if it is ever hit, <c>Meta.DensityCapped</c> says so instead of quietly
    /// under-counting.
    /// </summary>
    private const int DensityScanCap = 20_000;

    /// <summary>
    /// How many running executions get step-activity enrichment per snapshot. The oldest win —
    /// they are the candidates for being stuck. Runs beyond the cap ship NULL activity fields
    /// (not zero: "unknown" and "nothing happened" are different claims).
    /// </summary>
    private const int ProgressEnrichmentCap = 300;

    /// <summary>Observed step activity of one live run. See <see cref="OpsRunningExecution"/>.</summary>
    private readonly record struct StepActivity(
        int Finished, int Active, DateTime? LastProgressAt, string? LastStepName);

    [HttpGet("graph")]
    public async Task<ActionResult<OperationsGraphDto>> GetGraph(
        CancellationToken ct,
        [FromQuery] int windowMinutes = DefaultWindowMinutes)
    {
        if (!AllowedWindowMinutes.Contains(windowMinutes)) windowMinutes = DefaultWindowMinutes;
        var recentSince = DateTime.UtcNow.AddMinutes(-windowMinutes);
        var emptyMeta = new OpsSnapshotMeta(OverdueSeconds(), windowMinutes, recentSince, null, false, 0, false);

        // RBAC: resolve the accessible-folder set once and scope every query to it. Global Admin
        // is unrestricted and skips the filter; a user with zero folder access gets an empty graph.
        var accessible = await _authz.GetAccessibleFolderIdsAsync(User, ct);
        var workflowQuery = _db.Workflows.AsNoTracking().ScopeToAccessibleFolders(accessible);
        var execQuery = _db.WorkflowExecutions.AsNoTracking().ScopeToAccessibleFolders(accessible);
        if (workflowQuery is null || execQuery is null)
            return Ok(new OperationsGraphDto([], [], [], [], [], emptyMeta));

        // Deliberately WITHOUT DefinitionJson. Definitions are unbounded text including every
        // inline script (21-42 KB apiece in the repo's example set) and the only thing this endpoint
        // wants from them is the child-workflow call graph, which changes when a workflow is saved
        // and not when somebody polls. UpdatedAt rides along as the revision marker that lets
        // WorkflowCallSiteCache decide whose definition actually has to be read — steady state is
        // none of them. See the cache's remarks for the measured cost this removes.
        var workflows = await workflowQuery
            .Select(w => new { w.Id, w.Name, w.FolderId, w.IsEnabled, w.UpdatedAt })
            .ToListAsync(ct);

        // Request-local and authoritative for THIS response. Deliberately not a second read of the
        // shared cache after storing: two polls racing across a save could otherwise interleave into
        // a mixed answer, and an eviction landing mid-request would silently drop the edges of
        // workflows this very request had already extracted.
        var callSitesByWorkflow = new Dictionary<Guid, IReadOnlyList<WorkflowCallSite>>(workflows.Count);

        var staleIds = _callSites.StaleIds(workflows.Select(w => (w.Id, w.UpdatedAt)));
        if (staleIds.Count > 0)
        {
            var definitions = await workflowQuery
                .Where(w => staleIds.Contains(w.Id))
                .Select(w => new { w.Id, w.UpdatedAt, w.DefinitionJson })
                .ToListAsync(ct);
            foreach (var d in definitions)
            {
                var sites = WorkflowCallGraphBuilder.ExtractCallSites(d.DefinitionJson);
                _callSites.Store(d.Id, d.UpdatedAt, sites);
                callSitesByWorkflow[d.Id] = sites;
            }
        }

        foreach (var wf in workflows)
        {
            if (!callSitesByWorkflow.ContainsKey(wf.Id))
                callSitesByWorkflow[wf.Id] = _callSites.Get(wf.Id);
        }

        var folderIds = workflows.Select(w => w.FolderId).Distinct().ToList();
        var folderPaths = await _db.SharedWorkflowFolders.AsNoTracking()
            .Where(f => folderIds.Contains(f.Id))
            .Select(f => new { f.Id, f.Path })
            .ToDictionaryAsync(f => f.Id, f => f.Path, ct);

        // Per-folder capabilities, resolved once per DISTINCT folder and reused for every node
        // in it. GetWorkflowCapabilitiesAsync short-circuits for global Admin (zero queries) and
        // is per-request cached by (folderId, userId), so this costs at most one lookup per
        // folder even on a large snapshot.
        //
        // Deliberately per-node rather than one snapshot-wide flag: cancel/retry need
        // ResourceOp.Run, disable needs ResourceOp.Edit, and both are folder-scoped. A global
        // Operator holding only folder-Viewer rights must see disabled buttons here rather than
        // click one and collect a 403 from the endpoint.
        var capsByFolder = new Dictionary<Guid, ResourceCapabilities>(folderIds.Count);
        foreach (var folderId in folderIds)
            capsByFolder[folderId] = await _authz.GetWorkflowCapabilitiesAsync(User, folderId, ct);

        var runningRows = await execQuery
            .Where(e => e.Status == ExecutionStatus.Running || e.Status == ExecutionStatus.Pending)
            .OrderByDescending(e => e.StartedAt)
            .Select(e => new { e.Id, e.WorkflowId, e.Status, e.StartedAt, e.ParentExecutionId })
            .ToListAsync(ct);

        var runningCountByWf = runningRows
            .GroupBy(r => r.WorkflowId)
            .ToDictionary(g => g.Key, g => g.Count());

        // Step activity for the live bars. Bounded three ways: skipped entirely on an idle system
        // (the common case), capped at the OLDEST N running runs (those are the ones an operator
        // cares about), and driven off the (WorkflowExecutionId, StartedAt) index — never an
        // unbounded StepExecutions scan. Reads only; the documented-rejected "batch-write step
        // rows" idea stays rejected.
        var activityByExec = new Dictionary<Guid, StepActivity>();
        if (runningRows.Count > 0)
        {
            var enrichIds = runningRows
                .OrderBy(r => r.StartedAt)
                .Take(ProgressEnrichmentCap)
                .Select(r => r.Id)
                .ToList();

            var agg = await _db.StepExecutions.AsNoTracking()
                .Where(s => enrichIds.Contains(s.WorkflowExecutionId))
                .GroupBy(s => s.WorkflowExecutionId)
                .Select(g => new
                {
                    ExecId = g.Key,
                    Started = g.Count(),
                    Finished = g.Count(s => s.Status != ExecutionStatus.Running
                                         && s.Status != ExecutionStatus.Pending
                                         && s.Status != ExecutionStatus.Paused),
                    // Progress means work that actually RAN. A Skipped row is a control-flow
                    // branch that never executed; letting it set "last progress" would reset
                    // the stagnation clock without anything having happened.
                    LastProgressAt = g
                        .Where(s => s.Status != ExecutionStatus.Skipped)
                        .Max(s => s.CompletedAt),
                })
                .ToListAsync(ct);

            // Name of the step behind each LastProgressAt. Separate narrow query rather than a
            // correlated sub-select so it stays provider-portable (SQLite test backend included).
            var lastNames = await _db.StepExecutions.AsNoTracking()
                .Where(s => enrichIds.Contains(s.WorkflowExecutionId)
                         && s.CompletedAt != null
                         && s.Status != ExecutionStatus.Skipped)
                .Select(s => new { s.WorkflowExecutionId, s.StepId, s.StepName, s.CompletedAt })
                .ToListAsync(ct);
            var lastNameByExec = lastNames
                .GroupBy(s => s.WorkflowExecutionId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(s => s.CompletedAt).ThenByDescending(s => s.StepId).First());

            foreach (var a in agg)
            {
                lastNameByExec.TryGetValue(a.ExecId, out var last);
                activityByExec[a.ExecId] = new StepActivity(
                    Finished: a.Finished,
                    // Under DeferRunningStateWrite this is 0 in practice (no row exists while a
                    // step runs); it becomes meaningful only on the two-phase write path.
                    Active: Math.Max(0, a.Started - a.Finished),
                    LastProgressAt: a.LastProgressAt,
                    LastStepName: last is null ? null : (last.StepName ?? last.StepId));
            }
        }

        // Settled runs inside the requested window. Fetch one over the cap so truncation is a
        // fact we can report rather than a silent trim. `running` is deliberately NOT windowed —
        // a job that started six hours ago must stay on the timeline at every window setting;
        // that is precisely the stuck-run case the view exists for.
        var settledQuery = execQuery
            .Where(e => e.CompletedAt != null && e.CompletedAt >= recentSince
                     && e.Status != ExecutionStatus.Running
                     && e.Status != ExecutionStatus.Pending
                     && e.Status != ExecutionStatus.Paused);

        // ThenByDescending(Id) is part of the contract, not a nicety: without a tiebreaker, which row
        // is the 4000th at a shared CompletedAt is up to the provider, so the cap boundary and with it
        // OldestReturnedCompletedAt -- the seam the console draws density up to -- could differ between
        // two polls of unchanged data and make bars flicker in and out. The (CompletedAt, Id) index
        // serves this sort directly.
        var recentFetched = await settledQuery
            .OrderByDescending(e => e.CompletedAt)
            .ThenByDescending(e => e.Id)
            .Take(RecentCap + 1)
            .Select(e => new { e.Id, e.WorkflowId, e.Status, e.StartedAt, e.CompletedAt, e.ParentExecutionId })
            .ToListAsync(ct);

        var recentTruncated = recentFetched.Count > RecentCap;
        var recentRows = recentTruncated ? recentFetched.Take(RecentCap).ToList() : recentFetched;
        // Ordered by CompletedAt desc, so the last row is the oldest one we actually returned.
        var oldestReturned = recentRows.Count > 0 ? recentRows[^1].CompletedAt : null;

        // Density only when the bars could not cover the window. On a quiet system this whole
        // block is skipped: no second query, no extra payload, timeline unchanged.
        var density = Array.Empty<OpsDensityLane>() as IReadOnlyList<OpsDensityLane>;
        var densityBucketSeconds = 0;
        var densityCapped = false;
        if (recentTruncated)
        {
            densityBucketSeconds = Math.Max(1, windowMinutes * 60 / DensityBucketTarget);
            var scan = await settledQuery
                .OrderByDescending(e => e.CompletedAt)
                .ThenByDescending(e => e.Id)
                .Take(DensityScanCap + 1)
                .Select(e => new { e.WorkflowId, e.CompletedAt, e.Status })
                .ToListAsync(ct);
            densityCapped = scan.Count > DensityScanCap;
            density = Bucketize(
                scan.Take(DensityScanCap).Select(r => (r.WorkflowId, r.CompletedAt!.Value, r.Status)),
                recentSince, densityBucketSeconds);
        }

        var wfIds = workflows.Select(w => w.Id).ToList();
        var statsRows = await _db.WorkflowStats.AsNoTracking()
            .Where(s => wfIds.Contains(s.WorkflowId))
            .Select(s => new
            {
                s.WorkflowId,
                s.SucceededWindow,
                s.FailedWindow,
                s.CancelledWindow,
                s.LastExecutionAt,
                s.LastSuccessAt,
                s.LastFailureAt,
            })
            .ToListAsync(ct);
        var statsById = statsRows.ToDictionary(s => s.WorkflowId);

        var nodes = workflows.Select(w =>
        {
            statsById.TryGetValue(w.Id, out var st);
            int? callFrequency = st is null ? null : st.SucceededWindow + st.FailedWindow + st.CancelledWindow;
            string? lastStatus = st is null
                ? null
                : DeriveLastStatus(st.LastExecutionAt, st.LastSuccessAt, st.LastFailureAt);
            var caps = capsByFolder.GetValueOrDefault(w.FolderId, ResourceCapabilities.None);
            return new OpsNode(
                w.Id, w.Name, w.FolderId,
                folderPaths.GetValueOrDefault(w.FolderId, "/"),
                w.IsEnabled,
                runningCountByWf.GetValueOrDefault(w.Id, 0),
                lastStatus,
                callFrequency,
                caps.CanRun,
                caps.CanEdit);
        }).ToList();

        // Resolution stays per-request even though extraction is cached: a name-based reference
        // resolves against every OTHER workflow's name, so a rename changes this workflow's edges
        // without touching its definition. Caching resolved edges would go stale on exactly that.
        var callEdges = WorkflowCallGraphBuilder.BuildFromCallSites(
            workflows.Select(w => new WorkflowCallGraphIdentity(w.Id, w.Name)).ToList(),
            callSitesByWorkflow);

        var edges = callEdges.Select(e => new OpsEdge(
            Id: $"{e.SourceWorkflowId:N}|{e.Kind}|{(e.TargetWorkflowId?.ToString("N") ?? e.RawRef)}",
            Source: e.SourceWorkflowId,
            Target: e.TargetWorkflowId,
            Kind: e.Kind,
            RefStatus: e.RefStatus.ToString(),
            RawRef: e.RawRef,
            CallCount: e.CallCount)).ToList();

        var running = runningRows
            .Select(r =>
            {
                // Not-enriched → all-null, deliberately. Zero would read as "this run has done
                // nothing", which is a different (and wrong) claim from "we did not look".
                var has = activityByExec.TryGetValue(r.Id, out var a);
                return new OpsRunningExecution(
                    r.Id, r.WorkflowId, r.Status.ToString(), r.StartedAt, r.ParentExecutionId,
                    StepsFinished: has ? a.Finished : null,
                    LastCompletedStepName: has ? a.LastStepName : null,
                    LastProgressAt: has ? a.LastProgressAt : null,
                    ActiveStepCount: has ? a.Active : null);
            })
            .ToList();

        var recent = recentRows
            .Select(r => new OpsRecentExecution(r.Id, r.WorkflowId, r.Status.ToString(), r.StartedAt, r.CompletedAt!.Value, r.ParentExecutionId))
            .ToList();

        var meta = new OpsSnapshotMeta(
            OverdueSeconds(), windowMinutes, recentSince, oldestReturned, recentTruncated,
            densityBucketSeconds, densityCapped);

        return Ok(new OperationsGraphDto(nodes, edges, running, recent, density, meta));
    }

    /// <summary>
    /// Groups settled runs into fixed-width time slices per workflow.
    /// <para>
    /// Slices span the WHOLE window, not just the stretch the raw list missed. Two reasons: the
    /// boundary between "is a bar" and "is a bucket" falls mid-slice, so a bucket cut at that
    /// boundary would under-count itself; and covering everything makes the bucket sums the honest
    /// total for the window, which is what the console puts in its notice line. The console draws
    /// only the slices left of the seam — double-counting is a rendering concern, not a data one.
    /// </para>
    /// </summary>
    private static IReadOnlyList<OpsDensityLane> Bucketize(
        IEnumerable<(Guid WorkflowId, DateTime CompletedAt, ExecutionStatus Status)> rows,
        DateTime recentSince,
        int bucketSeconds)
    {
        var counts = new Dictionary<(Guid WorkflowId, int Bucket), (int Total, int Failed, int Cancelled)>();
        foreach (var row in rows)
        {
            // Clamped at 0: a run whose CompletedAt sits a hair before recentSince (clock skew
            // between the filter's timestamp and the row) belongs in the first slice, not in a
            // negative one that would sort ahead of the window.
            var offset = (int)Math.Max(0, (row.CompletedAt - recentSince).TotalSeconds / bucketSeconds);
            var key = (row.WorkflowId, offset);
            counts.TryGetValue(key, out var c);
            counts[key] = (
                c.Total + 1,
                c.Failed + (row.Status == ExecutionStatus.Failed ? 1 : 0),
                c.Cancelled + (row.Status == ExecutionStatus.Cancelled ? 1 : 0));
        }

        return counts
            .GroupBy(kv => kv.Key.WorkflowId)
            .Select(g => new OpsDensityLane(
                g.Key,
                g.OrderBy(kv => kv.Key.Bucket)
                 .Select(kv => new OpsDensityBucket(kv.Key.Bucket, kv.Value.Total, kv.Value.Failed, kv.Value.Cancelled))
                 .ToList()))
            .ToList();
    }

    /// <summary>
    /// Picks the status of the most recent execution from the pre-aggregated WorkflowStats
    /// timestamps. WorkflowStats has no explicit "last status" column, so we infer it from which
    /// of LastSuccess/LastFailure equals LastExecution. A run that was neither (Cancelled) falls
    /// through to "Cancelled".
    /// </summary>
    private static string? DeriveLastStatus(DateTime? lastExecution, DateTime? lastSuccess, DateTime? lastFailure)
    {
        if (lastExecution is null)
            return null;
        if (lastFailure == lastExecution)
            return nameof(ExecutionStatus.Failed);
        if (lastSuccess == lastExecution)
            return nameof(ExecutionStatus.Succeeded);
        return nameof(ExecutionStatus.Cancelled);
    }
}
