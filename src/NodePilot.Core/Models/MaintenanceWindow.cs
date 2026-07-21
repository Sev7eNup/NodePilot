using NodePilot.Core.Enums;

namespace NodePilot.Core.Models;

/// <summary>
/// Admin-managed maintenance window: a time-scoped rule that controls whether the workflows
/// it targets are allowed to start new runs. The modern equivalent of SCOrch "schedules used
/// as gates" — centralised so a single window can blanket everything, a folder subtree, or an
/// explicit list of workflows, instead of N drifting per-workflow copies.
///
/// <para><b>Admission control, not a kill-switch.</b> A window is only evaluated when a run is
/// <i>admitted</i> (manual execute, trigger fire, webhook, external trigger). It never cancels
/// an in-flight run, and it never re-gates a resume, an internal step-retry, or a sub-workflow
/// invocation — those have already been admitted. To drain running work, use
/// disable + cancel-all.</para>
///
/// <para><b>Precedence (deny-wins).</b> Across all windows that target a workflow at a given
/// instant: if ANY enabled <see cref="MaintenanceMode.Blackout"/> window is active → BLOCK.
/// Otherwise, if ANY non-expired <see cref="MaintenanceMode.AllowOnly"/> window targets the
/// workflow → allow ONLY while one of those allow-windows is active. Otherwise (no window
/// targets it) → ALLOW. <see cref="IsEnabled"/>=false makes a window inert (blocks nothing).</para>
///
/// <para><b>Time + DST.</b> Weekly/Cron occurrences are resolved from <see cref="TimeZoneId"/>
/// to half-open <c>[start, end)</c> UTC intervals at evaluation time, so node clock skew only
/// nudges a window edge rather than flipping verdicts. On a DST spring-forward the start moves
/// to the first valid instant; on fall-back a Blackout covers the ambiguous hour while an
/// AllowOnly does not.</para>
/// </summary>
public class MaintenanceWindow
{
    public Guid Id { get; set; }

    /// <summary>Unique, human-readable label shown in the UI / audit / block messages.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Master switch. A disabled window is ignored entirely — it blocks nothing.</summary>
    public bool IsEnabled { get; set; } = true;

    public MaintenanceMode Mode { get; set; } = MaintenanceMode.Blackout;

    public MaintenanceScopeKind ScopeKind { get; set; } = MaintenanceScopeKind.Global;

    public MaintenanceRecurrenceKind Recurrence { get; set; } = MaintenanceRecurrenceKind.Weekly;

    // --- OneTime ---
    /// <summary>Absolute UTC start instant (inclusive) for <see cref="MaintenanceRecurrenceKind.OneTime"/>.</summary>
    public DateTime? OneTimeStartUtc { get; set; }
    /// <summary>Absolute UTC end instant (exclusive) for <see cref="MaintenanceRecurrenceKind.OneTime"/>.</summary>
    public DateTime? OneTimeEndUtc { get; set; }

    // --- Weekly ---
    /// <summary>
    /// Bitmask of active weekdays: Sun=1, Mon=2, Tue=4, Wed=8, Thu=16, Fri=32, Sat=64.
    /// Evaluated against the day-of-week of the window's local time zone.
    /// </summary>
    public int WeeklyDaysMask { get; set; }
    /// <summary>Local minute-of-day the window opens (0..1439), interpreted in <see cref="TimeZoneId"/>.</summary>
    public int? WeeklyStartMinuteOfDay { get; set; }
    /// <summary>
    /// Local minute-of-day the window closes (0..1439). If less than
    /// <see cref="WeeklyStartMinuteOfDay"/> the window wraps past midnight into the next day.
    /// </summary>
    public int? WeeklyEndMinuteOfDay { get; set; }

    // --- Cron ---
    /// <summary>
    /// Quartz cron expression (with seconds field, e.g. <c>0 0 3 ? * SAT</c>) for
    /// <see cref="MaintenanceRecurrenceKind.Cron"/>, interpreted in <see cref="TimeZoneId"/>.
    /// The window opens at every fire.
    /// </summary>
    public string? CronExpression { get; set; }
    /// <summary>
    /// How many minutes the window stays open after each cron fire — active during the
    /// half-open interval <c>[fire, fire + duration)</c>.
    /// </summary>
    public int? DurationMinutes { get; set; }

    /// <summary>
    /// IANA or Windows time-zone id the Weekly/Cron wall-clock fields are interpreted in.
    /// Deliberately NOT the server-local zone (a deployment accident); defaults to UTC.
    /// </summary>
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>Reserved for a future catch-up feature; v1 only honors <see cref="MaintenanceDeferralPolicy.Skip"/>.</summary>
    public MaintenanceDeferralPolicy DeferralPolicy { get; set; } = MaintenanceDeferralPolicy.Skip;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Username of the last editor (audit cross-reference).</summary>
    public string? UpdatedBy { get; set; }

    public ICollection<MaintenanceWindowTarget> Targets { get; set; } = [];
}
