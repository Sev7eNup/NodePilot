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
    /// Maximum executions of this workflow that may run at once, across every caller — manual
    /// runs, triggers, webhooks and sub-workflow invocations alike. Null means unlimited.
    /// Reaching the limit queues further runs rather than rejecting them.
    /// <para>
    /// Operational, not part of the versioned definition: a rollback of the graph must not
    /// silently change a capacity guard, which is why <c>WorkflowVersion</c> does not carry it.
    /// </para>
    /// </summary>
    public int? MaxConcurrentExecutions { get; set; }

    /// <summary>
    /// Stable principal whose permissions govern trigger-driven cross-folder sub-workflow
    /// calls. Set only by Publish; routine moves, locks and enable/disable operations must
    /// not silently change runtime authority.
    /// </summary>
    public Guid? PublishedByUserId { get; set; }

    // Pre-computed from DefinitionJson to avoid parsing on every list/dashboard request.
    // Populated on Create, Update, Publish, and Import; null when no cached value exists.
    public string? TriggerTypesJson { get; set; }
    public int ActivityCount { get; set; }

    // Edit-Lock (SCOrch-style): when set, only the lock owner may mutate the workflow, and
    // IsEnabled is forced to false, since a partially-edited workflow must not fire triggers.
    // CheckedOutAt powers "Locked by Alice (15min ago)"-style UI hints; both clear on unlock.
    public Guid? CheckedOutByUserId { get; set; }
    public DateTime? CheckedOutAt { get; set; }

    /// <summary>
    /// SharedWorkflowFolder this workflow lives in for RBAC purposes. Every workflow belongs to
    /// exactly one folder, defaulting to <see cref="SharedWorkflowFolder.RootFolderId"/>. The
    /// folder governs who can read, run, or edit it via
    /// <see cref="SharedFolderPermission"/> grants.
    /// </summary>
    public Guid FolderId { get; set; } = SharedWorkflowFolder.RootFolderId;
    public SharedWorkflowFolder? Folder { get; set; }

    public ICollection<WorkflowExecution> Executions { get; set; } = [];
}
