using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;

namespace NodePilot.Api.Services;

internal static class DashboardRetryStats
{
    /// <summary>Both counters share one database snapshot and one row per finished execution.</summary>
    internal static IQueryable<ExecutionRetryStats> BuildQuery(
        IQueryable<WorkflowExecution> accessibleExecutions, IQueryable<StepExecution> steps,
        DateTime since, DateTime now)
    {
        var finished = accessibleExecutions.Where(e => e.StartedAt >= since && e.StartedAt <= now
            && (e.Status == ExecutionStatus.Succeeded || e.Status == ExecutionStatus.Failed || e.Status == ExecutionStatus.Cancelled));
        // Limit the step lookup to the same authorized window. Deduplicate before joining:
        // several retried activities must still contribute exactly one execution.
        var retriedIds = steps
            .Where(s => s.AttemptCount > 1 && finished.Select(e => e.Id).Contains(s.WorkflowExecutionId))
            .Select(s => (Guid?)s.WorkflowExecutionId)
            .Distinct();
        var rows = from execution in finished
                   join retryId in retriedIds on (Guid?)execution.Id equals retryId into matches
                   from retryId in matches.DefaultIfEmpty()
                   select new { HasRetry = retryId != null };
        return rows.GroupBy(_ => 1)
            .Select(g => new ExecutionRetryStats(g.Count(), g.Count(row => row.HasRetry)));
    }
}
