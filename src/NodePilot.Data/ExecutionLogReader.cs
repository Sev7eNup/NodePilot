using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;

namespace NodePilot.Data;

/// <summary>
/// Default <see cref="IExecutionLogReader"/> for the AI chat assistant's execution-log tools.
/// Redacts ALL free-text fields (ErrorMessage, Output, ErrorOutput) regardless of the caller's
/// role — deliberately stricter than the role-based gradient in <c>ExecutionsController</c>,
/// because these results are sent to an external LLM. Redaction at write time (StepRunner) is
/// the first line of defense; this is belt-and-braces cover for older rows or regressions.
/// The ownership check (does this execution actually belong to the authorized workflow?) lives
/// only here.
/// </summary>
public sealed class ExecutionLogReader(NodePilotDbContext db, IAuditDetailsRedactor redactor) : IExecutionLogReader
{
    private const int MaxTake = 20;

    public async Task<IReadOnlyList<ExecutionLogSummary>> GetRecentExecutionsAsync(
        Guid workflowId, int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, MaxTake);

        var rows = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.WorkflowId == workflowId)
            .OrderByDescending(e => e.StartedAt)
            .Take(take)
            .Select(e => new { e.Id, e.Status, e.StartedAt, e.CompletedAt, e.TriggeredBy, e.ErrorMessage })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var execIds = rows.Select(r => r.Id).ToList();

        // Two batched IN-list queries instead of a sub-select per row — the same provider-portable
        // pattern as ExecutionsController.GetAll (works identically on Postgres/SqlServer/SQLite test backend).
        var stepCounts = await db.StepExecutions.AsNoTracking()
            .Where(s => execIds.Contains(s.WorkflowExecutionId))
            .GroupBy(s => s.WorkflowExecutionId)
            .Select(g => new { ExecId = g.Key, Total = g.Count() })
            .ToDictionaryAsync(x => x.ExecId, x => x.Total, ct);

        var failedStepRows = await db.StepExecutions.AsNoTracking()
            .Where(s => execIds.Contains(s.WorkflowExecutionId) && s.Status == ExecutionStatus.Failed)
            .OrderBy(s => s.StartedAt)
            .Select(s => new { s.WorkflowExecutionId, s.StepId, s.StepName })
            .ToListAsync(ct);
        var failedByExec = failedStepRows
            .GroupBy(s => s.WorkflowExecutionId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<FailedStepInfo>)g.Select(s => new FailedStepInfo(s.StepId, s.StepName)).ToList());

        return rows.Select(e => new ExecutionLogSummary(
            e.Id,
            e.Status.ToString(),
            e.StartedAt,
            e.CompletedAt,
            e.TriggeredBy,
            redactor.Redact(e.ErrorMessage),
            stepCounts.TryGetValue(e.Id, out var total) ? total : 0,
            failedByExec.TryGetValue(e.Id, out var failed) ? failed : [])).ToList();
    }

    public async Task<ExecutionStepLogs?> GetExecutionStepsAsync(
        Guid workflowId, Guid executionId, CancellationToken ct)
    {
        // Ownership check: both ids are in the filter — an execution belonging to a different
        // workflow is indistinguishable from here from "does not exist".
        var exec = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == executionId && e.WorkflowId == workflowId)
            .Select(e => new { e.Id, e.Status, e.StartedAt, e.CompletedAt, e.TriggeredBy, e.ErrorMessage })
            .FirstOrDefaultAsync(ct);
        if (exec is null) return null;

        var steps = await db.StepExecutions.AsNoTracking()
            .Where(s => s.WorkflowExecutionId == executionId)
            .OrderBy(s => s.StartedAt)
            .Select(s => new
            {
                s.StepId, s.StepName, s.StepType, s.TargetMachine, s.Status,
                s.StartedAt, s.CompletedAt, s.AttemptCount, s.Output, s.ErrorOutput,
            })
            .ToListAsync(ct);

        var failed = steps
            .Where(s => s.Status == ExecutionStatus.Failed)
            .Select(s => new FailedStepInfo(s.StepId, s.StepName))
            .ToList();

        var summary = new ExecutionLogSummary(
            exec.Id,
            exec.Status.ToString(),
            exec.StartedAt,
            exec.CompletedAt,
            exec.TriggeredBy,
            redactor.Redact(exec.ErrorMessage),
            steps.Count,
            failed);

        var stepLogs = steps.Select(s => new StepExecutionLog(
            s.StepId,
            s.StepName,
            s.StepType,
            s.TargetMachine,
            s.Status.ToString(),
            s.StartedAt,
            s.CompletedAt,
            s.AttemptCount,
            redactor.Redact(s.Output),
            redactor.Redact(s.ErrorOutput))).ToList();

        return new ExecutionStepLogs(summary, stepLogs);
    }
}
