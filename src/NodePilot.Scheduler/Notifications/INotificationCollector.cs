using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler.Notifications;

/// <summary>
/// One execution-family alert collector run by <see cref="NotificationDispatcher"/> every pass for CUSTOM
/// rules (terminal executions, long-running, queued-long). Infra/signal alerts are no longer collectors —
/// they are modular <c>ISystemAlertSource</c>s evaluated per system policy (ADR 0008).
/// Each implementation owns one EventKey shape end-to-end: it builds the contexts during
/// collection AND rebuilds them from a crash-orphaned attempt's key on the recovery path,
/// so a new alert source cannot ship a key format the recovery branch doesn't understand.
///
/// <para>Collectors run on the dispatcher's leader-gated pass and share its
/// match → suppress → persist-Pending → send pipeline; they never send themselves.</para>
/// </summary>
internal interface INotificationCollector
{
    /// <summary>
    /// Collects alertable contexts for this pass. May stage tracked-entity changes on
    /// <paramref name="db"/> (execution watermark, gauge signal-states) — the dispatcher
    /// pipeline's single SaveChanges flushes them together with the Pending attempts,
    /// preserving persist-before-send crash-safety. Returns <c>null</c> when there is
    /// nothing to do this pass (the pipeline — including its SaveChanges — is skipped).
    /// </summary>
    Task<NotificationCollection?> CollectAsync(
        NodePilotDbContext db,
        IReadOnlyList<NotificationRule> enabledRules,
        DateTime now,
        CancellationToken ct);

    /// <summary>
    /// Rebuilds a context from a crash-orphaned attempt's EventKey (recovery path).
    /// Returns <c>null</c> when the key doesn't belong to this collector or the source
    /// row is gone (the dispatcher then fails the attempt out).
    /// </summary>
    Task<NotificationContext?> TryReconstructContextAsync(
        NodePilotDbContext db, string eventKey, CancellationToken ct);
}

/// <summary>
/// A collector's per-pass output: the rule subset the pipeline should match against
/// (pre-filtered where the event family allows it; the execution collector passes all
/// enabled rules) plus the contexts gathered this pass. Contexts may be empty when the
/// collector still needs the pipeline's SaveChanges to flush staged state.
/// </summary>
internal sealed record NotificationCollection(
    IReadOnlyList<NotificationRule> Rules,
    IReadOnlyList<NotificationContext> Contexts);
