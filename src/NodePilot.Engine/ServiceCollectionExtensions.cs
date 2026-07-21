using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Options;

namespace NodePilot.Engine;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers every concrete <see cref="IActivityExecutor"/> found in the
    /// <c>NodePilot.Engine</c> assembly as scoped. Picks up both activities and triggers
    /// without manual wiring — new activity = drop a file in Activities/Triggers and it's in.
    /// Abstract classes (e.g. <c>BaseRemoteActivity</c>) are skipped.
    ///
    /// Also registers the default <see cref="ISubWorkflowGate"/> (process-wide cap on
    /// concurrent sub-workflows). Capacity defaults to <see cref="InMemorySubWorkflowGate.DefaultCapacity"/>
    /// (128); override per-deployment by replacing the registration before this call.
    /// </summary>
    public static IServiceCollection AddNodePilotActivities(this IServiceCollection services)
    {
        // Singleton: the gate is shared across every activity invocation in the process.
        // TryAdd so a host that wants a custom gate (e.g. a future distributed implementation
        // for HA) can register first and not be overwritten.
        services.TryAddSingleton<ISubWorkflowGate>(_ => new InMemorySubWorkflowGate());

        var executorInterface = typeof(IActivityExecutor);
        var assembly = typeof(WorkflowEngine).Assembly;

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface) continue;
            if (!executorInterface.IsAssignableFrom(type)) continue;
            // Register both by concrete type AND by the IActivityExecutor interface.
            // The concrete-type registration lets ActivityRegistry resolve a specific executor
            // via scopedProvider.GetRequiredService(type) without enumerating all executors.
            // The interface registration is forwarded to the same scoped instance, so
            // GetServices<IActivityExecutor>() (used by the bootstrap scan) still works.
            services.AddScoped(type);
            services.AddScoped(executorInterface, sp => (IActivityExecutor)sp.GetRequiredService(type));
        }

        return services;
    }

    /// <summary>
    /// Binds strongly-typed Options POCOs for every Engine-level setting bucket so the
    /// activities can consume <c>IOptions&lt;T&gt;</c> instead of raw <c>IConfiguration</c>
    /// lookups. Call once at startup, after <c>IConfiguration</c> is available.
    /// </summary>
    public static IServiceCollection AddNodePilotEngineOptions(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));
        services.Configure<RestApiProxyOptions>(configuration.GetSection(RestApiProxyOptions.SectionName));
        return services;
    }
}
