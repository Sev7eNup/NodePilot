namespace NodePilot.Core.Enums;

/// <summary>
/// What happens to a trigger fire that a maintenance window blocks. Only <see cref="Skip"/> is
/// implemented; the other values exist so that adding catch-up later needs no schema migration.
/// </summary>
public enum MaintenanceDeferralPolicy
{
    /// <summary>Drop the blocked fire and record an audit entry.</summary>
    Skip,

    /// <summary>Reserved: queue the blocked fire and run it once when the window closes.</summary>
    RunOnceAfter,

    /// <summary>Reserved: queue all blocked fires and run them when the window closes.</summary>
    RunAllAfter,
}
