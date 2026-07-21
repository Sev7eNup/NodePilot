namespace NodePilot.Core.Models;

/// <summary>
/// Immutable snapshot of a <see cref="Workflow"/> at a specific version number. A new row
/// is appended every time <c>WorkflowsController.Update</c> replaces a definition (the
/// <em>previous</em> state is captured, not the new one — the live row already holds that).
/// Enables rollback + diff + blame ("who changed step X from A to B, and when?").
///
/// <para>
/// The table is append-only; there is no update path. Rows are removed only when the
/// parent <see cref="Workflow"/> is deleted (FK cascade). Rollback does not purge history
/// — restoring a prior version increments the Workflow's <c>Version</c> counter and
/// emits a fresh snapshot so the roll-forward remains auditable.
/// </para>
/// </summary>
public class WorkflowVersion
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }

    /// <summary>
    /// The version number this row snapshots. Matches <see cref="Workflow.Version"/> at
    /// the moment this row was written — so <c>Version=3</c> means "this is what the
    /// workflow looked like while its live row had Version=3".
    /// </summary>
    public int Version { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DefinitionJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Username of the editor who created the version this row captures.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Short human-readable note on why this revision was made. Usually NULL — only filled
    /// on rollback ("Rolled back to version 3") or when the editor explicitly passes one.
    /// </summary>
    public string? ChangeNote { get; set; }

    public Workflow Workflow { get; set; } = null!;
}
