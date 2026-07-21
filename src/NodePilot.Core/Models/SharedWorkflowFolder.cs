namespace NodePilot.Core.Models;

/// <summary>
/// Org-level shared folder for grouping workflows under role-based access control.
/// Every <see cref="Workflow"/> is assigned to exactly one
/// <see cref="SharedWorkflowFolder"/> via <see cref="Workflow.FolderId"/>;
/// folder permissions in <see cref="SharedFolderPermission"/> govern who can read, run,
/// edit, or administer the workflows underneath.
/// </summary>
public class SharedWorkflowFolder
{
    public Guid Id { get; set; }

    /// <summary>
    /// Parent folder. <c>null</c> only for the singleton Root folder. Multiple parent-less
    /// rows are not supported — there is exactly one Root, identified by
    /// <see cref="RootFolderId"/>.
    /// </summary>
    public Guid? ParentFolderId { get; set; }

    /// <summary>
    /// Display name. Sibling-unique within a parent (enforced by
    /// <c>unique(ParentFolderId, Name)</c> in <c>NodePilotDbContext.OnModelCreating</c>).
    /// Folder rename is allowed; permission lookups use <see cref="Id"/>, not name, so
    /// renames do not break ACLs.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Materialized display path for UI + audit, e.g. <c>/finance/reports</c>. Updated
    /// when this folder or any ancestor is renamed/moved. Never used for permission
    /// resolution — that goes through parent-id traversal of <see cref="SharedFolderPermission"/>.
    /// </summary>
    public string Path { get; set; } = "/";

    /// <summary>
    /// Depth from Root (Root = 0). Hard-capped at <see cref="MaxDepth"/> to keep
    /// permission traversal bounded and prevent pathological tree shapes.
    /// </summary>
    public int Depth { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }

    public ICollection<SharedFolderPermission> Permissions { get; set; } = [];

    /// <summary>
    /// Stable Guid for the singleton Root folder. Hard-coded so a fresh DB and any later
    /// migration share the same Root id — application code can reference it without a
    /// lookup query.
    /// </summary>
    public static readonly Guid RootFolderId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>
    /// Maximum tree depth. Root counts as 0; a folder under Root is depth 1; max allowed
    /// child depth is 5. Enforced in the create/move endpoints, not at the schema level.
    /// </summary>
    public const int MaxDepth = 5;
}
