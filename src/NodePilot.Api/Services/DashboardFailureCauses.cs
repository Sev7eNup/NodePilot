using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Security;

namespace NodePilot.Api.Services;

/// <summary>Read-only, window-scoped aggregation; no changes to execution logging or persisted errors.</summary>
internal sealed class DashboardFailureCauses(NodePilotDbContext db, OutputRedactor redactor)
{
    private static readonly Regex GuidPattern = new(
        @"(?<![\w-])[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?![\w-])",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));
    private static readonly Regex TimestampPattern = new(
        @"(?<![\w])\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])T(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d(?:\.\d{1,7})?(?:Z|[+-](?:[01]\d|2[0-3]):[0-5]\d)?(?![\w:+-]|\.\d)",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));

    internal sealed class FailureRow
    {
        public Guid Id { get; init; }
        public DateTime StartedAt { get; init; }
        public string? Message { get; init; }
    }

    internal sealed class RawGroup
    {
        public string? Message { get; init; }
        public int Count { get; init; }
        public FailureRow Latest { get; init; } = null!;
    }

    internal IQueryable<RawGroup> BuildQuery(IQueryable<WorkflowExecution> accessible, DateTime since, DateTime now)
    {
        var executions = accessible.Where(e => e.Status == ExecutionStatus.Failed && e.StartedAt >= since && e.StartedAt <= now);
        // Restrict to the same authorized window and select one activity per execution,
        // including activities with an empty error (fallback below). The scalar ID lookup
        // uses the existing (WorkflowExecutionId, StartedAt) index, within this one SQL query.
        var firstFailures = db.StepExecutions.AsNoTracking()
            .Where(s => s.Status == ExecutionStatus.Failed && executions.Select(e => e.Id).Contains(s.WorkflowExecutionId))
            .Where(s => s.Id == db.StepExecutions
                .Where(candidate => candidate.WorkflowExecutionId == s.WorkflowExecutionId && candidate.Status == ExecutionStatus.Failed)
                .OrderBy(candidate => candidate.StartedAt).ThenBy(candidate => candidate.Id)
                .Select(candidate => candidate.Id).First());
        var rows = from execution in executions
                   join failure in firstFailures on execution.Id equals failure.WorkflowExecutionId into failures
                   from failure in failures.DefaultIfEmpty()
                   select new FailureRow
                   {
                       Id = execution.Id,
                       StartedAt = execution.StartedAt,
                       Message = failure != null && failure.ErrorOutput != null
                           && failure.ErrorOutput.Replace("\r", "").Replace("\n", "").Replace("\t", "").Trim() != ""
                           ? failure.ErrorOutput : execution.ErrorMessage ?? "",
                   };
        // SQL Server's usual case-insensitive collation must not merge distinct messages
        // before the ordinal normalization pass. Trailing whitespace is normalized anyway.
        if (db.Database.IsSqlServer())
            rows = rows.Select(r => new FailureRow
            {
                Id = r.Id, StartedAt = r.StartedAt,
                Message = EF.Functions.Collate(r.Message!, "Latin1_General_100_BIN2"),
            });
        return rows.GroupBy(r => r.Message).Select(g => new RawGroup
        {
            Message = g.Key,
            Count = g.Count(),
            // Project the full row so EF uses ROW_NUMBER, rather than a correlated
            // lookup of the latest ID for every raw message group. The key is non-null
            // so the generated join also retains executions with no recorded message.
            Latest = g.OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).First(),
        });
    }

    internal string? Normalize(string? message)
    {
        var scrubbed = redactor.Redact(message);
        if (string.IsNullOrWhiteSpace(scrubbed)) return null;
        var normalized = GuidPattern.Replace(scrubbed, "<id>");
        normalized = TimestampPattern.Replace(normalized, "<timestamp>");
        return WhitespacePattern.Replace(normalized, " ").Trim();
    }

    public async Task<FailureCausesResponse> ReadAsync(IQueryable<WorkflowExecution> accessible, DateTime since, DateTime now, CancellationToken ct)
    {
        // Materialize aggregates, never every execution/step. Redaction and normalization
        // then merge raw variants before ranking (taking five in SQL would lose occurrences).
        var groups = new Dictionary<string, FailureCause>(StringComparer.Ordinal);
        var total = 0;
        await foreach (var raw in BuildQuery(accessible, since, now).AsAsyncEnumerable().WithCancellation(ct))
        {
            var message = Normalize(raw.Message);
            var key = message ?? "";
            total += raw.Count;
            if (groups.TryGetValue(key, out var existing))
            {
                var newer = raw.Latest.StartedAt > existing.LatestStartedAt
                    || (raw.Latest.StartedAt == existing.LatestStartedAt && raw.Latest.Id.CompareTo(existing.LatestExecutionId) > 0);
                groups[key] = existing with
                {
                    Count = existing.Count + raw.Count,
                    LatestExecutionId = newer ? raw.Latest.Id : existing.LatestExecutionId,
                    LatestStartedAt = newer ? raw.Latest.StartedAt : existing.LatestStartedAt,
                };
            }
            else groups[key] = new FailureCause(message, raw.Count, raw.Latest.Id, raw.Latest.StartedAt);
        }
        var top = groups.Values.OrderByDescending(g => g.Count).ThenByDescending(g => g.LatestStartedAt)
            .ThenBy(g => g.Message, StringComparer.Ordinal).Take(5).ToList();
        return new FailureCausesResponse(total, top, total - top.Sum(g => g.Count));
    }
}
