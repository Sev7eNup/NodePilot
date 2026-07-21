using NodePilot.Core.Activities;

namespace NodePilot.Engine.Telemetry;

/// <summary>
/// Per-activity-type allow-list for OutputParameters that may be exposed as OTel span
/// tags. We can't blindly tag every parameter because workflow authors can stuff
/// arbitrary (potentially secret-bearing) values in there, and OTel exporters ship
/// span tags wherever the backend sits. The allow-list says "for activity X, only
/// these parameter names are SAFE to surface in telemetry".
///
/// Keep entries strictly to numerics, enums, status codes, and short identifiers.
/// Anything that could carry user data, paths, raw strings, or secret material stays
/// off the list.
/// </summary>
internal static class ActivityTelemetryAllowList
{
    private static readonly Dictionary<string, HashSet<string>> _allowed =
        ActivityCatalog.All
            .Where(a => a.TelemetryParameters.Count > 0)
            .ToDictionary(
                a => a.Type,
                a => new HashSet<string>(a.TelemetryParameters, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

    internal static bool IsExposable(string activityType, string parameterName)
    {
        if (string.IsNullOrEmpty(activityType) || string.IsNullOrEmpty(parameterName)) return false;
        return _allowed.TryGetValue(activityType, out var set) && set.Contains(parameterName);
    }
}
