using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodePilot.Ai.Agents;
using NodePilot.Core.Agents;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Agents;

public sealed class AgentActivityRunner(AgentRuntime runtime, AgentToolHost tools, AgentExecutionGate gate,
    AgentRunJournal journal, IOptionsMonitor<AgentOptions> options, OutputRedactor redactor, ILogger<AgentActivityRunner> logger)
{
    public async Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement raw, bool team, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var sessions = new List<AgentToolHost.Session>();
        AgentBudget? budget = null;
        AgentArtifactStore? artifacts = null;
        var status = "Failed";
        string? result = null;
        string? error = null;
        var outcome = "unassessed";
        string? outcomeReason = null;
        try
        {
            var limits = options.CurrentValue;
            if (!limits.Enabled) throw new InvalidOperationException("Agent activities are disabled.");
            var config = AgentConfiguration.Parse(raw, team);
            if (!team && config.Agent.TargetMachineId is null)
                config = config with { Agent = config.Agent with { TargetMachineId = context.TargetMachineId } };
            var modelCalls = Bound(config.MaxModelCalls, team ? limits.TeamModelCalls : limits.SingleModelCalls, "model calls");
            if (modelCalls < 2) throw new ArgumentException("Agent model budget needs at least two calls: investigation and final report.");
            var toolCalls = Bound(config.MaxToolCalls, team ? limits.TeamToolCalls : limits.SingleToolCalls, "tool calls");
            var seconds = Bound(config.TimeoutSeconds, team ? limits.TeamTimeoutSeconds : limits.SingleTimeoutSeconds, "timeout");
            budget = new AgentBudget(modelCalls, toolCalls, team ? Bound(config.MaxDelegations, limits.TeamDelegations, "delegations") : 0);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            await journal.StartAsync(context, timeout.Token);
            using var lease = await gate.AcquireAsync(timeout.Token);
            artifacts = new AgentArtifactStore(journal.Run!.Id);
            var definitions = team ? config.Members : [config.Agent];
            var memberTools = new Dictionary<string, IReadOnlyList<AgentTool>>(StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                var memberContext = team ? new StepExecutionContext
                {
                    WorkflowExecutionId = context.WorkflowExecutionId, StepId = context.StepId, StepLabel = context.StepLabel,
                    WorkflowName = context.WorkflowName, Variables = context.Variables
                } : context;
                var session = await tools.OpenAsync(definition, memberContext, journal.Run.Id, artifacts, lease, timeout.Token, limits.MaxToolOutputCharacters);
                sessions.Add(session);
                memberTools.Add(definition.Id, session.Tools);
            }
            var answer = await runtime.RunAsync(config, team, memberTools, budget, limits, async (progress, token) =>
            {
                if (!options.CurrentValue.Enabled) throw new InvalidOperationException("Agent activities have been disabled.");
                journal.RecordUsage(budget);
                await journal.AppendAsync(progress, token);
            }, text => AgentContentRedactor.Redact(text, redactor), timeout.Token);
            result = answer.Text;
            outcome = answer.Outcome;
            outcomeReason = answer.OutcomeReason;
            status = "Succeeded";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = "Cancelled";
            error = "Agent execution was cancelled.";
            throw;
        }
        catch (OperationCanceledException) { error = "Agent execution exceeded its time budget."; }
        catch (Exception ex) { error = AgentContentRedactor.Redact(ex.Message, redactor); }
        finally
        {
            foreach (var session in sessions.AsEnumerable().Reverse())
                try { await session.DisposeAsync(); } catch (Exception ex) { logger.LogWarning(ex, "Agent tool cleanup failed"); }
            if (artifacts is not null)
                try { await artifacts.DisposeAsync(); } catch (Exception ex) { logger.LogWarning(ex, "Agent artifact cleanup failed"); }
            if (budget is not null && journal.Run is not null)
            {
                if (status != "Succeeded") result ??= journal.Run.Result;
                using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await journal.FinishAsync(status, result, error, budget, finish.Token); }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not finalize agent journal {AgentRunId}", journal.Run.Id);
                    status = "Failed";
                    outcome = "unassessed";
                    outcomeReason = null;
                    error = "Agent actions ended, but the durable result could not be saved. Do not automatically retry.";
                }
            }
        }
        return new ActivityResult
        {
            Success = status == "Succeeded", Output = result, ErrorOutput = error, Duration = clock.Elapsed,
            OutputParameters = new Dictionary<string, string>
            {
                ["agentRunId"] = journal.Run?.Id.ToString() ?? "",
                ["outcome"] = outcome,
                ["outcomeReason"] = outcomeReason ?? "No final task assessment was produced.",
                ["modelCalls"] = (budget?.ModelCalls ?? 0).ToString(CultureInfo.InvariantCulture),
                ["toolCalls"] = (budget?.ToolCalls ?? 0).ToString(CultureInfo.InvariantCulture),
                ["delegations"] = (budget?.Delegations ?? 0).ToString(CultureInfo.InvariantCulture),
                ["promptTokens"] = budget?.InputTokens?.ToString(CultureInfo.InvariantCulture) ?? "",
                ["completionTokens"] = budget?.OutputTokens?.ToString(CultureInfo.InvariantCulture) ?? ""
            }
        };
    }

    private static int Bound(int? requested, int ceiling, string name)
    {
        if (ceiling <= 0 || requested is <= 0 || requested > ceiling)
            throw new ArgumentException($"Agent {name} must be positive and cannot exceed the administrator's limit ({ceiling}).");
        return requested ?? ceiling;
    }
}
