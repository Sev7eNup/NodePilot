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

    /// <summary>
    /// Current liveness. MUST be a pure in-memory field read — no I/O, no locks, no awaiting.
    /// The orchestrator evaluates this SEQUENTIALLY for every registered trigger inside its
    /// 5-second sync pass; a blocking probe here (Directory.Exists on a dead UNC path can hang
    /// for the SMB timeout) would stall reconciliation for EVERY workflow, not just this one.
    /// A source that needs real I/O to answer must run it on its own timer and cache the verdict.
    ///
    /// Reporting unhealthy makes the orchestrator dispose and re-create the source, which routes
    /// it back through the existing exponential-backoff registration path. Without this a source
    /// that started fine and later died (FileSystemWatcher whose UNC share vanished, poll loop
    /// that exited) would sit in the registry forever with a matching config hash and never be
    /// retried.
    /// </summary>
    TriggerHealth Health { get; }

    /// <summary>Start listening. Must be idempotent — repeated calls with the same config
    /// no-op.</summary>
    Task StartAsync(TriggerContext context, CancellationToken ct);
}

/// <summary>
/// Liveness verdict of a trigger source. <see cref="Reason"/> is a short operator-facing
/// diagnostic that lands in the orchestrator's eviction log line — not a stable machine contract.
/// </summary>
public readonly record struct TriggerHealth(bool IsHealthy, string? Reason)
{
    public static readonly TriggerHealth Healthy = new(true, null);

    public static TriggerHealth Faulted(string reason) => new(false, reason);
}

public sealed class TriggerContext
{
    public required Guid WorkflowId { get; init; }
    public required string NodeId { get; init; }
    public required JsonElement Config { get; init; }
    /// <summary>Stable hash of the trigger configuration. Production cursors are scoped to this
    /// value so changing a directory, query or cron expression starts from a fresh baseline.</summary>
    public string ConfigurationHash { get; init; } = string.Empty;
    /// <summary>Caller-supplied callback invoked when the trigger fires. The orchestrator
    /// turns this into an engine.ExecuteAsync call with the given parameters.</summary>
    public required Func<Dictionary<string, string>, Task> OnFire { get; init; }

    /// <summary>
    /// Production delivery boundary. Returns true only after the signal and its source cursor
    /// were committed durably. Sources retry or reconcile while it returns false. Optional so
    /// isolated source tests and embedders can keep using <see cref="OnFire"/>.
    /// </summary>
    public Func<TriggerSignal, Task<bool>>? OnDurableFire { get; init; }

    public Func<Task<TriggerCheckpoint?>>? ReadCheckpoint { get; init; }
    public Func<TriggerCheckpoint, Task<bool>>? InitializeCheckpoint { get; init; }
    public Func<TriggerCheckpoint, Task<bool>>? SaveCheckpoint { get; init; }

    public async Task<bool> DeliverAsync(TriggerSignal signal)
    {
        if (OnDurableFire is not null) return await OnDurableFire(signal);
        await OnFire(signal.Parameters);
        return true;
    }

    public Task<TriggerCheckpoint?> ReadCheckpointAsync()
        => ReadCheckpoint?.Invoke() ?? Task.FromResult<TriggerCheckpoint?>(null);

    public Task<bool> InitializeCheckpointAsync(TriggerCheckpoint checkpoint)
        => InitializeCheckpoint?.Invoke(checkpoint) ?? Task.FromResult(true);

    public Task<bool> SaveCheckpointAsync(TriggerCheckpoint checkpoint)
        => SaveCheckpoint?.Invoke(checkpoint) ?? Task.FromResult(true);
}

public sealed record TriggerSignal(
    string EventKey,
    string Position,
    Dictionary<string, string> Parameters);

public sealed record TriggerCheckpoint(string Position, string Version);
