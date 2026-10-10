using NodePilot.Data;

namespace NodePilot.Engine.Agents;

/// <summary>Serializes access to the step-scoped context shared by team members.</summary>
public sealed class AgentRunDatabase(NodePilotDbContext db) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> UseAsync<T>(Func<NodePilotDbContext, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await action(db); }
        finally { _gate.Release(); }
    }

    public Task UseAsync(Func<NodePilotDbContext, Task> action, CancellationToken ct)
        => UseAsync(async context => { await action(context); return true; }, ct);

    public void Dispose() => _gate.Dispose();
}
