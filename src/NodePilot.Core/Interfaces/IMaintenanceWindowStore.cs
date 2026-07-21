using NodePilot.Core.Models;

namespace NodePilot.Core.Interfaces;

/// <summary>
/// CRUD persistence for <see cref="MaintenanceWindow"/> and its child
/// <see cref="MaintenanceWindowTarget"/> rows. Pure storage — the time/scope matching logic
/// lives in <see cref="IMaintenanceWindowEvaluator"/>.
/// </summary>
public interface IMaintenanceWindowStore
{
    /// <summary>All windows with their targets, ordered by name. Includes disabled windows.</summary>
    Task<IReadOnlyList<MaintenanceWindow>> GetAllAsync(CancellationToken ct);

    Task<MaintenanceWindow?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Persists a new window. The caller supplies a populated draft (including
    /// <see cref="MaintenanceWindow.Targets"/>); the store assigns ids and stamps audit fields.
    /// </summary>
    Task<MaintenanceWindow> CreateAsync(MaintenanceWindow draft, string? updatedBy, CancellationToken ct);

    /// <summary>
    /// Replaces the window's scalar fields and its full target set with those of
    /// <paramref name="draft"/>. Throws <see cref="KeyNotFoundException"/> if the id is unknown.
    /// </summary>
    Task UpdateAsync(Guid id, MaintenanceWindow draft, string? updatedBy, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);
}
