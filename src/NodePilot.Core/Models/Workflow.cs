namespace NodePilot.Core.Models;

public class Workflow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DefinitionJson { get; set; } = "{}";
    public int Version { get; set; } = 1;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>
    /// Stable principal whose permissions govern trigger-driven cross-folder sub-workflow
    /// calls. Set only by Publish; routine moves, locks and enable/disable operations must
    /// not silently change runtime authority.
    /// </summary>
    public Guid? PublishedByUserId { get; set; }

    // Pre-computed from DefinitionJson to avoid parsing on every list/dashboard request.
    // Populated on Create/Update/Publish/Import; null for rows written before this column existed.
    public string? TriggerTypesJson { get; set; }
    public int ActivityCount { get; set; }

    // Edit-Lock (SCOrch-style): when set, only the lock-owner may mutate the workflow.
    // `IsEnabled` is forced to false on lock — Enable while locked is rejected with 423,
    // because a partially-edited workflow must not fire triggers. CheckedOutAt powers the
    // "Locked by Alice (15min ago)"-style UI hints. Both clear together on unlock.
    public Guid? CheckedOutByUserId { get; set; }
    public DateTime? CheckedOutAt { get; set; }

    /// <summary>
    /// SharedWorkflowFolder this workflow lives in for RBAC purposes. NOT NULL after the
    /// AddSharedWorkflowFolders migration — every workflow belongs to exactly one folder,
    /// defaulting to <see cref="SharedWorkflowFolder.RootFolderId"/>. The folder governs
    /// who can read/run/edit the workflow via <see cref="SharedFolderPermission"/> grants
    /// (with inheritance down the tree).
    /// </summary>
    public Guid FolderId { get; set; } = SharedWorkflowFolder.RootFolderId;
    public SharedWorkflowFolder? Folder { get; set; }

    public ICollection<WorkflowExecution> Executions { get; set; } = [];
}
