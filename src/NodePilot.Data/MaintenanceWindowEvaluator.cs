using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;

namespace NodePilot.Data;

/// <summary>
/// Singleton <see cref="IMaintenanceWindowEvaluator"/>. Holds an immutable in-memory snapshot
/// of all <i>enabled</i> windows plus the folder parent-id map, refreshed on an interval and
/// inline after CRUD. <see cref="Evaluate"/> is pure in-memory (no I/O) so it is cheap on the
/// dispatch hot path, and fails OPEN on any error — a maintenance window is an availability
/// control, not a security gate, and there is no off-switch.
///
/// <para>Cron-recurrence windows open at each fire of their Quartz expression (interpreted in
/// the window's time zone) and stay active for <c>DurationMinutes</c> — i.e. active iff
/// <c>now ∈ [fire, fire + duration)</c> for the most recent fire. An unparseable expression or
/// a missing/non-positive duration makes the window inert (fail open), like an unknown zone.</para>
///
/// <para><b>Placement (deliberate):</b> the window-matching math looks Core-shaped (pure,
/// no I/O), but the compiled snapshot embeds <c>Quartz.CronExpression</c> for cron windows —
/// and Core must stay package-dependency-free. Splitting the cron path out just to move the
/// rest would scatter one cohesive evaluation across two layers, so the whole evaluator stays
/// here in Data, next to the snapshot loading it exists to serve (interface remains in Core).</para>
/// </summary>
public sealed class MaintenanceWindowEvaluator : IMaintenanceWindowEvaluator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MaintenanceWindowEvaluator> _logger;

    private volatile Snapshot _snapshot = Snapshot.Empty;

    public MaintenanceWindowEvaluator(
        IServiceScopeFactory scopeFactory,
        ILogger<MaintenanceWindowEvaluator> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public MaintenanceEvaluation Evaluate(Guid workflowId, Guid folderId, DateTime nowUtc)
    {
        try
        {
            var snap = _snapshot;
            if (snap.Windows.Count == 0) return MaintenanceEvaluation.Allowed;

            var ancestors = AncestorsOrSelf(folderId, snap.FolderParents);

            MaintenanceEvaluation? blackout = null;
            var hasLiveAllow = false;
            var insideAnyAllow = false;
            Guid allowWindowId = default;
            string allowWindowName = string.Empty;

            foreach (var w in snap.Windows)
            {
                if (!ScopeMatches(w, workflowId, ancestors)) continue;

                if (w.Mode == MaintenanceMode.Blackout)
                {
                    if (TryActive(w, nowUtc, out var until)
                        && (blackout is null || until > (blackout.Value.ActiveUntilUtc ?? DateTime.MinValue)))
                    {
                        // Deny-wins: any active blackout blocks; keep the one ending latest so the
                        // UI reports the longest wait.
                        blackout = new MaintenanceEvaluation(true, w.Id, w.Name, until, MaintenanceMode.Blackout);
                    }
                }
                else // AllowOnly
                {
                    if (IsLive(w, nowUtc))
                    {
                        if (!hasLiveAllow) { hasLiveAllow = true; allowWindowId = w.Id; allowWindowName = w.Name; }
                        if (TryActive(w, nowUtc, out _)) insideAnyAllow = true;
                    }
                }
            }

            if (blackout is not null) return blackout.Value;

            // A workflow with at least one live AllowOnly window may run ONLY while inside one of
            // them. Expired (non-live) AllowOnly windows are inert and fall through to allow.
            if (hasLiveAllow && !insideAnyAllow)
                return new MaintenanceEvaluation(true, allowWindowId, allowWindowName, null, MaintenanceMode.AllowOnly);

            return MaintenanceEvaluation.Allowed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Maintenance window evaluation failed; failing open (allowing the run).");
            return MaintenanceEvaluation.Allowed;
        }
    }

    public IReadOnlyList<MaintenanceWindowSummary> GetWindowsAffecting(Guid workflowId, Guid folderId, DateTime nowUtc)
    {
        var snap = _snapshot;
        if (snap.Windows.Count == 0) return [];

        var ancestors = AncestorsOrSelf(folderId, snap.FolderParents);
        var list = new List<MaintenanceWindowSummary>();
        foreach (var w in snap.Windows)
        {
            if (!ScopeMatches(w, workflowId, ancestors)) continue;
            var active = TryActive(w, nowUtc, out _);
            list.Add(new MaintenanceWindowSummary(w.Id, w.Name, w.Mode, IsEnabled: true, ActiveNow: active));
        }
        return list;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();

        var windows = await db.MaintenanceWindows
            .AsNoTracking()
            .Where(w => w.IsEnabled)
            .Include(w => w.Targets)
            .ToListAsync(ct);

        var folderParents = await db.SharedWorkflowFolders
            .AsNoTracking()
            .Select(f => new { f.Id, f.ParentFolderId })
            .ToDictionaryAsync(f => f.Id, f => f.ParentFolderId, ct);

        var compiled = new List<CompiledWindow>(windows.Count);
        foreach (var w in windows)
        {
            TimeZoneInfo? tz = null;
            if (NodePilot.Core.Time.TimeZoneResolver.TryResolve(w.TimeZoneId, out var resolved))
                tz = resolved;
            else
                _logger.LogWarning(
                    "Maintenance window '{Name}' ({Id}) has unknown time zone '{Tz}'; it will not be evaluated.",
                    w.Name, w.Id, w.TimeZoneId);

            // Cron expressions are compiled once per refresh. A garbage expression (or an
            // unresolved zone) leaves Cron=null and the window inert — a misconfigured window
            // must fail OPEN on the dispatch hot path, never throw or trap everything.
            Quartz.CronExpression? cron = null;
            if (w.Recurrence == MaintenanceRecurrenceKind.Cron && tz is not null
                && !string.IsNullOrWhiteSpace(w.CronExpression))
            {
                try
                {
                    cron = new Quartz.CronExpression(w.CronExpression) { TimeZone = tz };
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Maintenance window '{Name}' ({Id}) has an invalid cron expression '{Cron}'; it will not be evaluated.",
                        w.Name, w.Id, w.CronExpression);
                }
            }

            var folderTargets = new HashSet<Guid>();
            var workflowTargets = new HashSet<Guid>();
            foreach (var t in w.Targets)
            {
                if (t.TargetKind == MaintenanceTargetKind.Folder) folderTargets.Add(t.TargetId);
                else workflowTargets.Add(t.TargetId);
            }

            compiled.Add(new CompiledWindow(
                w.Id, w.Name, w.Mode, w.ScopeKind, w.Recurrence,
                w.OneTimeStartUtc, w.OneTimeEndUtc,
                w.WeeklyDaysMask, w.WeeklyStartMinuteOfDay, w.WeeklyEndMinuteOfDay,
                cron, w.DurationMinutes,
                tz, folderTargets, workflowTargets));
        }

        _snapshot = new Snapshot(compiled, folderParents);
    }

    // ---- matching ----

    private static bool ScopeMatches(CompiledWindow w, Guid workflowId, HashSet<Guid> folderAncestorsOrSelf)
        => w.ScopeKind switch
        {
            MaintenanceScopeKind.Global => true,
            MaintenanceScopeKind.Workflows => w.WorkflowTargets.Contains(workflowId),
            MaintenanceScopeKind.Folders => w.FolderTargets.Overlaps(folderAncestorsOrSelf),
            _ => false,
        };

    // Walk ParentFolderId up to the root, collecting the workflow's folder and all ancestors.
    // A window targeting folder F blocks F and every descendant, so a descendant matches when F
    // is in its ancestors-or-self set. Cycle-guarded via the HashSet + a hard hop cap.
    private static HashSet<Guid> AncestorsOrSelf(Guid folderId, IReadOnlyDictionary<Guid, Guid?> parents)
    {
        var set = new HashSet<Guid>();
        Guid? current = folderId;
        var guard = 0;
        while (current is { } id && set.Add(id) && guard++ < 32)
            current = parents.TryGetValue(id, out var parent) ? parent : null;
        return set;
    }

    // ---- time ----

    private static bool TryActive(CompiledWindow w, DateTime nowUtc, out DateTime activeUntilUtc)
    {
        activeUntilUtc = default;
        return w.Recurrence switch
        {
            MaintenanceRecurrenceKind.OneTime => TryOneTimeActive(w, nowUtc, out activeUntilUtc),
            MaintenanceRecurrenceKind.Weekly => TryWeeklyActive(w, nowUtc, out activeUntilUtc),
            MaintenanceRecurrenceKind.Cron => TryCronActive(w, nowUtc, out activeUntilUtc),
            _ => false,
        };
    }

    private static bool TryOneTimeActive(CompiledWindow w, DateTime nowUtc, out DateTime activeUntilUtc)
    {
        activeUntilUtc = default;
        if (w.OneTimeStartUtc is { } s && w.OneTimeEndUtc is { } e && s <= nowUtc && nowUtc < e)
        {
            activeUntilUtc = e;
            return true;
        }
        return false;
    }

    private static bool TryWeeklyActive(CompiledWindow w, DateTime nowUtc, out DateTime activeUntilUtc)
    {
        activeUntilUtc = default;
        if (w.Tz is null || w.WeeklyStartMin is not { } start || w.WeeklyEndMin is not { } end)
            return false;

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), w.Tz);
        var nowMin = localNow.TimeOfDay.TotalMinutes;
        var today = localNow.DayOfWeek;

        // start == end means a full 24h window on each enabled weekday: [day 00:00, next 00:00).
        // (Minute-of-day caps at 1439, so 00:00-23:59 would leave a one-minute gap — this is how
        // "every Sunday, all day" is expressed.)
        if (start == end)
        {
            if (DayEnabled(w.WeeklyDaysMask, today))
            {
                activeUntilUtc = ToUtc(localNow.Date.AddDays(1), w.Tz);
                return true;
            }
            return false;
        }

        if (end > start) // same-day window
        {
            if (DayEnabled(w.WeeklyDaysMask, today) && nowMin >= start && nowMin < end)
            {
                activeUntilUtc = ToUtc(localNow.Date.AddMinutes(end), w.Tz);
                return true;
            }
            return false;
        }

        // wraps past midnight: [start, 24:00) on day D, [00:00, end) on day D+1
        if (DayEnabled(w.WeeklyDaysMask, today) && nowMin >= start)
        {
            activeUntilUtc = ToUtc(localNow.Date.AddDays(1).AddMinutes(end), w.Tz);
            return true;
        }
        var yesterday = (DayOfWeek)(((int)today + 6) % 7);
        if (DayEnabled(w.WeeklyDaysMask, yesterday) && nowMin < end)
        {
            activeUntilUtc = ToUtc(localNow.Date.AddMinutes(end), w.Tz);
            return true;
        }
        return false;
    }

    // A cron window is active during [fire, fire + duration) for every fire of its expression.
    // GetTimeAfter(now - duration) yields the earliest fire STRICTLY after that probe — i.e.
    // exactly the fires with fire + duration > now — so the window is active iff that fire has
    // already happened (fire <= now). Half-open semantics fall out naturally: at now == fire +
    // duration the fire is no longer strictly after the probe, so the window reads closed.
    // The zone is baked into the compiled CronExpression (Quartz's TimeZone property).
    private static bool TryCronActive(CompiledWindow w, DateTime nowUtc, out DateTime activeUntilUtc)
    {
        activeUntilUtc = default;
        if (w.Cron is null || w.CronDurationMinutes is not { } minutes || minutes <= 0)
            return false; // defensive: misconfigured cron windows are never active (fail open)

        var duration = TimeSpan.FromMinutes(minutes);
        var now = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
        if (w.Cron.GetTimeAfter(now - duration) is { } fire && fire <= now)
        {
            activeUntilUtc = (fire + duration).UtcDateTime;
            return true;
        }
        return false;
    }

    private static bool IsLive(CompiledWindow w, DateTime nowUtc) => w.Recurrence switch
    {
        MaintenanceRecurrenceKind.OneTime => w.OneTimeEndUtc is { } e && e > nowUtc,
        MaintenanceRecurrenceKind.Weekly => true,
        // A cron window is live only while it can still open: currently active OR at
        // least one future fire exists. Quartz expressions can EXHAUST (optional year
        // field, e.g. "0 0 3 ? * SAT 2026") — without this check an exhausted AllowOnly
        // window would stay "live" forever and permanently trap its targeted workflows
        // (hasLiveAllow && never insideAnyAllow). Misconfigured crons stay inert.
        MaintenanceRecurrenceKind.Cron => w.Cron is not null
            && w.CronDurationMinutes is { } minutes && minutes > 0
            && w.Cron.GetTimeAfter(
                new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc))
                    - TimeSpan.FromMinutes(minutes)) is not null,
        _ => false,
    };

    private static bool DayEnabled(int mask, DayOfWeek dow) => (mask & (1 << (int)dow)) != 0;

    private static DateTime ToUtc(DateTime localUnspecified, TimeZoneInfo tz)
    {
        var local = DateTime.SpecifyKind(localUnspecified, DateTimeKind.Unspecified);
        try { return TimeZoneInfo.ConvertTimeToUtc(local, tz); }
        catch (ArgumentException)
        {
            // DST spring-forward gap: the local time doesn't exist. Best-effort fallback so the
            // window edge is only nudged, never flipped.
            return DateTime.SpecifyKind(local - tz.GetUtcOffset(local), DateTimeKind.Utc);
        }
    }

    // ---- immutable snapshot types ----

    private sealed record CompiledWindow(
        Guid Id,
        string Name,
        MaintenanceMode Mode,
        MaintenanceScopeKind ScopeKind,
        MaintenanceRecurrenceKind Recurrence,
        DateTime? OneTimeStartUtc,
        DateTime? OneTimeEndUtc,
        int WeeklyDaysMask,
        int? WeeklyStartMin,
        int? WeeklyEndMin,
        Quartz.CronExpression? Cron,
        int? CronDurationMinutes,
        TimeZoneInfo? Tz,
        HashSet<Guid> FolderTargets,
        HashSet<Guid> WorkflowTargets);

    private sealed class Snapshot(
        IReadOnlyList<CompiledWindow> windows,
        IReadOnlyDictionary<Guid, Guid?> folderParents)
    {
        public IReadOnlyList<CompiledWindow> Windows { get; } = windows;
        public IReadOnlyDictionary<Guid, Guid?> FolderParents { get; } = folderParents;
        public static readonly Snapshot Empty = new([], new Dictionary<Guid, Guid?>());
    }
}
