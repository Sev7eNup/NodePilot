using NodePilot.Core.Models;

namespace NodePilot.Scheduler.SystemAlerts;

/// <summary>
/// The aggregated set of registered system-alert sources — the built-in, self-describing alert producers
/// (backlog size, machine reachability, execution failures, etc.) that operators build policies on top of.
/// This design replaced a set of hard-coded threshold providers (ADR 0008). Built once from the
/// DI-registered <see cref="ISystemAlertSource"/> collection; the API serves its <see cref="Descriptors"/>
/// as the single server-owned alerting catalog and the evaluator resolves sources by id via <see cref="Find"/>.
/// </summary>
public interface ISystemAlertCatalog
{
    /// <summary>All source descriptors, ordered by <c>SourceId</c> for stable output.</summary>
    IReadOnlyList<SystemAlertSourceDescriptor> Descriptors { get; }

    /// <summary>The source with this id, or null if none is registered.</summary>
    ISystemAlertSource? Find(string sourceId);
}

/// <summary>
/// Default <see cref="ISystemAlertCatalog"/>. Enforces the two invariants that keep the catalog honest at
/// boot: every source's <c>SourceId</c> matches its descriptor, and no two sources share a <c>SourceId</c>.
/// A mis-registered source throws here rather than surfacing a confusing catalog at runtime.
/// </summary>
public sealed class SystemAlertCatalog : ISystemAlertCatalog
{
    private readonly IReadOnlyDictionary<string, ISystemAlertSource> _byId;

    public SystemAlertCatalog(IEnumerable<ISystemAlertSource> sources)
    {
        var byId = new Dictionary<string, ISystemAlertSource>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var descriptor = source.Describe();
            if (!string.Equals(source.SourceId, descriptor.SourceId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"System-alert source '{source.GetType().Name}' exposes SourceId '{source.SourceId}' " +
                    $"but its descriptor SourceId is '{descriptor.SourceId}'.");
            if (!byId.TryAdd(source.SourceId, source))
                throw new InvalidOperationException($"Duplicate system-alert SourceId '{source.SourceId}'.");
        }

        _byId = byId;
        Descriptors = byId.Values
            .Select(s => s.Describe())
            .OrderBy(d => d.SourceId, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<SystemAlertSourceDescriptor> Descriptors { get; }

    public ISystemAlertSource? Find(string sourceId) => _byId.GetValueOrDefault(sourceId);
}
