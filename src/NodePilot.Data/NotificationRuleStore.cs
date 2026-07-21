using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Data;

/// <summary>
/// Default <see cref="INotificationRuleStore"/>. EF CRUD with route-secret encryption via
/// <see cref="ISecretProtector"/> (like <see cref="GlobalVariableStore"/>). Routes are <b>diffed</b>
/// on update (matched by id, updated in place) rather than removed-and-re-added: keeping a route's
/// id — required so an <see cref="UnchangedSecret"/> round-trip preserves the stored cipher — would
/// otherwise collide in the EF change tracker. Deleting a rule also clears its transient
/// suppression + delivery-attempt state (those carry no rule FK to avoid multiple-cascade-paths).
/// </summary>
public class NotificationRuleStore : INotificationRuleStore
{
    /// <summary>Sentinel a caller sends to mean "keep the existing route secret".</summary>
    public const string UnchangedSecret = "__unchanged__";

    private readonly NodePilotDbContext _db;
    private readonly ISecretProtector _protector;

    [ActivatorUtilitiesConstructor]
    public NotificationRuleStore(NodePilotDbContext db, ISecretProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    public async Task<IReadOnlyList<NotificationRule>> GetAllAsync(CancellationToken ct)
        => await _db.NotificationRules.AsNoTracking()
            .Include(r => r.Routes.OrderBy(x => x.Order)).Include(r => r.Targets)
            .OrderBy(r => r.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<NotificationRule>> GetAllByKindAsync(NotificationRuleKind kind, CancellationToken ct)
        => await _db.NotificationRules.AsNoTracking()
            .Include(r => r.Routes.OrderBy(x => x.Order)).Include(r => r.Targets)
            .Where(r => r.Kind == kind)
            .OrderBy(r => r.Name).ToListAsync(ct);

    public async Task<NotificationRule?> GetAsync(Guid id, CancellationToken ct)
        => await _db.NotificationRules.AsNoTracking()
            .Include(r => r.Routes.OrderBy(x => x.Order)).Include(r => r.Targets)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<NotificationRule?> GetByKindAsync(Guid id, NotificationRuleKind kind, CancellationToken ct)
        => await _db.NotificationRules.AsNoTracking()
            .Include(r => r.Routes.OrderBy(x => x.Order)).Include(r => r.Targets)
            .FirstOrDefaultAsync(r => r.Id == id && r.Kind == kind, ct);

    public async Task<NotificationRule> SetEnabledAsync(Guid id, bool enabled, string? updatedBy, CancellationToken ct)
    {
        var existing = await _db.NotificationRules
            .Include(r => r.Routes.OrderBy(x => x.Order)).Include(r => r.Targets)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException($"NotificationRule {id} not found");

        existing.IsEnabled = enabled;
        // Enabling a System policy (re-)stamps its activation watermark so event sources never back-alert
        // history; the evaluator prunes any leftover state for a disabled policy on its next pass.
        if (enabled && existing.Kind == NotificationRuleKind.System)
            existing.ActivatedAt = DateTime.UtcNow;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.UpdatedBy = updatedBy;

        await _db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<IReadOnlyList<NotificationRule>> GetEnabledAsync(CancellationToken ct)
        => await _db.NotificationRules.AsNoTracking()
            .Include(r => r.Routes.OrderBy(x => x.Order)).Include(r => r.Targets)
            .Where(r => r.IsEnabled)
            .OrderBy(r => r.Name).ToListAsync(ct);

    public async Task<NotificationRule> CreateAsync(NotificationRule draft, string? updatedBy, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        draft.Id = Guid.NewGuid();
        draft.CreatedAt = now;
        draft.UpdatedAt = now;
        draft.UpdatedBy = updatedBy;
        draft.Routes = BuildNewRoutes(draft.Routes, draft.Id);
        draft.Targets = NormalizeTargets(draft, draft.Id);

        _db.NotificationRules.Add(draft);
        await _db.SaveChangesAsync(ct);
        return draft;
    }

    public async Task UpdateAsync(Guid id, NotificationRule draft, string? updatedBy, CancellationToken ct)
    {
        var existing = await _db.NotificationRules
            .Include(r => r.Routes).Include(r => r.Targets)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException($"NotificationRule {id} not found");

        // Capture the system-relevant "before" values so we can drop transient policy state when a change
        // invalidates it (ADR 0008 — a changed source/params/filter/scope/duration resets the state machine).
        var systemStateInvalidated = existing.Kind == NotificationRuleKind.System && (
            existing.SystemSourceId != draft.SystemSourceId
            || existing.SourceParametersJson != draft.SourceParametersJson
            || existing.FilterExpressionJson != draft.FilterExpressionJson
            || existing.ScopeKind != draft.ScopeKind
            || existing.SustainForSeconds != draft.SustainForSeconds
            || !TargetsEqual(existing.Targets, draft.Targets));

        existing.Name = draft.Name;
        existing.Description = draft.Description;
        existing.IsEnabled = draft.IsEnabled;
        existing.EventTypes = draft.EventTypes;
        existing.FilterExpressionJson = draft.FilterExpressionJson;
        existing.ScopeKind = draft.ScopeKind;
        existing.CooldownMinutes = draft.CooldownMinutes;
        existing.DedupKeyTemplate = draft.DedupKeyTemplate;
        existing.MinOccurrences = draft.MinOccurrences;
        existing.OccurrenceWindowMinutes = draft.OccurrenceWindowMinutes;
        // System-policy fields (Kind itself is immutable after create — a custom rule never becomes a system
        // policy — so it is deliberately not copied here).
        existing.SystemSourceId = draft.SystemSourceId;
        existing.SystemPresetId = draft.SystemPresetId;
        existing.SourceParametersJson = draft.SourceParametersJson;
        existing.SustainForSeconds = draft.SustainForSeconds;
        existing.SeverityOverride = draft.SeverityOverride;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.UpdatedBy = updatedBy;

        if (systemStateInvalidated)
            await _db.SystemAlertPolicyStates.Where(s => s.NotificationRuleId == id).ExecuteDeleteAsync(ct);

        // Diff routes: update matched-by-id in place, add new, remove dropped. (Remove+re-add with
        // the same id would throw a duplicate-key tracking error — that's the whole point.)
        var existingList = existing.Routes.ToList();
        var existingById = existingList.ToDictionary(r => r.Id);
        var keep = new HashSet<Guid>();
        var order = 0;
        foreach (var dr in draft.Routes ?? [])
        {
            if (dr.Id != Guid.Empty && existingById.TryGetValue(dr.Id, out var ex))
            {
                ex.Channel = dr.Channel;
                ex.Target = dr.Target;
                ex.Order = order++;
                ex.Secret = ResolveRouteSecret(dr.Secret, ex.Secret);
                ex.ConditionExpressionJson = dr.ConditionExpressionJson;
                keep.Add(ex.Id);
            }
            else
            {
                var newId = dr.Id == Guid.Empty ? Guid.NewGuid() : dr.Id;
                // Add via the DbSet (NOT the navigation): a new route carries a client-set Guid, and
                // Guid PKs are ValueGeneratedOnAdd by EF convention — added through the tracked
                // parent's collection EF would infer "already exists" → Modified → an UPDATE that
                // affects 0 rows. Explicit DbSet.Add pins the Added state.
                _db.NotificationRoutes.Add(new NotificationRoute
                {
                    Id = newId,
                    NotificationRuleId = existing.Id,
                    Channel = dr.Channel,
                    Target = dr.Target,
                    Order = order++,
                    Secret = ResolveRouteSecret(dr.Secret, currentCipher: null),
                    ConditionExpressionJson = dr.ConditionExpressionJson,
                });
                keep.Add(newId);
            }
        }
        // Delete dropped routes explicitly via the DbSet (Deleted state by reference).
        foreach (var ex in existingList.Where(r => !keep.Contains(r.Id)))
            _db.NotificationRoutes.Remove(ex);

        // Targets carry fresh ids each time (no preserved state), so the cheap full replace is safe.
        _db.NotificationRuleTargets.RemoveRange(existing.Targets);
        _db.NotificationRuleTargets.AddRange(NormalizeTargets(draft, existing.Id));

        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var existing = await _db.NotificationRules
            .Include(r => r.Routes).Include(r => r.Targets)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException($"NotificationRule {id} not found");

        // Routes + targets cascade via FK; suppression + delivery-attempt rows do not (no rule FK,
        // to avoid multiple-cascade-paths), so clear them explicitly or a deleted rule leaves a
        // stale cooldown record + orphan ledger rows behind.
        var suppression = await _db.NotificationSuppressionStates
            .Where(s => s.NotificationRuleId == id).ToListAsync(ct);
        _db.NotificationSuppressionStates.RemoveRange(suppression);
        var attempts = await _db.NotificationDeliveryAttempts
            .Where(a => a.NotificationRuleId == id).ToListAsync(ct);
        _db.NotificationDeliveryAttempts.RemoveRange(attempts);

        // System policies also carry per-instance evaluator state (no rule FK either) — clear it so a
        // deleted policy leaves no orphan match/episode rows behind.
        if (existing.Kind == NotificationRuleKind.System)
            await _db.SystemAlertPolicyStates.Where(s => s.NotificationRuleId == id).ExecuteDeleteAsync(ct);

        _db.NotificationRules.Remove(existing); // routes + targets cascade
        await _db.SaveChangesAsync(ct);
    }

    public async Task<string?> GetRouteSecretAsync(Guid routeId, CancellationToken ct)
    {
        var cipher = await _db.NotificationRoutes.AsNoTracking()
            .Where(r => r.Id == routeId).Select(r => r.Secret).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(cipher)) return null;
        return _protector.Unprotect(Convert.FromBase64String(cipher));
    }

    // Encrypts a route secret for storage. Sentinel keeps the current cipher; empty clears it.
    private string? ResolveRouteSecret(string? input, string? currentCipher)
    {
        if (string.Equals(input, UnchangedSecret, StringComparison.Ordinal)) return currentCipher;
        if (string.IsNullOrEmpty(input)) return null;
        return Convert.ToBase64String(_protector.Protect(input));
    }

    private List<NotificationRoute> BuildNewRoutes(ICollection<NotificationRoute>? routes, Guid ruleId)
    {
        if (routes is null) return [];
        var order = 0;
        return routes.Select(r => new NotificationRoute
        {
            Id = r.Id == Guid.Empty ? Guid.NewGuid() : r.Id,
            NotificationRuleId = ruleId,
            Channel = r.Channel,
            Target = r.Target,
            // On create there is no existing cipher; a stray sentinel resolves to null.
            Secret = ResolveRouteSecret(r.Secret, currentCipher: null),
            ConditionExpressionJson = r.ConditionExpressionJson,
            Order = order++,
        }).ToList();
    }

    // Set-equality of scope targets by (kind, id), ignoring row ids — a changed scope target set invalidates
    // a system policy's transient state (its instances may differ).
    private static bool TargetsEqual(ICollection<NotificationRuleTarget>? a, ICollection<NotificationRuleTarget>? b)
    {
        var setA = (a ?? []).Select(t => (t.TargetKind, t.TargetId)).ToHashSet();
        var setB = (b ?? []).Select(t => (t.TargetKind, t.TargetId)).ToHashSet();
        return setA.SetEquals(setB);
    }

    private static List<NotificationRuleTarget> NormalizeTargets(NotificationRule draft, Guid ruleId)
    {
        if (draft.ScopeKind == NotificationScopeKind.Global || draft.Targets is null)
            return [];

        return draft.Targets
            .GroupBy(t => (t.TargetKind, t.TargetId))
            .Select(g => new NotificationRuleTarget
            {
                Id = Guid.NewGuid(),
                NotificationRuleId = ruleId,
                TargetKind = g.Key.TargetKind,
                TargetId = g.Key.TargetId,
            })
            .ToList();
    }
}
