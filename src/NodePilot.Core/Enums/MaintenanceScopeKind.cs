namespace NodePilot.Core.Enums;

/// <summary>
/// What a <see cref="NodePilot.Core.Models.MaintenanceWindow"/> applies to. A window has exactly
/// one kind; compose several windows for mixed coverage.
/// </summary>
public enum MaintenanceScopeKind
{
    /// <summary>Every workflow. The window's Targets collection is empty and ignored.</summary>
    Global,

    /// <summary>
    /// The folders listed in Targets (TargetKind=Folder) and all their descendant folders.
    /// Membership resolves by <c>ParentFolderId</c> traversal, not by the display path.
    /// </summary>
    Folders,

    /// <summary>The individual workflows listed in Targets (TargetKind=Workflow).</summary>
    Workflows,
}
