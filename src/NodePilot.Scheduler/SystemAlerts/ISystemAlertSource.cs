using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler.SystemAlerts;

/// <summary>
/// A modular, code-owned system-alert source (ADR 0008). Each source publishes a pure-metadata
/// <see cref="SystemAlertSourceDescriptor"/> and, given a descriptor-validated <see cref="SystemAlertQuery"/>,
/// yields raw <see cref="SystemAlertObservation"/>s WITHOUT deciding severity or health — the central
/// evaluator applies each policy's condition and sustain window. Implementations are stateless and
/// side-effect free; transient per-policy/per-source state lives in the SystemAlertPolicyStates /
/// SystemAlertSourceStates tables (wired in a later phase). Sources are compiled and DI-registered — never
/// user-uploaded, never a text query language.
/// </summary>
public interface ISystemAlertSource
{
    /// <summary>Stable identity; MUST equal <c>Describe().SourceId</c> (guarded by <c>SystemAlertCatalog</c> at boot).</summary>
    string SourceId { get; }

    /// <summary>Pure metadata: field schema, query parameters, presets, scope capability, default severity.</summary>
    SystemAlertSourceDescriptor Describe();

    /// <summary>
    /// Whether this source can produce observations right now (e.g. the underlying feature is enabled /
    /// present). A not-available source surfaces in the catalog as "unavailable" and yields neither
    /// observations nor recovery.
    /// </summary>
    Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct);

    /// <summary>
    /// Sample current observations for the given descriptor-validated parameter set. Read-only — must not
    /// mutate the database or any external system (the write side is the evaluator + delivery pipeline).
    /// </summary>
    Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(
        NodePilotDbContext db, SystemAlertQuery query, CancellationToken ct);
}
