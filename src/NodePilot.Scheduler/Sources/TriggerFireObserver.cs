using Microsoft.Extensions.Logging;

namespace NodePilot.Scheduler.Sources;

/// <summary>
/// Observes a fire-and-forget <see cref="TriggerContext.OnFire"/> call.
///
/// <para>The event-driven sources (file watcher, event log, database poller) cannot await the
/// callback: they run inside a <see cref="System.IO.FileSystemWatcher"/> event handler, an
/// <c>EntryWritten</c> handler, or a poll loop that must stay responsive. Discarding the task
/// with <c>_ = ctx.OnFire(...)</c> keeps them responsive but throws the result away — including
/// the exception. A trigger whose dispatch fails then looks exactly like a trigger that never
/// fired: nothing in the log, no metric, no execution row.</para>
///
/// <para>This keeps the non-blocking behaviour and attaches a fault continuation, so a failed
/// dispatch is at least visible to whoever is looking for the run that did not happen.</para>
/// </summary>
internal static class TriggerFireObserver
{
    public static void Observe(Task fire, ILogger logger, string triggerType, Guid workflowId, string nodeId)
    {
        if (fire.IsCompletedSuccessfully) return;

        _ = fire.ContinueWith(
            faulted => logger.LogError(
                faulted.Exception,
                "{TriggerType} fire failed for workflow {WorkflowId} node {NodeId} — no execution was started.",
                triggerType, workflowId, nodeId),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
