namespace NodePilot.Core.Enums;

/// <summary>
/// Semantics of a <see cref="NodePilot.Core.Models.MaintenanceWindow"/> relative to the
/// workflows it targets.
/// </summary>
public enum MaintenanceMode
{
    /// <summary>
    /// While the window is active, targeted workflows cannot start new runs, for example no
    /// backups during a Saturday patch-reboot window. An active Blackout wins over every
    /// AllowOnly window (deny-wins precedence).
    /// </summary>
    Blackout,

    /// <summary>
    /// Targeted workflows may run only while one of their AllowOnly windows is active and are
    /// blocked outside it, for example a heavy report job limited to 01:00-04:00. A fully
    /// expired AllowOnly window (non-recurring, end in the past) is inert: it neither blocks
    /// forever nor reverts the workflow to allow-always.
    /// </summary>
    AllowOnly,
}
