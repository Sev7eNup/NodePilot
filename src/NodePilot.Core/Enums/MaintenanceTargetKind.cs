namespace NodePilot.Core.Enums;

/// <summary>
/// Discriminator for a <see cref="NodePilot.Core.Models.MaintenanceWindowTarget"/>'s
/// soft reference. <c>TargetId</c> points at a <c>SharedWorkflowFolder.Id</c> or a
/// <c>Workflow.Id</c> depending on this value.
/// </summary>
public enum MaintenanceTargetKind
{
    Folder,
    Workflow,
}
