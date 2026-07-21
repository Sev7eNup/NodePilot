using NodePilot.Core.Models;

namespace NodePilot.Core.Audit;

/// <summary>
/// Builds <see cref="AuditLogEntry"/> instances with the redaction + size-cap policy
/// applied — without persisting them. Use this from call sites that need to attach the
/// audit row to a specific <c>DbContext</c> instance (e.g. atomic-with-mutation writes
/// in <c>DbAdminController</c>) or that run on a background scope and persist via their
/// own scope factory (e.g. <c>CredentialStore.DecryptPassword</c>,
/// <c>TriggerOrchestrator.AppendSuppressionAudit</c>).
///
/// <para>
/// The HTTP-flow <see cref="IAuditWriter"/> uses the stager internally too, so every
/// audit row in the system goes through the same redaction + cap pipeline. That's the
/// whole point of this abstraction — the previous bypass paths skipped both protections.
/// </para>
/// </summary>
public interface IAuditStager
{
    /// <summary>
    /// Constructs an <see cref="AuditLogEntry"/>: assigns a fresh Id, stamps
    /// <see cref="DateTime.UtcNow"/>, runs <paramref name="details"/> through the
    /// configured redactor, and truncates Details to the size cap. The caller adds the
    /// returned entry to a DbContext and saves it.
    /// </summary>
    AuditLogEntry Build(
        string action,
        AuditActor actor,
        string? resourceType = null,
        Guid? resourceId = null,
        string? details = null);
}
