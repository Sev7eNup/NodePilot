using FluentAssertions;
using NodePilot.ServiceSwitcher.Localization;
using NodePilot.ServiceSwitcher.Models;
using NodePilot.ServiceSwitcher.Services;
using NodePilot.ServiceSwitcher.ViewModels;
using Xunit;

namespace NodePilot.ServiceSwitcher.Tests;

public sealed class StatusBarTests
{
    [Fact]
    public async Task SuccessfulSwitch_UpdatesReadyStateAndLastSwitchTime()
    {
        var strings = new StringCatalog();
        var gateway = StandardGateway();
        var viewModel = CreateViewModel(gateway, strings);

        await viewModel.SwitchAsync(SwitchTarget.SystemCenterOrchestrator);

        viewModel.FooterStatusText.Should().Be(strings.Ready);
        viewModel.FooterStatusIsError.Should().BeFalse();
        viewModel.FooterStatusIsWarning.Should().BeFalse();
        viewModel.FooterDetailText.Should().Be(strings.StateTitle(EnvironmentState.SystemCenterActive));
        viewModel.FooterLastSwitchText.Should().NotBe(strings.NoSuccessfulSwitch);
    }

    [Fact]
    public async Task FailedSwitch_ShowsErrorAndKeepsLastSwitchEmpty()
    {
        var strings = new StringCatalog();
        var gateway = StandardGateway();
        gateway.FailStart = "orunbook";
        var viewModel = CreateViewModel(gateway, strings);

        await viewModel.SwitchAsync(SwitchTarget.SystemCenterOrchestrator);

        viewModel.FooterStatusText.Should().Be(strings.SwitchFailedStatus);
        viewModel.FooterStatusIsError.Should().BeTrue();
        viewModel.FooterDetailText.Should().Be(strings.DetailsInActivityHistory);
        viewModel.FooterLastSwitchText.Should().Be(strings.NoSuccessfulSwitch);
    }

    private static MainWindowViewModel CreateViewModel(
        FakeServiceControlGateway gateway,
        StringCatalog strings)
    {
        var discovery = new ServiceDiscovery(gateway, () => ["NodePilot"]);
        var logger = new RecordingLogger();
        var coordinator = new SwitchCoordinator(
            gateway,
            discovery,
            logger,
            new NoOpWorkloadReconciler(),
            TestServices.FastOptions,
            processes: new FakeProcessPresenceProbe());
        return new MainWindowViewModel(
            coordinator,
            logger,
            new AcceptingUserInteraction(),
            strings);
    }

    private static FakeServiceControlGateway StandardGateway()
    {
        var gateway = new FakeServiceControlGateway();
        gateway.Services["NodePilot"] = TestServices.Service(
            "NodePilot", ServiceRuntimeState.Running, ServiceStartMode.Automatic, delayed: true);
        gateway.Services["omanagement"] = TestServices.Service("omanagement");
        gateway.Services["orunbook"] = TestServices.Service("orunbook", dependencies: ["omanagement"]);
        return gateway;
    }

    private sealed class AcceptingUserInteraction : IUserInteraction
    {
        public Task<bool> ConfirmSwitchAsync(SwitchTarget target, ManagedEnvironmentSnapshot snapshot) =>
            Task.FromResult(true);

        public void ShowError(string error) { }
    }
}
