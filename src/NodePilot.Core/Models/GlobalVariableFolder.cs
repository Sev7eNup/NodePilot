namespace NodePilot.Core.Models;

/// <summary>
/// Folder for grouping <see cref="GlobalVariable"/> rows in the UI. A global is identified in
/// <c>{{globals.NAME}}</c> templates by its bare, globally unique
/// <see cref="GlobalVariable.Name"/>, so folders never namespace a variable and moving one does
/// not change how it resolves. Every <see cref="GlobalVariable"/> belongs to exactly one folder
/// via <see cref="GlobalVariable.FolderId"/>, defaulting to the singleton Root.
///
/// <para>
/// The shape mirrors <see cref="SharedWorkflowFolder"/> (self-referencing
/// <see cref="ParentFolderId"/>, materialized <see cref="Path"/>, <see cref="Depth"/>, one
/// singleton Root) but has no per-folder RBAC: global-variable management is Admin-gated as a
/// whole, so there is no <c>Permissions</c> collection and no resource-authorization traversal.
/// </para>
/// </summary>
public class GlobalVariableFolder
{
    public Guid Id { get; set; }

    /// <summary>
    /// Parent folder. <c>null</c> only for the singleton Root folder (identified by
    /// <see cref="RootFolderId"/>). Sibling names are unique within a parent.
    /// </summary>
    public Guid? ParentFolderId { get; set; }

    /// <summary>Display name. Sibling-unique within a parent (enforced by a unique index).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Materialized display path for UI and audit, e.g. <c>/environment/prod</c>. Recomputed for
    /// the whole subtree on rename or move. Not used for lookup; resolution is by variable Name.
    /// </summary>
    public string Path { get; set; } = "/";

    /// <summary>Depth from Root (Root = 0). Capped at <see cref="MaxDepth"/>.</summary>
    public int Depth { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }

    /// <summary>
    /// Stable Guid of the singleton Root folder, distinct from
    /// <see cref="SharedWorkflowFolder.RootFolderId"/>. Hard-coded so a seeded database and a
    /// migrated one share the same Root id and application code can reference it without a
    /// lookup.
    /// </summary>
    public static readonly Guid RootFolderId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    /// <summary>Maximum tree depth. Root = 0; enforced in the create/move endpoints.</summary>
    public const int MaxDepth = 5;
}
