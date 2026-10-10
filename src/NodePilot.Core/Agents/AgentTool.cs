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

public enum AgentBudgetLimit { Other, ModelCalls }

public sealed class AgentBudgetExceededException(string message, AgentBudgetLimit limit = AgentBudgetLimit.Other) : InvalidOperationException(message)
{
    public AgentBudgetLimit Limit { get; } = limit;
}

public sealed class AgentBudget
{
    private readonly object _sync = new();
    private int _reservedModelCalls;
    public bool Unlimited { get; }
    public int MaxModelCalls { get; }
    public int MaxToolCalls { get; }
    public int MaxDelegations { get; }
    private int _modelCalls, _toolCalls, _delegations;
    private long? _inputTokens, _outputTokens;
    public int ModelCalls { get { lock (_sync) return _modelCalls; } }
    public int ToolCalls { get { lock (_sync) return _toolCalls; } }
    public int Delegations { get { lock (_sync) return _delegations; } }
    public long? InputTokens { get { lock (_sync) return _inputTokens; } }
    public long? OutputTokens { get { lock (_sync) return _outputTokens; } }
    public int RemainingModelCalls { get { lock (_sync) return Unlimited ? int.MaxValue : MaxModelCalls - _modelCalls - _reservedModelCalls; } }
    public sealed record Usage(int ModelCalls, int ToolCalls, int Delegations, long? InputTokens, long? OutputTokens);
    public Usage Snapshot() { lock (_sync) return new(_modelCalls, _toolCalls, _delegations, _inputTokens, _outputTokens); }
    public bool LastModelCall => RemainingModelCalls <= 1;
    public void ReserveFinalReport() { lock (_sync) _reservedModelCalls = MaxModelCalls > 1 ? 1 : 0; }
    public void BeginFinalReport() { lock (_sync) _reservedModelCalls = 0; }

    public AgentBudget(int modelCalls, int toolCalls, int delegations, bool unlimited = false)
    {
        Unlimited = unlimited;
        MaxModelCalls = modelCalls;
        MaxToolCalls = toolCalls;
        MaxDelegations = delegations;
    }

    public int TakeModelCall()
    {
        lock (_sync)
        {
            if (RemainingModelCalls <= 0) throw new AgentBudgetExceededException("Model call budget exhausted (the final-report reservation cannot be used for further investigation).", AgentBudgetLimit.ModelCalls);
            return ++_modelCalls;
        }
    }

    public void TakeToolCall()
    {
        lock (_sync)
        {
            if (!Unlimited && ToolCalls >= MaxToolCalls) throw new AgentBudgetExceededException("Tool call budget exhausted.");
            _toolCalls++;
        }
    }

    public bool TryTakeModelRetry(bool reserveAnswer, out int callNumber)
    {
        lock (_sync)
        {
            callNumber = 0;
            if (RemainingModelCalls <= (reserveAnswer ? 1 : 0)) return false;
            callNumber = ++_modelCalls;
            return true;
        }
    }

    public void AddUsage(long? input, long? output)
    {
        lock (_sync)
        {
            if (input.HasValue) _inputTokens = (_inputTokens ?? 0) + input;
            if (output.HasValue) _outputTokens = (_outputTokens ?? 0) + output;
        }
    }

    public void TakeDelegationBatch(int assignments, int started)
    {
        lock (_sync)
        {
            if (assignments < 1 || started < 0 || started > assignments) throw new ArgumentOutOfRangeException(nameof(assignments));
            if (!Unlimited && assignments > MaxToolCalls - _toolCalls) throw new AgentBudgetExceededException("Tool call budget exhausted.");
            if (!Unlimited && started > MaxDelegations - _delegations) throw new AgentBudgetExceededException("Delegation budget exhausted.");
            _toolCalls += assignments;
            _delegations += started;
        }
    }
}
