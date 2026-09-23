using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Agents;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Agents;

public sealed class AgentRunJournal(NodePilotDbContext db, IExecutionNotifier notifier,
    OutputRedactor redactor, ILogger<AgentRunJournal> logger)
{
    private long _sequence;
    private Guid _workflowId;
    public AgentRun? Run { get; private set; }

    public async Task StartAsync(StepExecutionContext context, CancellationToken ct)
    {
        _sequence = 0;
        _workflowId = await db.WorkflowExecutions.Where(x => x.Id == context.WorkflowExecutionId).Select(x => x.WorkflowId).SingleAsync(ct);
        Run = new AgentRun { Id = Guid.NewGuid(), WorkflowExecutionId = context.WorkflowExecutionId, StepId = context.StepId };
        db.AgentRuns.Add(Run);
        await db.SaveChangesAsync(ct);
        await AppendAsync(new AgentProgress("run_started", "Agent run started"), ct);
    }

    public async Task AppendAsync(AgentProgress progress, CancellationToken ct)
    {
        var run = Run ?? throw new InvalidOperationException("Agent journal is not started.");
        var content = AgentContentRedactor.Redact(progress.Content, redactor);
        if (content.Length > 64_000) content = content[..64_000] + "\n[truncated]";
        if (progress.Kind == "report_draft") run.Result = content;
        var entry = new AgentRunEvent
        {
            AgentRunId = run.Id, Sequence = ++_sequence, MemberId = progress.MemberId,
            Kind = progress.Kind, ToolName = progress.ToolName, Content = content
        };
        db.AgentRunEvents.Add(entry);
        await db.SaveChangesAsync(ct);
        db.Entry(entry).State = EntityState.Detached;
        try
        {
            await notifier.AgentEventAsync(_workflowId, new AgentEventNotification(run.WorkflowExecutionId,
                run.StepId, run.Id, entry.Sequence, entry.Timestamp, entry.Kind, entry.Content, entry.MemberId, entry.ToolName));
        }
        catch (Exception ex) { logger.LogWarning(ex, "Agent live notification failed for {AgentRunId}; the journal remains available", run.Id); }
    }

    public async Task FinishAsync(string status, string? result, string? error, AgentBudget budget, CancellationToken ct)
    {
        if (Run is null) return;
        Run.Status = status;
        Run.CompletedAt = DateTime.UtcNow;
        var finalResult = result ?? (status == "Succeeded" ? null : Run.Result);
        Run.Result = finalResult is null ? null : AgentContentRedactor.Redact(finalResult, redactor);
        Run.Error = error is null ? null : AgentContentRedactor.Redact(error, redactor);
        RecordUsage(budget);
        await AppendAsync(new AgentProgress("run_" + status.ToLowerInvariant(), Run.Error ?? status), ct);
    }

    public void RecordUsage(AgentBudget budget)
    {
        if (Run is null) return;
        Run.ModelCalls = budget.ModelCalls;
        Run.ToolCalls = budget.ToolCalls;
        Run.Delegations = budget.Delegations;
        Run.InputTokens = budget.InputTokens;
        Run.OutputTokens = budget.OutputTokens;
    }
}
