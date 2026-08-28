using NodePilot.Core.Models;

namespace NodePilot.Core.Audit;

/// <summary>
/// Builds <see cref="AuditLogEntry"/> instances with the redaction and size-cap policy applied,
/// without persisting them. Use this from call sites that attach the audit row to a specific
/// <c>DbContext</c> instance, such as atomic-with-mutation writes in <c>DbAdminController</c>,
/// or that run on a background scope and persist via their own scope factory, such as
/// <c>CredentialStore.DecryptPassword</c> and <c>TriggerOrchestrator.AppendSuppressionAudit</c>.
///
/// <para>
/// The HTTP-flow <see cref="IAuditWriter"/> uses the stager internally as well, so every audit
/// row in the system passes through the same redaction and cap pipeline.
/// </para>
/// </summary>
public interface IAuditStager
{
    /// <summary>
    /// Constructs an <see cref="AuditLogEntry"/>: assigns a fresh Id, stamps
    /// <see cref="DateTime.UtcNow"/>, runs <paramref name="details"/> through the configured
    /// redactor and truncates Details to the size cap. The caller adds the returned entry
    /// to a DbContext and saves it.
    /// </summary>
    AuditLogEntry Build(
        string action,
        AuditActor actor,
        string? resourceType = null,
        Guid? resourceId = null,
        string? details = null);
}
