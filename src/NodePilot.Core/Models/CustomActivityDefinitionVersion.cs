namespace NodePilot.Core.Models;

/// <summary>
/// Immutable snapshot of a <see cref="CustomActivityDefinition"/> at a specific version number.
/// Every update appends a row holding the previous state, since the live row already holds the
/// new one, mirroring <see cref="WorkflowVersion"/>. This enables rollback and blame. The table
/// is append-only: a rollback restores an earlier snapshot by bumping the live counter and
/// writing a fresh snapshot, so the roll-forward stays auditable.
///
/// <para>
/// Executions persist only the key, version and hash that ran (see
/// <c>StepExecution.CustomActivity*</c>), so these snapshots are the only place where the script
/// of a past run survives a later edit. Deleting a definition is therefore a soft tombstone
/// rather than a hard delete.
/// </para>
/// </summary>
public class CustomActivityDefinitionVersion
{
    public Guid Id { get; set; }
    public Guid DefinitionId { get; set; }

    /// <summary>The version number this row snapshots; matches
    /// <see cref="CustomActivityDefinition.Version"/> at write time.</summary>
    public int Version { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Icon { get; set; } = "extension";
    public string? Color { get; set; }
    public string ScriptTemplate { get; set; } = string.Empty;
    public string Engine { get; set; } = "auto";
    public bool RunsRemote { get; set; }
    public bool Isolated { get; set; }
    public int? MemoryLimitMb { get; set; }
    public int? MaxProcesses { get; set; }
    public int? DefaultTimeoutSeconds { get; set; }
    public string? SuccessExitCodes { get; set; }
    public string InputParametersJson { get; set; } = "[]";
    public string OutputParametersJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public string? ChangeNote { get; set; }

    public CustomActivityDefinition Definition { get; set; } = null!;
}
