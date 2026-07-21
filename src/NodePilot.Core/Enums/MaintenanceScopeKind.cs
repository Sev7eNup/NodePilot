namespace NodePilot.Core.Enums;

/// <summary>
/// What a <see cref="NodePilot.Core.Models.MaintenanceWindow"/> applies to. Exactly ONE
/// kind per window — for mixed coverage, compose multiple windows.
/// </summary>
public enum MaintenanceScopeKind
{
    /// <summary>Every workflow. The window's Targets collection is empty and ignored.</summary>
    Global,

    /// <summary>
    /// The folders listed in Targets (TargetKind=Folder) and ALL their descendant folders.
    /// Folder membership resolves via <c>ParentFolderId</c> traversal, not the display path.
    /// </summary>
    Folders,

    /// <summary>The individual workflows listed in Targets (TargetKind=Workflow).</summary>
    Workflows,
}
