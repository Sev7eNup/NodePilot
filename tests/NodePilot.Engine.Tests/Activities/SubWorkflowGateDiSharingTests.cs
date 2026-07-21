using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// Verifies that <c>AddNodePilotActivities</c> registers <see cref="ISubWorkflowGate"/>
/// as a Singleton — the same instance must be handed to <c>StartWorkflowActivity</c>
/// AND <c>ForEachActivity</c>, otherwise the cross-activity back-pressure that the
/// old static-semaphore design provided would silently break.
///
/// This is the regression test the previous static-class design didn't need but the
/// DI-driven design absolutely does.
/// </summary>
public class SubWorkflowGateDiSharingTests
{
    [Fact]
    public void Gate_IsRegistered_AsSingleton()
    {
        var services = new ServiceCollection();
        services.AddNodePilotActivities();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISubWorkflowGate));
        descriptor.Should().NotBeNull("AddNodePilotActivities must register ISubWorkflowGate");
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton,
            "process-wide back-pressure requires a single shared instance");
    }

    [Fact]
    public void Gate_ResolvesToSameInstance_AcrossMultipleScopes()
    {
        var services = new ServiceCollection();
        services.AddNodePilotActivities();
        var provider = services.BuildServiceProvider();

        // Two independent scopes (the engine creates one per step) must still see
        // the same gate — otherwise StartWorkflowActivity in scope-A and
        // ForEachActivity in scope-B would run against separate semaphores.
        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();

        var gateA = scopeA.ServiceProvider.GetRequiredService<ISubWorkflowGate>();
        var gateB = scopeB.ServiceProvider.GetRequiredService<ISubWorkflowGate>();

        gateA.Should().BeSameAs(gateB);
    }

    [Fact]
    public void Gate_IsInMemorySubWorkflowGate_WithDefaultCapacity()
    {
        var services = new ServiceCollection();
        services.AddNodePilotActivities();
        var provider = services.BuildServiceProvider();

        var gate = provider.GetRequiredService<ISubWorkflowGate>();

        gate.Should().BeOfType<InMemorySubWorkflowGate>();
        gate.Capacity.Should().Be(InMemorySubWorkflowGate.DefaultCapacity)
            .And.Subject.Should().Be(128, "tuned in 2026-05-07 stress runs — see InMemorySubWorkflowGate xmldoc");
    }

    [Fact]
    public void Gate_HostCanReplace_BeforeAddNodePilotActivities()
    {
        // The activity registration uses TryAddSingleton, so a host that wants a
        // custom gate (e.g. a future distributed implementation for HA) can register
        // it first and not be overwritten. This test pins that contract.
        var services = new ServiceCollection();
        var customGate = new InMemorySubWorkflowGate(capacity: 4);
        services.AddSingleton<ISubWorkflowGate>(customGate);
        services.AddNodePilotActivities();

        var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<ISubWorkflowGate>();

        resolved.Should().BeSameAs(customGate);
        resolved.Capacity.Should().Be(4);
    }
}
