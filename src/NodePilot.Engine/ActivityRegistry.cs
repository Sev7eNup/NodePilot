using Microsoft.Extensions.DependencyInjection;
using NodePilot.Core.Activities;
using NodePilot.Core.Interfaces;

namespace NodePilot.Engine;

/// <summary>
/// Maps workflow activity-type strings (e.g. <c>"runScript"</c>) to their executor.
/// Supports two modes:
///
/// <list type="bullet">
/// <item><b>Production singleton</b> via <see cref="ActivityRegistry(IServiceProvider)"/>:
/// scans the registered executors once at startup, caches a <see cref="Dictionary{TKey,TValue}"/>
/// of <c>activityType → Type</c>, then resolves a fresh executor instance from the per-step scope
/// on every <see cref="GetExecutor(string, IServiceProvider)"/> call. No per-step dictionary
/// rebuild, no enumeration of all registered executors per step.</item>
///
/// <item><b>Test mode</b> via <see cref="ActivityRegistry(IEnumerable{IActivityExecutor})"/>:
/// pre-built mock instances. <see cref="GetExecutor(string)"/> returns them directly without
/// touching DI. Used by the unit tests in <c>NodePilot.Engine.Tests</c>.</item>
/// </list>
///
/// Registered as singleton: a Scoped registration would rebuild the executor dictionary
/// for every step by enumerating all <see cref="IActivityExecutor"/> registrations.
/// Singleton + per-step scope-resolve drops the rebuild cost to zero for hot-path workflows.
/// </summary>
public class ActivityRegistry
{
    // Production mode: type-only map, executors resolved from the per-step scope on demand.
    private readonly Dictionary<string, Type>? _typeMap;
    private readonly IServiceProvider? _rootProvider;

    // Test mode: pre-built executor instances.
    private readonly Dictionary<string, IActivityExecutor>? _instances;

    /// <summary>
    /// Test ctor — accepts pre-built executors (typically Moq-generated).
    /// <see cref="GetExecutor(string)"/> returns them directly; the
    /// <see cref="GetExecutor(string, IServiceProvider)"/> overload falls back to this map.
    /// </summary>
    public ActivityRegistry(IEnumerable<IActivityExecutor> executors)
    {
        _instances = new Dictionary<string, IActivityExecutor>(StringComparer.OrdinalIgnoreCase);
        foreach (var executor in executors)
            _instances[executor.ActivityType] = executor;
    }

    /// <summary>
    /// Production ctor — scans <see cref="IActivityExecutor"/> registrations once via a
    /// bootstrap scope to learn each executor's <see cref="IActivityExecutor.ActivityType"/>,
    /// then caches the <c>activityType → Type</c> map. Subsequent
    /// <see cref="GetExecutor(string, IServiceProvider)"/> calls resolve fresh instances from
    /// the supplied per-step scope, so no scoped state is shared between steps.
    /// </summary>
    public ActivityRegistry(IServiceProvider rootProvider)
    {
        _rootProvider = rootProvider;
        using var bootstrap = rootProvider.CreateScope();
        var executors = bootstrap.ServiceProvider.GetServices<IActivityExecutor>();
        _typeMap = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var executor in executors)
            _typeMap[executor.ActivityType] = executor.GetType();
    }

    /// <summary>
    /// Test-mode lookup. In production, prefer
    /// <see cref="GetExecutor(string, IServiceProvider)"/> with the per-step scope.
    /// If called in production, opens a temporary scope (one-shot) — functionally correct
    /// but allocates an extra scope per call.
    /// </summary>
    public IActivityExecutor GetExecutor(string activityType)
    {
        if (_instances is not null)
        {
            if (_instances.TryGetValue(activityType, out var instance))
                return instance;
            // custom:<key> activities all map to the single sentinel-registered CustomActivityExecutor.
            if (CustomActivityType.IsCustomType(activityType)
                && _instances.TryGetValue(CustomActivityType.ExecutorSentinel, out var customInstance))
                return customInstance;
            throw new InvalidOperationException(
                $"No executor registered for activity type '{activityType}'");
        }

        // Production fallback: temporary scope for callers that don't have one.
        if (_typeMap is not null && _rootProvider is not null)
        {
            using var scope = _rootProvider.CreateScope();
            return GetExecutor(activityType, scope.ServiceProvider);
        }

        throw new InvalidOperationException("ActivityRegistry was not initialized.");
    }

    /// <summary>
    /// Production lookup. Resolves a fresh executor instance from <paramref name="scopedProvider"/>
    /// (the per-step scope). Falls back to the test-mode map when constructed via the test ctor.
    /// </summary>
    public IActivityExecutor GetExecutor(string activityType, IServiceProvider scopedProvider)
    {
        if (_typeMap is not null)
        {
            if (!_typeMap.TryGetValue(activityType, out var executorType))
            {
                // custom:<key> activities are not individually registered — route every one of them
                // to the single CustomActivityExecutor (registered under the reserved sentinel type).
                if (CustomActivityType.IsCustomType(activityType)
                    && _typeMap.TryGetValue(CustomActivityType.ExecutorSentinel, out var customType))
                    return (IActivityExecutor)scopedProvider.GetRequiredService(customType);
                throw new InvalidOperationException(
                    $"No executor registered for activity type '{activityType}'");
            }
            return (IActivityExecutor)scopedProvider.GetRequiredService(executorType);
        }
        return GetExecutor(activityType);
    }

    public IReadOnlyList<string> GetRegisteredTypes() =>
        _typeMap?.Keys.ToList() ?? _instances!.Keys.ToList();
}
