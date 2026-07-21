using NodePilot.Core.Enums;

namespace NodePilot.Core.Interfaces;

/// <summary>
/// Verdict of evaluating the maintenance windows that target a workflow at a point in time.
/// </summary>
/// <param name="Blocked">True if the workflow must NOT start a new run now.</param>
/// <param name="WindowId">The deciding window (the active blackout, or the allow-only window that gates the run).</param>
/// <param name="WindowName">Name of the deciding window, for UI / audit messages.</param>
/// <param name="ActiveUntilUtc">
/// For a blackout block: when the current blackout interval ends (so the UI can say when to
/// retry). Null when not computable (e.g. an "outside the allow window" block).
/// </param>
/// <param name="Mode">Mode of the deciding window.</param>
public readonly record struct MaintenanceEvaluation(
    bool Blocked,
    Guid? WindowId,
    string? WindowName,
    DateTime? ActiveUntilUtc,
    MaintenanceMode? Mode)
{
    public static MaintenanceEvaluation Allowed => new(false, null, null, null, null);
}

/// <summary>Lightweight projection of a window that targets a workflow (read-only badge).</summary>
public readonly record struct MaintenanceWindowSummary(
    Guid Id,
    string Name,
    MaintenanceMode Mode,
    bool IsEnabled,
    bool ActiveNow);

/// <summary>
/// Decides whether a workflow may start a run right now, based on the maintenance windows that
/// target it. Backed by an in-memory snapshot refreshed on a short interval, so
/// <see cref="Evaluate"/> is a pure, allocation-light, in-memory call safe on the dispatch hot
/// path. Implementations MUST fail OPEN (return <see cref="MaintenanceEvaluation.Allowed"/>) on
/// any internal error — a maintenance window is an availability control, never a hard security
/// gate, and there is no off-switch, so a fail-closed bug would halt the whole cluster.
/// </summary>
public interface IMaintenanceWindowEvaluator
{
    MaintenanceEvaluation Evaluate(Guid workflowId, Guid folderId, DateTime nowUtc);

    /// <summary>
    /// Windows that target the given workflow (directly or via folder ancestry), regardless of
    /// whether they are active now — powers the read-only "windows affecting this workflow" badge.
    /// </summary>
    IReadOnlyList<MaintenanceWindowSummary> GetWindowsAffecting(Guid workflowId, Guid folderId, DateTime nowUtc);

    /// <summary>Reloads the snapshot from the database. Called on an interval and inline after CRUD.</summary>
    Task RefreshAsync(CancellationToken ct);
}
