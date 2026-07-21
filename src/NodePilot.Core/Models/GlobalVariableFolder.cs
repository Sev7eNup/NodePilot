namespace NodePilot.Core.Models;

/// <summary>
/// Organizational folder for grouping <see cref="GlobalVariable"/> rows in the UI. Purely
/// cosmetic: a global's identity in <c>{{globals.NAME}}</c> templates is its bare, globally
/// unique <see cref="GlobalVariable.Name"/> — folders never namespace a variable, so moving a
/// variable between folders does not change how it resolves. Every <see cref="GlobalVariable"/>
/// belongs to exactly one folder via <see cref="GlobalVariable.FolderId"/> (default: the
/// singleton Root).
///
/// <para>
/// Structurally a mirror of <see cref="SharedWorkflowFolder"/> (self-referencing
/// <see cref="ParentFolderId"/> + materialized <see cref="Path"/> + <see cref="Depth"/>, one
/// singleton Root), but deliberately <b>without</b> the per-folder RBAC layer — global-variable
/// management is Admin-gated wholesale, so there is no <c>Permissions</c> collection and no
/// resource-authorization traversal.
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
    /// Materialized display path for UI + audit, e.g. <c>/environment/prod</c>. Recomputed for
    /// the whole subtree on rename/move. Never used for lookup — resolution is by variable Name.
    /// </summary>
    public string Path { get; set; } = "/";

    /// <summary>Depth from Root (Root = 0). Capped at <see cref="MaxDepth"/> in the endpoints.</summary>
    public int Depth { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }

    /// <summary>
    /// Stable Guid for the singleton Root folder. Distinct from
    /// <see cref="SharedWorkflowFolder.RootFolderId"/> (…0001) to avoid confusion — this is
    /// …0002. Hard-coded so a fresh DB (EnsureCreated seed) and a migrated DB (InsertData) share
    /// the same Root id, and application code can reference it without a lookup.
    /// </summary>
    public static readonly Guid RootFolderId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    /// <summary>Maximum tree depth. Root = 0; enforced in the create/move endpoints.</summary>
    public const int MaxDepth = 5;
}
