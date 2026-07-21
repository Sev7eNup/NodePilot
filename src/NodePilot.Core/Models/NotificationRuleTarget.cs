using NodePilot.Core.Enums;

namespace NodePilot.Core.Models;

/// <summary>
/// One scope target of a <see cref="NotificationRule"/> whose <c>ScopeKind</c> is
/// <see cref="NotificationScopeKind.Folders"/> or <see cref="NotificationScopeKind.Workflows"/>.
/// <see cref="TargetId"/> is a soft reference (no hard FK) to a <see cref="SharedWorkflowFolder"/>
/// or <see cref="Workflow"/>; a dangling id simply makes the row a no-op. The only real FK is to
/// the owning rule and cascades on delete. Mirrors <see cref="MaintenanceWindowTarget"/>.
/// </summary>
public class NotificationRuleTarget
{
    public Guid Id { get; set; }
    public Guid NotificationRuleId { get; set; }
    public NotificationTargetKind TargetKind { get; set; }
    public Guid TargetId { get; set; }
}
