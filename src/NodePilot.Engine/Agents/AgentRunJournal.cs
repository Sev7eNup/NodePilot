using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Agents;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Agents;

public sealed class AgentRunJournal(AgentRunDatabase database, IExecutionNotifier notifier,
    OutputRedactor redactor, ILogger<AgentRunJournal> logger)
{
    private long _sequence;
    private Guid _workflowId;
    private AgentRunCheckpoint _checkpoint = new();
    public AgentRun? Run { get; private set; }

    public async Task StartAsync(StepExecutionContext context, CancellationToken ct)
    {
        await database.UseAsync(async db =>
        {
            _sequence = 0;
            _checkpoint = new();
            _workflowId = await db.WorkflowExecutions.Where(x => x.Id == context.WorkflowExecutionId).Select(x => x.WorkflowId).SingleAsync(ct);
            Run = new AgentRun { Id = Guid.NewGuid(), WorkflowExecutionId = context.WorkflowExecutionId, StepId = context.StepId };
            db.AgentRuns.Add(Run);
            await db.SaveChangesAsync(ct);
        }, ct);
        await AppendAsync(new AgentProgress("run_started", "Agent run started"), ct);
    }

    public async Task AppendAsync(AgentProgress progress, CancellationToken ct, AgentBudget? budget = null)
    {
        var run = Run ?? throw new InvalidOperationException("Agent journal is not started.");
        var entry = await database.UseAsync(async db =>
        {
            var content = AgentContentRedactor.Redact(progress.Content, redactor);
            if (content.Length > 64_000) content = content[..64_000] + "\n[truncated]";
            if (_checkpoint.Update(progress with { Content = content }) is { } checkpoint)
                run.Result = checkpoint;
            if (budget is not null) ApplyUsage(budget.Snapshot());
            var saved = new AgentRunEvent
            {
                AgentRunId = run.Id,
                Sequence = ++_sequence,
                MemberId = progress.MemberId,
                Kind = progress.Kind,
                ToolName = progress.ToolName,
                Content = content
            };
            db.AgentRunEvents.Add(saved);
            try { await db.SaveChangesAsync(ct); }
            catch { _sequence--; throw; }
            finally { db.Entry(saved).State = EntityState.Detached; }
            return saved;
        }, ct);
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
        await database.UseAsync(db =>
        {
            Run.Status = status;
            Run.CompletedAt = DateTime.UtcNow;
            var finalResult = result ?? (status == "Succeeded" ? null : Run.Result);
            Run.Result = finalResult is null ? null : AgentContentRedactor.Redact(finalResult, redactor);
            Run.Error = error is null ? null : AgentContentRedactor.Redact(error, redactor);
            return Task.CompletedTask;
        }, ct);
        await AppendAsync(new AgentProgress("run_" + status.ToLowerInvariant(), Run.Error ?? status), ct, budget);
    }

    private void ApplyUsage(AgentBudget.Usage budget)
    {
        if (Run is null) return;
        Run.ModelCalls = budget.ModelCalls;
        Run.ToolCalls = budget.ToolCalls;
        Run.Delegations = budget.Delegations;
        Run.InputTokens = budget.InputTokens;
        Run.OutputTokens = budget.OutputTokens;
    }
}
