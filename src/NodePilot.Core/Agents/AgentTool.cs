using System.Text.Json;

namespace NodePilot.Core.Agents;

public sealed record AgentTool(string Name, string Description, JsonElement Schema,
    Func<JsonElement, CancellationToken, Task<string>> InvokeAsync)
{
    public bool IsSkillGuidance { get; init; }
    public IReadOnlyList<AgentSkillGuidance> Skills { get; init; } = [];
}

public sealed record AgentSkillGuidance(Guid Id, string Name, string Version, string Sha256,
    string Instructions, IReadOnlyList<string> Resources, Func<CancellationToken, Task> ValidateAsync);

public sealed record AgentRunResult(string Text, int ModelCalls, int ToolCalls, int Delegations,
    long? InputTokens, long? OutputTokens, string Outcome = "unassessed", string? OutcomeReason = null);

public sealed class AgentBudgetExceededException(string message) : InvalidOperationException(message);

public sealed class AgentBudget
{
    private readonly object _sync = new();
    private int _reservedModelCalls;
    public int MaxModelCalls { get; }
    public int MaxToolCalls { get; }
    public int MaxDelegations { get; }
    public int ModelCalls { get; private set; }
    public int ToolCalls { get; private set; }
    public int Delegations { get; private set; }
    public long? InputTokens { get; private set; }
    public long? OutputTokens { get; private set; }
    public int RemainingModelCalls => MaxModelCalls - ModelCalls - _reservedModelCalls;
    public bool LastModelCall => RemainingModelCalls <= 1;
    public void ReserveFinalReport() { lock (_sync) _reservedModelCalls = MaxModelCalls > 1 ? 1 : 0; }
    public void BeginFinalReport() { lock (_sync) _reservedModelCalls = 0; }

    public AgentBudget(int modelCalls, int toolCalls, int delegations)
    {
        MaxModelCalls = modelCalls;
        MaxToolCalls = toolCalls;
        MaxDelegations = delegations;
    }

    public void TakeModelCall()
    {
        lock (_sync)
        {
            if (RemainingModelCalls <= 0) throw new AgentBudgetExceededException("Model call budget exhausted (the final-report reservation cannot be used for further investigation).");
            ModelCalls++;
        }
    }

    public void TakeToolCall()
    {
        lock (_sync)
        {
            if (ToolCalls >= MaxToolCalls) throw new AgentBudgetExceededException("Tool call budget exhausted.");
            ToolCalls++;
        }
    }

    public void TakeDelegation()
    {
        lock (_sync)
        {
            if (Delegations >= MaxDelegations) throw new AgentBudgetExceededException("Delegation budget exhausted.");
            Delegations++;
        }
    }

    public void AddUsage(long? input, long? output)
    {
        lock (_sync)
        {
            if (input.HasValue) InputTokens = (InputTokens ?? 0) + input;
            if (output.HasValue) OutputTokens = (OutputTokens ?? 0) + output;
        }
    }
}
