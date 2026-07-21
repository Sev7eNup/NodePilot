namespace NodePilot.Core.Audit;

/// <summary>
/// Writes compliance-relevant mutations to the <c>AuditLog</c> table. One call per
/// state change (create/update/delete/login). Read-only operations (GET endpoints,
/// connectivity probes, diagnostics) must NOT flow through here — an audit log that
/// captures read traffic becomes useless noise during investigations.
///
/// <para>
/// The writer is best-effort: a failure to append must never abort the mutation
/// that triggered it. Implementations catch-and-log their own errors and return
/// normally so controllers can always call this in a fire-and-forget style.
/// </para>
///
/// <para>
/// Lives in <c>NodePilot.Core</c> so non-HTTP callers (engine background services,
/// CredentialStore-style data-layer code, the scheduler) can depend on the same
/// interface as controllers — keeping every audit-write through one chokepoint that
/// applies redaction and the details-size cap. The default <see cref="IAuditWriter"/>
/// implementation in <c>NodePilot.Api</c> resolves the actor from the current
/// HTTP context; callers that don't have an HTTP context should use
/// <see cref="IAuditStager"/> and persist the resulting entry on their own DbContext.
/// </para>
/// </summary>
public interface IAuditWriter
{
    /// <summary>
    /// Append an audit entry. <paramref name="action"/> uses the verb-noun convention
    /// (<c>WORKFLOW_CREATED</c>, <c>CREDENTIAL_UPDATED</c>, <c>LOGIN_FAILED</c>, …)
    /// — stable, greppable, easy to alert on in SIEM.
    /// </summary>
    /// <param name="action">Stable action code. Convention: UPPER_SNAKE_CASE verb-noun.</param>
    /// <param name="resourceType">Optional type label (<c>Workflow</c>, <c>Machine</c>, <c>Credential</c>, <c>User</c>).</param>
    /// <param name="resourceId">Optional target entity id.</param>
    /// <param name="details">
    /// Optional JSON string with extra context. Never put secrets (passwords, API keys,
    /// raw credential bodies) in here — treat it as world-readable for Audit-role users.
    /// The implementation runs this through the redactor and caps it at 4 KiB.
    /// </param>
    /// <param name="ct">Cancellation token; honored by the underlying SaveChangesAsync.</param>
    Task LogAsync(
        string action,
        string? resourceType = null,
        Guid? resourceId = null,
        string? details = null,
        CancellationToken ct = default);
}
