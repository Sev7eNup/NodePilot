namespace NodePilot.Core.Enums;

/// <summary>
/// Semantics of a <see cref="NodePilot.Core.Models.MaintenanceWindow"/> relative to the
/// workflows it targets.
/// </summary>
public enum MaintenanceMode
{
    /// <summary>
    /// While the window is active, targeted workflows are BLOCKED from starting new runs
    /// (e.g. "no backups during the Saturday patch-reboot window"). Any active Blackout
    /// wins over every AllowOnly window (deny-wins precedence).
    /// </summary>
    Blackout,

    /// <summary>
    /// Targeted workflows may run ONLY while one of their AllowOnly windows is active;
    /// outside it they are blocked (e.g. "this heavy report job may only run 01:00–04:00").
    /// An AllowOnly window that has fully expired (non-recurring, end in the past) is inert:
    /// it neither blocks forever nor reverts the workflow to allow-always.
    /// </summary>
    AllowOnly,
}
