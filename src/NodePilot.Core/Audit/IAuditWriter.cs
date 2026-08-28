namespace NodePilot.Core.Audit;

/// <summary>
/// Writes compliance-relevant mutations to the <c>AuditLog</c> table. One call per state
/// change (create, update, delete, login). Read-only operations such as GET endpoints,
/// connectivity probes and diagnostics must not flow through here, because read traffic
/// would bury the entries that matter during an investigation.
///
/// <para>
/// Best-effort: a failed append must never abort the mutation that triggered it.
/// Implementations catch and log their own errors and return normally, so callers can
/// use this fire-and-forget.
/// </para>
///
/// <para>
/// Lives in <c>NodePilot.Core</c> so non-HTTP callers (engine background services,
/// data-layer code, the scheduler) share the one chokepoint that applies redaction and the
/// details-size cap. The default <see cref="IAuditWriter"/> implementation in
/// <c>NodePilot.Api</c> resolves the actor from the current HTTP context; callers without
/// an HTTP context use <see cref="IAuditStager"/> and persist the resulting entry on their
/// own DbContext.
/// </para>
/// </summary>
public interface IAuditWriter
{
    /// <summary>
    /// Append an audit entry. <paramref name="action"/> uses the verb-noun convention
    /// (<c>WORKFLOW_CREATED</c>, <c>CREDENTIAL_UPDATED</c>, <c>LOGIN_FAILED</c>): stable,
    /// greppable, and easy to alert on in a SIEM.
    /// </summary>
    /// <param name="action">Stable action code. Convention: UPPER_SNAKE_CASE verb-noun.</param>
    /// <param name="resourceType">
    /// Optional type label, e.g. <c>Workflow</c>, <c>Machine</c>, <c>Credential</c>, <c>User</c>.
    /// </param>
    /// <param name="resourceId">Optional target entity id.</param>
    /// <param name="details">
    /// Optional JSON string with extra context. Never put secrets (passwords, API keys, raw
    /// credential bodies) in here; it is readable by every user with the Audit role. The
    /// implementation runs it through the redactor and caps it at 4 KiB.
    /// </param>
    /// <param name="ct">Cancellation token; honored by the underlying SaveChangesAsync.</param>
    Task LogAsync(
        string action,
        string? resourceType = null,
        Guid? resourceId = null,
        string? details = null,
        CancellationToken ct = default);
}
