namespace NodePilot.Core.Enums;

/// <summary>
/// How a <see cref="NodePilot.Core.Models.MaintenanceWindow"/>'s active periods are
/// expressed in time.
/// </summary>
public enum MaintenanceRecurrenceKind
{
    /// <summary>
    /// A single absolute interval <c>[OneTimeStartUtc, OneTimeEndUtc)</c>. Best for an
    /// ad-hoc migration ("this Saturday 22:00 → Sunday 06:00").
    /// </summary>
    OneTime,

    /// <summary>
    /// Recurs every selected weekday between a start and end minute-of-day, interpreted in
    /// the window's <c>TimeZoneId</c>. <c>WeeklyEndMinuteOfDay &lt; WeeklyStartMinuteOfDay</c>
    /// wraps past midnight into the next day.
    /// </summary>
    Weekly,

    /// <summary>
    /// Opens at each Quartz-cron fire for <c>DurationMinutes</c> — active during the half-open
    /// interval <c>[fire, fire + duration)</c>, with the expression interpreted in the window's
    /// <c>TimeZoneId</c>.
    /// </summary>
    Cron,
}
