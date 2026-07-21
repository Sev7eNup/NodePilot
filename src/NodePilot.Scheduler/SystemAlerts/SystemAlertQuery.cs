using System.Globalization;

namespace NodePilot.Scheduler.SystemAlerts;

/// <summary>
/// Descriptor-validated, normalized source parameters for one <c>ObserveAsync</c> call. Values are typed
/// per the source's declared <c>SystemAlertParameter</c>s. Two policies of the same source with identical
/// normalized queries are sampled once per dispatcher pass (grouped by source + normalized parameters).
/// </summary>
public sealed class SystemAlertQuery
{
    private readonly IReadOnlyDictionary<string, object?> _values;

    public SystemAlertQuery(IReadOnlyDictionary<string, object?> values)
        => _values = values ?? new Dictionary<string, object?>();

    /// <summary>An empty parameter set — for sources that take no query parameters.</summary>
    public static SystemAlertQuery Empty { get; } = new(new Dictionary<string, object?>());

    public int GetInt(string name, int fallback)
        => _values.TryGetValue(name, out var v) && v is not null
            ? Convert.ToInt32(v, CultureInfo.InvariantCulture)
            : fallback;

    public double GetDouble(string name, double fallback)
        => _values.TryGetValue(name, out var v) && v is not null
            ? Convert.ToDouble(v, CultureInfo.InvariantCulture)
            : fallback;

    public string? GetString(string name)
        => _values.TryGetValue(name, out var v) && v is not null
            ? Convert.ToString(v, CultureInfo.InvariantCulture)
            : null;

    public bool GetBool(string name, bool fallback)
        => _values.TryGetValue(name, out var v) && v is not null
            ? Convert.ToBoolean(v, CultureInfo.InvariantCulture)
            : fallback;
}
