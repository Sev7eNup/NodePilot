using NodePilot.Core.Activities;

namespace NodePilot.Core.Constants;

/// <summary>
/// Single source of truth for which activityType strings are considered trigger entry-points.
/// The engine uses this to pick root nodes trigger-first, so a node is never silently
/// promoted to an entry point just because its incoming edge was deleted.
/// </summary>
public static class TriggerActivityTypes
{
    public static readonly IReadOnlySet<string> All = ActivityCatalog.TriggerTypes;

    public static bool IsTrigger(string? activityType) =>
        activityType is not null && All.Contains(activityType);
}
