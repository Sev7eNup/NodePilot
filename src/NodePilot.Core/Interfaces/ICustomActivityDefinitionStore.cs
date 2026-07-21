using NodePilot.Core.Models;

namespace NodePilot.Core.Interfaces;

/// <summary>
/// Editable surface of a <see cref="CustomActivityDefinition"/>. <see cref="Key"/> is honored only
/// on create (immutable thereafter); <see cref="IsEnabled"/> is not here because enable/disable is a
/// separate Admin-only operation.
/// </summary>
public sealed record CustomActivityDefinitionInput
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string Icon { get; init; } = "extension";
    public string? Color { get; init; }
    public required string ScriptTemplate { get; init; }
    public string Engine { get; init; } = "auto";
    public bool RunsRemote { get; init; }
    public bool Isolated { get; init; }
    public int? MemoryLimitMb { get; init; }
    public int? MaxProcesses { get; init; }
    public int? DefaultTimeoutSeconds { get; init; }
    public string? SuccessExitCodes { get; init; }
    public string InputParametersJson { get; init; } = "[]";
    public string OutputParametersJson { get; init; } = "[]";
    public string? ChangeNote { get; init; }
}

/// <summary>Thrown by the store when a mutation carries a stale <see cref="CustomActivityDefinition.ConcurrencyToken"/>.</summary>
public sealed class CustomActivityConcurrencyException(string message) : Exception(message);

/// <summary>
/// Persistence for user-authored custom activities. Tombstoned (soft-deleted) rows are excluded
/// from every read here; the executor treats a missing/disabled definition as a clean step failure.
/// </summary>
public interface ICustomActivityDefinitionStore
{
    /// <summary>All non-deleted definitions, ordered by name. <paramref name="includeDisabled"/>=false returns only enabled (palette/catalog).</summary>
    Task<IReadOnlyList<CustomActivityDefinition>> GetAllAsync(bool includeDisabled, CancellationToken ct);

    Task<CustomActivityDefinition?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<CustomActivityDefinition?> GetByKeyAsync(string key, CancellationToken ct);

    /// <summary>Inserts a new definition. Always created disabled (Draft); the key must be unique among non-deleted rows.</summary>
    Task<CustomActivityDefinition> CreateAsync(CustomActivityDefinitionInput input, string? createdBy, CancellationToken ct);

    /// <summary>
    /// Updates the live row: snapshots the previous state into a version row, bumps the counter and
    /// regenerates the concurrency token. Throws <see cref="CustomActivityConcurrencyException"/> on a
    /// stale token, <see cref="KeyNotFoundException"/> if missing/deleted.
    /// </summary>
    Task<CustomActivityDefinition> UpdateAsync(Guid id, CustomActivityDefinitionInput input, Guid expectedConcurrencyToken, string? updatedBy, CancellationToken ct);

    /// <summary>Admin-only enable/disable. Bumps the concurrency token.</summary>
    Task SetEnabledAsync(Guid id, bool enabled, string? updatedBy, CancellationToken ct);

    /// <summary>Soft-delete (tombstone) — keeps script+versions resolvable for old executions/audit.</summary>
    Task SoftDeleteAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<CustomActivityDefinitionVersion>> GetVersionsAsync(Guid id, CancellationToken ct);

    /// <summary>Restores a prior snapshot as a new live version (does not purge history).</summary>
    Task<CustomActivityDefinition> RollbackAsync(Guid id, int version, string? updatedBy, CancellationToken ct);

    /// <summary>Count of non-deleted definitions (for backup manifest).</summary>
    Task<int> CountAsync(CancellationToken ct);
}
