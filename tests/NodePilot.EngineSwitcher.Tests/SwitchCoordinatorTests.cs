using FluentAssertions;
using NodePilot.EngineSwitcher.Configuration;
using NodePilot.EngineSwitcher.Models;
using NodePilot.EngineSwitcher.Services;
using Xunit;

namespace NodePilot.EngineSwitcher.Tests;

public sealed class SwitchCoordinatorTests
{
    [Fact]
    public async Task SwitchToSystemCenter_StopsNodePilotBeforeStartingTargetAndPersistsModes()
    {
        var gateway = StandardGateway();
        var coordinator = Coordinator(gateway);

        var result = await coordinator.SwitchAsync(
            SwitchTarget.SystemCenterOrchestrator,
            progress: null,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        gateway.Services["NodePilot"].State.Should().Be(ServiceRuntimeState.Stopped);
        gateway.Services["NodePilot"].StartMode.Should().Be(ServiceStartMode.Manual);
        gateway.Services["omanagement"].State.Should().Be(ServiceRuntimeState.Running);
        gateway.Services["orunbook"].State.Should().Be(ServiceRuntimeState.Running);
        gateway.Services["omanagement"].StartMode.Should().Be(ServiceStartMode.Automatic);

        gateway.Operations.IndexOf("stop:NodePilot")
            .Should().BeLessThan(gateway.Operations.IndexOf("start:omanagement"));
        gateway.Operations.IndexOf("start:omanagement")
            .Should().BeLessThan(gateway.Operations.IndexOf("start:orunbook"));
    }

    [Fact]
    public async Task SwitchToNodePilot_StopsSystemCenterInReverseDependencyOrderAndUsesDelayedStart()
    {
        var gateway = StandardGateway();
        gateway.Services["NodePilot"] = gateway.Services["NodePilot"] with { State = ServiceRuntimeState.Stopped };
        gateway.Services["omanagement"] = gateway.Services["omanagement"] with { State = ServiceRuntimeState.Running };
        gateway.Services["orunbook"] = gateway.Services["orunbook"] with { State = ServiceRuntimeState.Running };

        var result = await Coordinator(gateway).SwitchAsync(
            SwitchTarget.NodePilot,
            progress: null,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        gateway.Operations.Should().Contain("force-stop:omonitor");
        gateway.Operations.Should().NotContain("stop:omonitor");
        gateway.Operations.IndexOf("stop:orunbook")
            .Should().BeLessThan(gateway.Operations.IndexOf("force-stop:omonitor"));
        gateway.Operations.IndexOf("force-stop:omonitor")
            .Should().BeLessThan(gateway.Operations.IndexOf("stop:omanagement"));
        gateway.Services["NodePilot"].State.Should().Be(ServiceRuntimeState.Running);
        gateway.Services["NodePilot"].StartMode.Should().Be(ServiceStartMode.Automatic);
        gateway.Services["NodePilot"].DelayedAutoStart.Should().BeTrue();
    }

    [Fact]
    public async Task TargetStartFailure_StopsPartialTargetAndLeavesEverythingManual()
    {
        var gateway = StandardGateway();
        gateway.FailStart = "orunbook";

        var result = await Coordinator(gateway).SwitchAsync(
            SwitchTarget.SystemCenterOrchestrator,
            progress: null,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        gateway.Services.Values.Should().OnlyContain(service => service.State == ServiceRuntimeState.Stopped);
        gateway.Services.Values.Should().OnlyContain(service => service.StartMode == ServiceStartMode.Manual);
    }

    [Fact]
    public async Task SourceStopFailure_NeverStartsTarget()
    {
        var gateway = StandardGateway();
        gateway.FailStop = "NodePilot";

        var result = await Coordinator(gateway).SwitchAsync(
            SwitchTarget.SystemCenterOrchestrator,
            progress: null,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        gateway.Operations.Should().NotContain(operation => operation.StartsWith("start:omanagement"));
        gateway.Operations.Should().NotContain(operation => operation.StartsWith("start:orunbook"));
    }

    [Fact]
    public async Task AllowListPreflightFailure_DoesNotMutateAnyService()
    {
        var gateway = StandardGateway();
        var workloads = new ThrowingWorkloadReconciler();
        var discovery = new ServiceDiscovery(gateway, () => ["NodePilot"]);
        var coordinator = new SwitchCoordinator(
            gateway,
            discovery,
            new RecordingLogger(),
            workloads,
            TestServices.FastOptions);

        var result = await coordinator.SwitchAsync(
            SwitchTarget.SystemCenterOrchestrator,
            progress: null,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        gateway.Operations.Should().BeEmpty();
        workloads.ReconcileCalled.Should().BeFalse();
    }

    [Fact]
    public async Task OpenRunbookDesigner_BlocksNodePilotSwitchBeforeAnyServiceMutation()
    {
        var gateway = StandardGateway();
        var discovery = new ServiceDiscovery(gateway, () => ["NodePilot"]);
        var coordinator = new SwitchCoordinator(
            gateway,
            discovery,
            new RecordingLogger(),
            new NoOpWorkloadReconciler(),
            TestServices.FastOptions,
            processes: new FakeProcessPresenceProbe("RunbookDesigner"));

        var result = await coordinator.SwitchAsync(
            SwitchTarget.NodePilot,
            progress: null,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("Runbook Designer");
        gateway.Operations.Should().BeEmpty();
    }

    [Fact]
    public void OrderByDependencies_UsesDependencyOrderInBothDirections()
    {
        var management = TestServices.Service("omanagement");
        var monitor = TestServices.Service("omonitor", dependencies: ["omanagement"]);
        var runbook = TestServices.Service("orunbook", dependencies: ["omonitor"]);
        ServiceSnapshot[] unordered = [runbook, management, monitor];

        SwitchCoordinator.OrderByDependencies(unordered, dependenciesFirst: true)
            .Select(service => service.Name).Should().Equal("omanagement", "omonitor", "orunbook");
        SwitchCoordinator.OrderByDependencies(unordered, dependenciesFirst: false)
            .Select(service => service.Name).Should().Equal("orunbook", "omonitor", "omanagement");
    }

    private static FakeServiceControlGateway StandardGateway()
    {
        var gateway = new FakeServiceControlGateway();
        gateway.Services["NodePilot"] = TestServices.Service(
            "NodePilot", ServiceRuntimeState.Running, ServiceStartMode.Automatic, delayed: true);
        gateway.Services["omanagement"] = TestServices.Service("omanagement");
        gateway.Services["omonitor"] = TestServices.Service("omonitor", dependencies: ["omanagement"]);
        gateway.Services["orunbook"] = TestServices.Service("orunbook", dependencies: ["omonitor"]);
        return gateway;
    }

    // A failed query changed nothing, so stopping every managed service would be an over-reaction.
    [Fact]
    public async Task DeactivationFailureBeforeAnyChange_LeavesEveryServiceUntouched()
    {
        var gateway = StandardGateway();
        var workloads = new FailingDeactivationReconciler(reportMutation: false);
        var coordinator = Coordinator(gateway, workloads);

        var result = await coordinator.SwitchAsync(SwitchTarget.NodePilot, progress: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("SCOrch returned a malformed");
        gateway.Operations.Should().BeEmpty();
        gateway.Services["omanagement"].StartMode.Should().Be(ServiceStartMode.Manual);
    }

    [Fact]
    public async Task DeactivationFailureAfterTheFirstChange_RunsFailClosedCleanup()
    {
        var gateway = StandardGateway();
        var coordinator = Coordinator(gateway, new FailingDeactivationReconciler(reportMutation: true));

        var result = await coordinator.SwitchAsync(SwitchTarget.NodePilot, progress: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        gateway.Operations.Should().Contain("stop:NodePilot");
        gateway.Services.Values.Should().OnlyContain(service => service.StartMode == ServiceStartMode.Manual);
    }

    private static SwitchCoordinator Coordinator(FakeServiceControlGateway gateway) =>
        Coordinator(gateway, new NoOpWorkloadReconciler());

    private static SwitchCoordinator Coordinator(
        FakeServiceControlGateway gateway,
        IWorkloadReconciler workloads)
    {
        var discovery = new ServiceDiscovery(gateway, () => ["NodePilot"]);
        return new SwitchCoordinator(
            gateway,
            discovery,
            new RecordingLogger(),
            workloads,
            TestServices.FastOptions,
            processes: new FakeProcessPresenceProbe());
    }

    private sealed class FailingDeactivationReconciler(bool reportMutation) : IWorkloadReconciler
    {
        private static readonly SwitcherConfiguration Configuration = new(
            new NodePilotWorkloadConfiguration(string.Empty, string.Empty),
            new ScorchWorkloadConfiguration(string.Empty, "http://localhost"));

        public Task<WorkloadSwitchPlan> PreflightAsync(SwitchTarget target, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkloadSwitchPlan(target, Configuration, []));

        public Task DeactivateSourceAsync(
            WorkloadSwitchPlan plan,
            IProgress<SwitchProgress>? progress,
            Action onMutationStarted,
            CancellationToken cancellationToken)
        {
            if (reportMutation) onMutationStarted();
            throw new InvalidOperationException("SCOrch returned a malformed ScorchJob response from http://localhost:81/api/jobs");
        }

        public Task ReconcileAsync(
            WorkloadSwitchPlan plan,
            IProgress<SwitchProgress>? progress,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingWorkloadReconciler : IWorkloadReconciler
    {
        public bool ReconcileCalled { get; private set; }
        public Task<WorkloadSwitchPlan> PreflightAsync(SwitchTarget target, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("UNC share unavailable");
        public Task DeactivateSourceAsync(WorkloadSwitchPlan plan, IProgress<SwitchProgress>? progress, Action onMutationStarted, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task ReconcileAsync(WorkloadSwitchPlan plan, IProgress<SwitchProgress>? progress, CancellationToken cancellationToken)
        {
            ReconcileCalled = true;
            return Task.CompletedTask;
        }
    }
}
