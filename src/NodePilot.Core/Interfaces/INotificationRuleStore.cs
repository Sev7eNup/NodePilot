using NodePilot.Core.Enums;
using NodePilot.Core.Models;

namespace NodePilot.Core.Interfaces;

/// <summary>
/// CRUD persistence for <see cref="NotificationRule"/> and its child <see cref="NotificationRoute"/>
/// + <see cref="NotificationRuleTarget"/> rows. Pure storage; matching/dispatch lives in the
/// scheduler pipeline. Mirrors <see cref="IMaintenanceWindowStore"/>. Route secrets are encrypted
/// at rest by the implementation.
/// </summary>
public interface INotificationRuleStore
{
    /// <summary>All rules with routes + targets, ordered by name. Includes disabled rules.</summary>
    Task<IReadOnlyList<NotificationRule>> GetAllAsync(CancellationToken ct);

    /// <summary>Rules of one <see cref="NotificationRuleKind"/> — the custom (user-defined) and system
    /// (built-in infrastructure) alerting management surfaces each filter on this so neither can list
    /// or mutate the other's rows (ADR 0008, the design decision that split alerting into these two kinds).</summary>
    Task<IReadOnlyList<NotificationRule>> GetAllByKindAsync(NotificationRuleKind kind, CancellationToken ct);

    Task<NotificationRule?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>A rule by id, but only if it is of the given kind (else null) — kind-scoped fetch for the
    /// custom vs. system endpoints.</summary>
    Task<NotificationRule?> GetByKindAsync(Guid id, NotificationRuleKind kind, CancellationToken ct);

    /// <summary>
    /// Toggles a rule's enabled state. Enabling a System policy stamps <c>ActivatedAt = now</c> — a
    /// per-policy watermark (see ADR 0008) that prevents the policy from immediately firing alerts
    /// for events that happened before it was turned on. Throws
    /// <see cref="KeyNotFoundException"/> if the id is unknown. Returns the updated rule.
    /// </summary>
    Task<NotificationRule> SetEnabledAsync(Guid id, bool enabled, string? updatedBy, CancellationToken ct);

    /// <summary>Enabled rules only — the set the dispatcher evaluates against.</summary>
    Task<IReadOnlyList<NotificationRule>> GetEnabledAsync(CancellationToken ct);

    Task<NotificationRule> CreateAsync(NotificationRule draft, string? updatedBy, CancellationToken ct);

    /// <summary>
    /// Replaces the rule's scalar fields + full route/target sets with those of
    /// <paramref name="draft"/>. Throws <see cref="KeyNotFoundException"/> if the id is unknown.
    /// A route whose secret is the unchanged-sentinel keeps its stored secret.
    /// </summary>
    Task UpdateAsync(Guid id, NotificationRule draft, string? updatedBy, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Decrypts a route's stored secret (for the sender), or null when none is set.</summary>
    Task<string?> GetRouteSecretAsync(Guid routeId, CancellationToken ct);
}
