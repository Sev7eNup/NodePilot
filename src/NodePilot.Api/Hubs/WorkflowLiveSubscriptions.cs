using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NodePilot.Data;
using NodePilot.Api.Services;

namespace NodePilot.Api.Hubs;

/// <summary>Capture under the folder-tree lock before a mutation, then revoke after commit.</summary>
internal sealed record WorkflowLiveSubscriptions(
    IReadOnlyCollection<Guid> WorkflowIds, IReadOnlyCollection<Guid> ExecutionIds)
{
    internal static async Task<WorkflowLiveSubscriptions> CaptureAsync(
        NodePilotDbContext db, IReadOnlyCollection<Guid> workflowIds, CancellationToken ct)
    {
        var watched = ExecutionHub.SubscribedExecutionIds();
        var executionIds = watched.Count == 0
            ? []
            : await db.WorkflowExecutions.AsNoTracking()
                .Where(e => watched.Contains(e.Id) && workflowIds.Contains(e.WorkflowId))
                .Select(e => e.Id).ToListAsync(ct);
        return new(workflowIds, executionIds);
    }

    internal Task RevokeAsync(IHubContext<ExecutionHub> hub, IWorkflowFolderProjection projection,
        DashboardAggregateCache? aggregates = null)
    {
        aggregates?.Clear();
        foreach (var workflowId in WorkflowIds)
            projection.InvalidateWorkflowFolder(workflowId);
        return ExecutionHub.RevokeSubscriptionsAsync(hub, WorkflowIds, ExecutionIds, CancellationToken.None);
    }
}
