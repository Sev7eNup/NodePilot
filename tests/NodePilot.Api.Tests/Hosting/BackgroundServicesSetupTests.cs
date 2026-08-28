using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    /// from the root provider stays referenced by that provider until process exit and would
    /// be disposed a second time at shutdown. <see cref="TriggerOrchestrator"/> constructs
    /// them with <c>new</c> instead.
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

    [Fact]
    public void AddNodePilotBackgroundServices_RegistersExactlyOneDatabaseRecoveryAuditService()
    {
        var services = new ServiceCollection().AddNodePilotBackgroundServices();

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(DatabaseRecoveryAuditService));
    }

    [Fact]
    public void AddNodePilotBackgroundServices_StartsRecoveryAuditSubscriberBeforeDatabaseProbe()
    {
        var services = new ServiceCollection().AddNodePilotBackgroundServices();
        var hostedTypes = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)
            .ToList();

        hostedTypes.Should().Contain(typeof(DatabaseAvailabilityProbe));
        hostedTypes.IndexOf(typeof(DatabaseRecoveryAuditService)).Should().BeLessThan(
            hostedTypes.IndexOf(typeof(DatabaseAvailabilityProbe)),
            "the audit subscriber must be attached before the probe can publish an early recovery");
    }
}
