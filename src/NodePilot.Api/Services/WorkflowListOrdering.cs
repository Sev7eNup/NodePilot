using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Services;

/// <summary>Orders the authorized query before paging; execution statistics use the same last-20
/// window as the list response. Only the selected page crosses the database boundary.</summary>
internal static class WorkflowListOrdering
{
    public static IQueryable<Workflow> Apply(NodePilotDbContext db, IQueryable<Workflow> query,
        string sort, bool ascending)
    {
        IOrderedQueryable<Workflow> Order<T>(Expression<Func<Workflow, T>> key)
            => (ascending ? query.OrderBy(key) : query.OrderByDescending(key)).ThenBy(w => w.Id);

        switch (sort)
        {
            case "name": return Order(w => w.Name);
            case "activities": return Order(w => w.ActivityCount);
            // Computed metadata stores the trigger keys in ordinal order. Removing JSON
            // punctuation preserves the UI's comma-joined key, including the empty set.
            case "triggers": return Order(w => (w.TriggerTypesJson ?? "").Replace("[", "").Replace("]", "").Replace("\"", ""));
            case "status": return Order(w => !w.IsEnabled);
            case "created": return Order(w => w.CreatedAt);
            case "lastRun":
            case "successRate":
            case "runtime":
                return OrderByStatistics(db, query, sort, ascending);
            default: return Order(w => w.UpdatedAt);
        }
    }

    private static IQueryable<Workflow> OrderByStatistics(NodePilotDbContext db,
        IQueryable<Workflow> query, string sort, bool ascending)
    {
        var duration = db.Database.IsNpgsql()
            ? "EXTRACT(EPOCH FROM (\"CompletedAt\" - \"StartedAt\")) * 1000.0"
            : db.Database.IsSqlServer()
                ? "DATEDIFF_BIG(microsecond, \"StartedAt\", \"CompletedAt\") / 1000.0"
                : "ROUND((julianday(\"CompletedAt\") - julianday(\"StartedAt\")) * 86400000.0)";
        var floating = db.Database.IsNpgsql() ? "double precision" : "float";
        var sql = $$"""
            SELECT "WorkflowId", MAX("StartedAt") AS "LastRun",
                CAST(SUM(CASE WHEN "Status" = 'Succeeded' THEN 1.0 ELSE 0 END)
                    / NULLIF(SUM(CASE WHEN "Status" IN ('Succeeded', 'Failed', 'Cancelled') THEN 1 ELSE 0 END), 0)
                    AS {{floating}}) AS "SuccessRate",
                CAST(AVG(CASE WHEN "Status" = 'Succeeded' AND "CompletedAt" IS NOT NULL
                    THEN {{duration}} END) AS {{floating}}) AS "Runtime"
            FROM (
                SELECT "WorkflowId", "StartedAt", "CompletedAt", "Status",
                    ROW_NUMBER() OVER (PARTITION BY "WorkflowId" ORDER BY "StartedAt" DESC, "Id" DESC) AS rn
                FROM "WorkflowExecutions"
            ) AS ranked WHERE rn <= 20 GROUP BY "WorkflowId"
            """;
        var stats = db.Database.SqlQueryRaw<Statistics>(sql);
        var joined = query.GroupJoin(stats, w => w.Id, s => s.WorkflowId, (workflow, matches) => new { workflow, matches })
            .SelectMany(x => x.matches.DefaultIfEmpty(), (x, stat) => new { x.workflow, stat });
        // Explicit null placement matches the existing client sort for never-run workflows.
        var ordered = sort switch
        {
            "lastRun" => ascending
                ? joined.OrderBy(x => x.stat.LastRun ?? DateTime.MinValue)
                : joined.OrderByDescending(x => x.stat.LastRun ?? DateTime.MinValue),
            "successRate" => ascending
                ? joined.OrderBy(x => x.stat.SuccessRate ?? -1)
                : joined.OrderByDescending(x => x.stat.SuccessRate ?? -1),
            _ => ascending
                ? joined.OrderBy(x => x.stat.Runtime ?? -1)
                : joined.OrderByDescending(x => x.stat.Runtime ?? -1),
        };
        // Keep ordering in the expression after projection, without re-ordering by Id first.
        return ordered.ThenBy(x => x.workflow.Id).Select(x => x.workflow);
    }

    private sealed record Statistics(Guid WorkflowId, DateTime? LastRun, double? SuccessRate, double? Runtime);
}
