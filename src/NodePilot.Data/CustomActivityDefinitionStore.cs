using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Data;

/// <summary>
/// Default <see cref="ICustomActivityDefinitionStore"/>. Soft-delete semantics: tombstoned rows are
/// invisible to every read but retained so old executions stay reproducible. Key-uniqueness-among-
/// live and optimistic concurrency are enforced here (the latter via a regenerated
/// <see cref="CustomActivityDefinition.ConcurrencyToken"/>, a provider-agnostic stand-in for a SQL
/// rowversion). Every update snapshots the previous state into the version table, mirroring
/// <see cref="WorkflowVersion"/>.
/// </summary>
public sealed class CustomActivityDefinitionStore(NodePilotDbContext db) : ICustomActivityDefinitionStore
{
    public async Task<IReadOnlyList<CustomActivityDefinition>> GetAllAsync(bool includeDisabled, CancellationToken ct)
    {
        var q = db.CustomActivityDefinitions.AsNoTracking().Where(x => !x.IsDeleted);
        if (!includeDisabled) q = q.Where(x => x.IsEnabled);
        return await q.OrderBy(x => x.Name).ToListAsync(ct);
    }

    public async Task<CustomActivityDefinition?> GetByIdAsync(Guid id, CancellationToken ct) =>
        await db.CustomActivityDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);

    public async Task<CustomActivityDefinition?> GetByKeyAsync(string key, CancellationToken ct) =>
        await db.CustomActivityDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == key && !x.IsDeleted, ct);

    public async Task<CustomActivityDefinition> CreateAsync(CustomActivityDefinitionInput input, string? createdBy, CancellationToken ct)
    {
        var exists = await db.CustomActivityDefinitions
            .AnyAsync(x => x.Key == input.Key && !x.IsDeleted, ct);
        if (exists)
            throw new InvalidOperationException($"A custom activity with key '{input.Key}' already exists.");

        var now = DateTime.UtcNow;
        var def = new CustomActivityDefinition
        {
            Id = Guid.NewGuid(),
            Key = input.Key,
            IsEnabled = false, // created as Draft; enabling is a separate Admin action
            Version = 1,
            ConcurrencyToken = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = createdBy,
            UpdatedBy = createdBy,
        };
        ApplyInput(def, input);
        db.CustomActivityDefinitions.Add(def);
        await db.SaveChangesAsync(ct);
        return def;
    }

    public async Task<CustomActivityDefinition> UpdateAsync(
        Guid id, CustomActivityDefinitionInput input, Guid expectedConcurrencyToken, string? updatedBy, CancellationToken ct)
    {
        var def = await LoadLiveAsync(id, ct);
        if (def.ConcurrencyToken != expectedConcurrencyToken)
            throw new CustomActivityConcurrencyException(
                "The custom activity was modified by someone else. Reload and re-apply your changes.");

        SnapshotCurrent(def);          // capture previous state under its current version number
        ApplyInput(def, input);         // Key is immutable — ApplyInput does not touch it
        def.Version += 1;
        def.ChangeNote = input.ChangeNote;
        def.ConcurrencyToken = Guid.NewGuid();
        def.UpdatedAt = DateTime.UtcNow;
        def.UpdatedBy = updatedBy;
        await db.SaveChangesAsync(ct);
        return def;
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, string? updatedBy, CancellationToken ct)
    {
        var def = await LoadLiveAsync(id, ct);
        def.IsEnabled = enabled;
        def.ConcurrencyToken = Guid.NewGuid();
        def.UpdatedAt = DateTime.UtcNow;
        def.UpdatedBy = updatedBy;
        await db.SaveChangesAsync(ct);
    }

    public async Task SoftDeleteAsync(Guid id, CancellationToken ct)
    {
        var def = await LoadLiveAsync(id, ct);
        def.IsDeleted = true;
        def.IsEnabled = false; // drop out of catalog/palette immediately
        def.DeletedAt = DateTime.UtcNow;
        def.ConcurrencyToken = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<CustomActivityDefinitionVersion>> GetVersionsAsync(Guid id, CancellationToken ct) =>
        await db.CustomActivityDefinitionVersions.AsNoTracking()
            .Where(v => v.DefinitionId == id)
            .OrderByDescending(v => v.Version)
            .ToListAsync(ct);

    public async Task<CustomActivityDefinition> RollbackAsync(Guid id, int version, string? updatedBy, CancellationToken ct)
    {
        var def = await LoadLiveAsync(id, ct);
        var snap = await db.CustomActivityDefinitionVersions
            .FirstOrDefaultAsync(v => v.DefinitionId == id && v.Version == version, ct)
            ?? throw new KeyNotFoundException($"Custom activity {id} has no version {version}.");

        SnapshotCurrent(def);
        def.Name = snap.Name;
        def.Description = snap.Description;
        def.Icon = snap.Icon;
        def.Color = snap.Color;
        def.ScriptTemplate = snap.ScriptTemplate;
        def.Engine = snap.Engine;
        def.RunsRemote = snap.RunsRemote;
        def.Isolated = snap.Isolated;
        def.MemoryLimitMb = snap.MemoryLimitMb;
        def.MaxProcesses = snap.MaxProcesses;
        def.DefaultTimeoutSeconds = snap.DefaultTimeoutSeconds;
        def.SuccessExitCodes = snap.SuccessExitCodes;
        def.InputParametersJson = snap.InputParametersJson;
        def.OutputParametersJson = snap.OutputParametersJson;
        def.Version += 1;
        def.ChangeNote = $"Rolled back to version {version}";
        def.ConcurrencyToken = Guid.NewGuid();
        def.UpdatedAt = DateTime.UtcNow;
        def.UpdatedBy = updatedBy;
        await db.SaveChangesAsync(ct);
        return def;
    }

    public async Task<int> CountAsync(CancellationToken ct) =>
        await db.CustomActivityDefinitions.CountAsync(x => !x.IsDeleted, ct);

    private async Task<CustomActivityDefinition> LoadLiveAsync(Guid id, CancellationToken ct) =>
        await db.CustomActivityDefinitions.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct)
            ?? throw new KeyNotFoundException($"Custom activity {id} not found.");

    /// <summary>Appends a version row capturing the live row's CURRENT state under its current version number.</summary>
    private void SnapshotCurrent(CustomActivityDefinition def) =>
        db.CustomActivityDefinitionVersions.Add(new CustomActivityDefinitionVersion
        {
            Id = Guid.NewGuid(),
            DefinitionId = def.Id,
            Version = def.Version,
            Name = def.Name,
            Description = def.Description,
            Icon = def.Icon,
            Color = def.Color,
            ScriptTemplate = def.ScriptTemplate,
            Engine = def.Engine,
            RunsRemote = def.RunsRemote,
            Isolated = def.Isolated,
            MemoryLimitMb = def.MemoryLimitMb,
            MaxProcesses = def.MaxProcesses,
            DefaultTimeoutSeconds = def.DefaultTimeoutSeconds,
            SuccessExitCodes = def.SuccessExitCodes,
            InputParametersJson = def.InputParametersJson,
            OutputParametersJson = def.OutputParametersJson,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = def.UpdatedBy,
            ChangeNote = def.ChangeNote,
        });

    /// <summary>Copies editable fields from the input onto the entity. Never touches the immutable Key.</summary>
    private static void ApplyInput(CustomActivityDefinition def, CustomActivityDefinitionInput input)
    {
        def.Name = input.Name;
        def.Description = input.Description;
        def.Icon = input.Icon;
        def.Color = input.Color;
        def.ScriptTemplate = input.ScriptTemplate;
        def.Engine = input.Engine;
        def.RunsRemote = input.RunsRemote;
        def.Isolated = input.Isolated;
        def.MemoryLimitMb = input.MemoryLimitMb;
        def.MaxProcesses = input.MaxProcesses;
        def.DefaultTimeoutSeconds = input.DefaultTimeoutSeconds;
        def.SuccessExitCodes = input.SuccessExitCodes;
        def.InputParametersJson = input.InputParametersJson;
        def.OutputParametersJson = input.OutputParametersJson;
    }
}
