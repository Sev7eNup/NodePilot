using System.Collections.Concurrent;

namespace NodePilot.Scheduler;

/// <summary>
/// Process-local record of which trigger registrations are currently broken, written by
/// <see cref="TriggerOrchestrator"/> and read by
/// <see cref="SystemAlerts.Sources.TriggerUnhealthySource"/>.
///
/// A trigger that cannot register — a fileWatcherTrigger whose UNC share is gone, a cron the
/// scheduler rejects — is caught per-trigger and retried with backoff, which means the sync pass
/// itself keeps succeeding and every existing surface keeps looking green: the heartbeat, the
/// dashboard's "armed triggers" (derived from the workflow definition, not from runtime state),
/// and the log after the first warning. This registry is what makes that state alertable.
///
/// Deliberately in memory rather than a table: trigger sources are process-local by definition,
/// so a persisted row would outlive the process that owns it and need a staleness/ownership
/// protocol, and writing it would put a database round-trip on the 5-second sync path. Both the
/// writer (the orchestrator) and the reader (the alert evaluator) are leader-gated in the same
/// process, so they always agree. The one visible seam is HA: the alerting controller probes
/// source availability on whichever node serves the request, so a follower — which runs no
/// triggers — reports this source as unavailable even while the leader alerts correctly.
/// </summary>
public sealed class TriggerHealthRegistry
{
    // Keyed by the orchestrator's own registry key ($"{workflowId}:{nodeId}") so the two never
    // drift and nothing has to parse a key back into its parts.
    private readonly ConcurrentDictionary<string, TriggerHealthEntry> _unhealthy = new();

    public void MarkUnhealthy(string key, Guid workflowId, string nodeId, string triggerType, string reason, int consecutiveFailures, DateTime nowUtc)
        => _unhealthy.AddOrUpdate(
            key,
            _ => new TriggerHealthEntry(workflowId, nodeId, triggerType, reason, nowUtc, consecutiveFailures),
            // SinceUtc must survive repeated failures — it is what "unhealthy for N seconds"
            // measures, and restamping it on every retry would pin that number near zero forever.
            (_, existing) => existing with { Reason = reason, ConsecutiveFailures = consecutiveFailures });

    public void MarkHealthy(string key) => _unhealthy.TryRemove(key, out _);

    /// <summary>Drops everything — used when this node stops owning triggers at all (leadership
    /// loss, shutdown).</summary>
    public void Clear() => _unhealthy.Clear();

    public IReadOnlyCollection<TriggerHealthEntry> Snapshot() => _unhealthy.Values.ToList();
}

/// <param name="Reason">Operator-facing diagnostic — the source's fault reason or the registration
/// error.</param>
/// <param name="SinceUtc">When this trigger first went unhealthy, not when it last failed.</param>
/// <param name="ConsecutiveFailures">Failed registration attempts so far; 0 while it has been
/// evicted but not yet retried.</param>
public sealed record TriggerHealthEntry(
    Guid WorkflowId,
    string NodeId,
    string TriggerType,
    string Reason,
    DateTime SinceUtc,
    int ConsecutiveFailures);
