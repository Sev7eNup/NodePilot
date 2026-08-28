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
    /// <summary>Caller-supplied callback invoked when the trigger fires. The orchestrator
    /// turns this into an engine.ExecuteAsync call with the given parameters.</summary>
    public required Func<Dictionary<string, string>, Task> OnFire { get; init; }
}
