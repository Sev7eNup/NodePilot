using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Dtos;
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

    public OperationsController(NodePilotDbContext db, IResourceAuthorizationService authz)
    {
        _db = db;
        _authz = authz;
    }

    [HttpGet("graph")]
    public async Task<ActionResult<OperationsGraphDto>> GetGraph(CancellationToken ct)
    {
        var capabilities = new OpsCapabilities(CanCancel: User.IsInRole("Admin") || User.IsInRole("Operator"));

        // RBAC: resolve the accessible-folder set once and scope every query to it. Global Admin
        // is unrestricted and skips the filter; a user with zero folder access gets an empty graph.
        var accessible = await _authz.GetAccessibleFolderIdsAsync(User, ct);
        var workflowQuery = _db.Workflows.AsNoTracking().AsQueryable();
        var execQuery = _db.WorkflowExecutions.AsNoTracking().AsQueryable();
        if (!accessible.IsUnrestricted)
        {
            if (accessible.FolderIds.Count == 0)
                return Ok(new OperationsGraphDto([], [], [], [], capabilities));
            workflowQuery = workflowQuery.Where(w => accessible.FolderIds.Contains(w.FolderId));
            execQuery = execQuery.Where(e => accessible.FolderIds.Contains(e.Workflow.FolderId));
        }

        var workflows = await workflowQuery
            .Select(w => new { w.Id, w.Name, w.FolderId, w.IsEnabled, w.DefinitionJson })
            .ToListAsync(ct);

        var folderIds = workflows.Select(w => w.FolderId).Distinct().ToList();
        var folderPaths = await _db.SharedWorkflowFolders.AsNoTracking()
            .Where(f => folderIds.Contains(f.Id))
            .Select(f => new { f.Id, f.Path })
            .ToDictionaryAsync(f => f.Id, f => f.Path, ct);

        var runningRows = await execQuery
            .Where(e => e.Status == ExecutionStatus.Running || e.Status == ExecutionStatus.Pending)
            .OrderByDescending(e => e.StartedAt)
            .Select(e => new { e.Id, e.WorkflowId, e.Status, e.StartedAt, e.ParentExecutionId })
            .ToListAsync(ct);

        var runningCountByWf = runningRows
            .GroupBy(r => r.WorkflowId)
            .ToDictionary(g => g.Key, g => g.Count());

        var recentSince = DateTime.UtcNow.AddMinutes(-30);
        var recentRows = await execQuery
            .Where(e => e.CompletedAt != null && e.CompletedAt >= recentSince
                     && e.Status != ExecutionStatus.Running
                     && e.Status != ExecutionStatus.Pending
                     && e.Status != ExecutionStatus.Paused)
            .OrderByDescending(e => e.CompletedAt)
            .Take(200)
            .Select(e => new { e.Id, e.WorkflowId, e.Status, e.StartedAt, e.CompletedAt, e.ParentExecutionId })
            .ToListAsync(ct);

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
            return new OpsNode(
                w.Id, w.Name, w.FolderId,
                folderPaths.GetValueOrDefault(w.FolderId, "/"),
                w.IsEnabled,
                runningCountByWf.GetValueOrDefault(w.Id, 0),
                lastStatus,
                callFrequency);
        }).ToList();

        var callEdges = WorkflowCallGraphBuilder.Build(
            workflows.Select(w => new WorkflowCallGraphInput(w.Id, w.Name, w.DefinitionJson)).ToList());

        var edges = callEdges.Select(e => new OpsEdge(
            Id: $"{e.SourceWorkflowId:N}|{e.Kind}|{(e.TargetWorkflowId?.ToString("N") ?? e.RawRef)}",
            Source: e.SourceWorkflowId,
            Target: e.TargetWorkflowId,
            Kind: e.Kind,
            RefStatus: e.RefStatus.ToString(),
            RawRef: e.RawRef,
            CallCount: e.CallCount)).ToList();

        var running = runningRows
            .Select(r => new OpsRunningExecution(r.Id, r.WorkflowId, r.Status.ToString(), r.StartedAt, r.ParentExecutionId))
            .ToList();

        var recent = recentRows
            .Select(r => new OpsRecentExecution(r.Id, r.WorkflowId, r.Status.ToString(), r.StartedAt, r.CompletedAt!.Value, r.ParentExecutionId))
            .ToList();

        return Ok(new OperationsGraphDto(nodes, edges, running, recent, capabilities));
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
