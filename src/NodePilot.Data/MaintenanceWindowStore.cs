using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Data;

/// <summary>
/// Default <see cref="IMaintenanceWindowStore"/>. Plain EF CRUD; updates replace the full
/// target set so the caller never has to diff. No secrets are involved, so this is much
/// simpler than <see cref="GlobalVariableStore"/> — no protector, no encode/decode.
/// </summary>
public class MaintenanceWindowStore : IMaintenanceWindowStore
{
    private readonly NodePilotDbContext _db;

    public MaintenanceWindowStore(NodePilotDbContext db) => _db = db;

    public async Task<IReadOnlyList<MaintenanceWindow>> GetAllAsync(CancellationToken ct)
        => await _db.MaintenanceWindows
            .AsNoTracking()
            .Include(w => w.Targets)
            .OrderBy(w => w.Name)
            .ToListAsync(ct);

    public async Task<MaintenanceWindow?> GetAsync(Guid id, CancellationToken ct)
        => await _db.MaintenanceWindows
            .AsNoTracking()
            .Include(w => w.Targets)
            .FirstOrDefaultAsync(w => w.Id == id, ct);

    public async Task<MaintenanceWindow> CreateAsync(MaintenanceWindow draft, string? updatedBy, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        draft.Id = Guid.NewGuid();
        draft.CreatedAt = now;
        draft.UpdatedAt = now;
        draft.UpdatedBy = updatedBy;
        draft.Targets = NormalizeTargets(draft);

        _db.MaintenanceWindows.Add(draft);
        await _db.SaveChangesAsync(ct);
        return draft;
    }

    public async Task UpdateAsync(Guid id, MaintenanceWindow draft, string? updatedBy, CancellationToken ct)
    {
        var existing = await _db.MaintenanceWindows
            .Include(w => w.Targets)
            .FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw new KeyNotFoundException($"MaintenanceWindow {id} not found");

        existing.Name = draft.Name;
        existing.Description = draft.Description;
        existing.IsEnabled = draft.IsEnabled;
        existing.Mode = draft.Mode;
        existing.ScopeKind = draft.ScopeKind;
        existing.Recurrence = draft.Recurrence;
        existing.OneTimeStartUtc = draft.OneTimeStartUtc;
        existing.OneTimeEndUtc = draft.OneTimeEndUtc;
        existing.WeeklyDaysMask = draft.WeeklyDaysMask;
        existing.WeeklyStartMinuteOfDay = draft.WeeklyStartMinuteOfDay;
        existing.WeeklyEndMinuteOfDay = draft.WeeklyEndMinuteOfDay;
        existing.CronExpression = draft.CronExpression;
        existing.DurationMinutes = draft.DurationMinutes;
        existing.TimeZoneId = draft.TimeZoneId;
        existing.DeferralPolicy = draft.DeferralPolicy;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.UpdatedBy = updatedBy;

        // Replace the full target set — caller sends the desired state, not a diff. Remove the old
        // rows and add the new ones through the DbSet directly (NOT via the navigation): targets
        // carry a client-assigned Guid key, and adding them through a tracked parent's collection
        // makes EF infer "Modified" rather than "Added", producing a phantom UPDATE that affects
        // zero rows. Explicit Add/Remove pins the intended INSERT/DELETE state.
        _db.MaintenanceWindowTargets.RemoveRange(existing.Targets);
        _db.MaintenanceWindowTargets.AddRange(NormalizeTargets(draft, existing.Id));

        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var existing = await _db.MaintenanceWindows
            .Include(w => w.Targets)
            .FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw new KeyNotFoundException($"MaintenanceWindow {id} not found");
        _db.MaintenanceWindows.Remove(existing); // tracked targets cascade-delete with the window
        await _db.SaveChangesAsync(ct);
    }

    // Global windows carry no targets; for Folders/Workflows windows we stamp fresh ids and
    // the owning-window FK. Deduped on (kind, id) — the unique index would reject collisions.
    private static List<MaintenanceWindowTarget> NormalizeTargets(MaintenanceWindow draft, Guid? windowId = null)
    {
        if (draft.ScopeKind == Core.Enums.MaintenanceScopeKind.Global || draft.Targets is null)
            return [];

        return draft.Targets
            .GroupBy(t => (t.TargetKind, t.TargetId))
            .Select(g => new MaintenanceWindowTarget
            {
                Id = Guid.NewGuid(),
                MaintenanceWindowId = windowId ?? draft.Id,
                TargetKind = g.Key.TargetKind,
                TargetId = g.Key.TargetId,
            })
            .ToList();
    }
}
