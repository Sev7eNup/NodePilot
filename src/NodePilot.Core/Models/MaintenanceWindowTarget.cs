using NodePilot.Core.Enums;

namespace NodePilot.Core.Models;

/// <summary>
/// One target of a <see cref="MaintenanceWindow"/> whose <c>ScopeKind</c> is
/// <see cref="MaintenanceScopeKind.Folders"/> or <see cref="MaintenanceScopeKind.Workflows"/>.
///
/// <para><see cref="TargetId"/> is a <b>soft</b> reference (no hard FK) to either a
/// <see cref="SharedWorkflowFolder"/> or a <see cref="Workflow"/>, depending on
/// <see cref="TargetKind"/>. The evaluator tolerates dangling ids — deleting the referenced
/// folder/workflow simply makes this row a no-op. The only real FK is to the owning window,
/// and it cascades on delete so deleting a window cleans up its targets.</para>
/// </summary>
public class MaintenanceWindowTarget
{
    public Guid Id { get; set; }

    public Guid MaintenanceWindowId { get; set; }

    public MaintenanceTargetKind TargetKind { get; set; }

    /// <summary>Id of the targeted <see cref="SharedWorkflowFolder"/> or <see cref="Workflow"/>.</summary>
    public Guid TargetId { get; set; }
}
