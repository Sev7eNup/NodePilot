using NodePilot.Core.Models;

namespace NodePilot.Core.Audit;

/// <summary>
/// Default <see cref="IAuditStager"/>. Applies redaction through the registered
/// <see cref="IAuditDetailsRedactor"/> and the size cap on Details, then returns the entry
/// without touching a DbContext. The companion <c>AuditWriter</c> in <c>NodePilot.Api</c> adds
/// HttpContext-derived actor resolution and SaveChangesAsync persistence; non-HTTP callers
/// such as CredentialStore, TriggerOrchestrator and DbAdminController persist on their own.
/// </summary>
public sealed class AuditStager : IAuditStager
{
    // 4 KiB holds a structured delta for a single mutation. Larger payloads usually mean a
    // whole workflow definition, which belongs in WorkflowVersions rather than AuditLog.
    public const int MaxDetailsChars = 4096;
    public const string TruncationMarker = "...[truncated]";

    private readonly IAuditDetailsRedactor? _redactor;

    public AuditStager(IAuditDetailsRedactor? redactor = null)
    {
        _redactor = redactor;
    }

    public AuditLogEntry Build(
        string action,
        AuditActor actor,
        string? resourceType = null,
        Guid? resourceId = null,
        string? details = null)
    {
        var redacted = _redactor?.Redact(details) ?? details;
        var capped = Truncate(redacted);

        return new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            UserId = actor.UserId,
            Username = actor.Username,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Details = capped,
            IpAddress = actor.IpAddress,
        };
    }

    private static string? Truncate(string? s)
    {
        if (s is null || s.Length <= MaxDetailsChars) return s;
        var keep = MaxDetailsChars - TruncationMarker.Length;
        if (keep <= 0) return TruncationMarker;
        return s.Substring(0, keep) + TruncationMarker;
    }
}
