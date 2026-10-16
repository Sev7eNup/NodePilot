using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Services;

/// <summary>Orders the authorized execution query before paging, so a column sort spans every
/// page rather than only the rows already loaded. Ties fall back to newest first.</summary>
internal static class ExecutionListOrdering
{
    internal static readonly IReadOnlySet<string> Sorts = new HashSet<string>(StringComparer.Ordinal)
    {
        "started", "status", "workflow", "trigger", "startedBy", "steps", "duration",
    };

    public static IQueryable<WorkflowExecution> Apply(NodePilotDbContext db,
        IQueryable<WorkflowExecution> query, string sort, bool ascending)
    {
        IQueryable<WorkflowExecution> Order<T>(Expression<Func<WorkflowExecution, T>> key)
            => (ascending ? query.OrderBy(key) : query.OrderByDescending(key))
                .ThenByDescending(e => e.StartedAt).ThenByDescending(e => e.Id);

        return sort switch
        {
            "status" => Order(e => e.Status),
            "workflow" => Order(e => e.Workflow.Name),
            "trigger" => Order(e => e.TriggeredBy ?? ""),
            "startedBy" => Order(e => db.Users.Where(u => u.Id == e.StartedByUserId)
                .Select(u => u.Username).FirstOrDefault() ?? ""),
            "steps" => Order(e => db.StepExecutions.Count(s =>
                s.WorkflowExecutionId == e.Id && s.Status != ExecutionStatus.Skipped)),
            "duration" => OrderByDuration(db, query, ascending),
            _ => ascending
                ? query.OrderBy(e => e.StartedAt).ThenBy(e => e.Id)
                : query.OrderByDescending(e => e.StartedAt).ThenByDescending(e => e.Id),
        };
    }

    private static IQueryable<WorkflowExecution> OrderByDuration(NodePilotDbContext db,
        IQueryable<WorkflowExecution> query, bool ascending)
    {
        var floating = db.Database.IsNpgsql() ? "double precision" : "float";
        var durations = db.Database.SqlQueryRaw<ExecutionDuration>(
            $"SELECT \"Id\", CAST({WorkflowListOrdering.DurationMillisecondsSql(db)} AS {floating}) AS \"DurationMs\" FROM \"WorkflowExecutions\"");
        var joined = query.Join(durations, e => e.Id, d => d.Id, (execution, d) => new { execution, d.DurationMs });
        // Runs without a completion time sort as the shortest.
        var ordered = ascending
            ? joined.OrderBy(x => x.DurationMs ?? -1)
            : joined.OrderByDescending(x => x.DurationMs ?? -1);
        return ordered.ThenByDescending(x => x.execution.StartedAt).ThenByDescending(x => x.execution.Id)
            .Select(x => x.execution);
    }

    private sealed record ExecutionDuration(Guid Id, double? DurationMs);
}
