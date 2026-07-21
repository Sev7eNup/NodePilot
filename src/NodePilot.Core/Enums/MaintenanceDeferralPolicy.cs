namespace NodePilot.Core.Enums;

/// <summary>
/// What happens to a trigger fire that a maintenance window blocks. Reserved for a future
/// catch-up feature — v1 only implements <see cref="Skip"/>. The column exists now so the
/// later phase needs no schema migration.
/// </summary>
public enum MaintenanceDeferralPolicy
{
    /// <summary>Drop the blocked fire and record an audit entry (v1 behavior).</summary>
    Skip,

    /// <summary>Reserved: queue the blocked fire and run it once when the window closes.</summary>
    RunOnceAfter,

    /// <summary>Reserved: queue every blocked fire and run them all when the window closes.</summary>
    RunAllAfter,
}
