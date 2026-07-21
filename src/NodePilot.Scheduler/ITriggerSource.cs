using System.Text.Json;

namespace NodePilot.Scheduler;

/// <summary>
/// A live subscription to some external signal (cron, file system, DB, …) that fires a workflow.
/// The orchestrator creates one source per (workflow, trigger-node) pair and disposes it when the
/// workflow is deleted / disabled / the trigger node is removed.
/// </summary>
public interface ITriggerSource : IAsyncDisposable
{
    /// <summary>The trigger node's activityType (e.g. "scheduleTrigger").</summary>
    string ActivityType { get; }

    /// <summary>Start listening. Must be idempotent — repeated calls with the same config no-op.</summary>
    Task StartAsync(TriggerContext context, CancellationToken ct);
}

public sealed class TriggerContext
{
    public required Guid WorkflowId { get; init; }
    public required string NodeId { get; init; }
    public required JsonElement Config { get; init; }
    /// <summary>Caller-supplied callback invoked when the trigger fires. The orchestrator
    /// turns this into an engine.ExecuteAsync call with the given parameters.</summary>
    public required Func<Dictionary<string, string>, Task> OnFire { get; init; }
}
