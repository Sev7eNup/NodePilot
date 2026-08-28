namespace NodePilot.Core.Models;

/// <summary>
/// Immutable snapshot of a <see cref="Workflow"/> at a specific version number. A new row
/// is appended every time <c>WorkflowsController.Update</c> replaces a definition, capturing the
/// <em>previous</em> state rather than the new one, which the live row already holds.
/// Enables rollback, diff and blame.
///
/// <para>
/// The history is append-only apart from one in-place update: an explicit secret-provider
/// migration re-wraps the opaque definition envelope. Rows are removed when the parent
/// <see cref="Workflow"/> is deleted (FK cascade) or by configured retention. Rollback keeps the
/// history: restoring a prior version increments the Workflow's <c>Version</c> counter and
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
    /// <summary>
    /// At rest this contains a versioned authenticated-ciphertext envelope. Authorised history and
    /// rollback paths decrypt it before parsing. Legacy plaintext JSON remains readable during a
    /// rolling upgrade and is converted only by the explicit post-upgrade
    /// <c>secrets reencrypt</c> cutover, after every HA node supports this envelope.
    /// </summary>
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
