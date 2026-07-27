using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Api.Hosting;
using NodePilot.Scheduler;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// Composition guards for <see cref="BackgroundServicesSetup"/>.
/// </summary>
public sealed class BackgroundServicesSetupTests
{
    /// <summary>
    /// No <see cref="ITriggerSource"/> may be registered in the container. Sources are
    /// IAsyncDisposable with an orchestrator-owned lifetime; a transient disposable resolved
    /// from the root provider is tracked by that provider until process exit, so every source
    /// the reconcile loop ever created would stay referenced (growing with each trigger
    /// add/update and each backoff retry) and be disposed a second time at shutdown.
    /// <see cref="TriggerOrchestrator"/> constructs them with <c>new</c> instead.
    /// </summary>
    [Fact]
    public void AddNodePilotBackgroundServices_RegistersNoTriggerSource()
    {
        var services = new ServiceCollection().AddNodePilotBackgroundServices();

        var triggerSources = services
            .Where(d => typeof(ITriggerSource).IsAssignableFrom(d.ServiceType)
                     || (d.ImplementationType is not null && typeof(ITriggerSource).IsAssignableFrom(d.ImplementationType)))
            .Select(d => d.ServiceType.Name)
            .ToList();

        triggerSources.Should().BeEmpty(
            "trigger sources are owned and disposed by the TriggerOrchestrator, not by the container");
    }
}
