using FluentAssertions;
using NodePilot.Switcher.Configuration;
using NodePilot.Switcher.Localization;
using NodePilot.Switcher.Models;
using NodePilot.Switcher.Services;
using NodePilot.Switcher.ViewModels;
using Xunit;

namespace NodePilot.Switcher.Tests;

public sealed class SwitcherConfigurationProbeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"switcher-probe-{Guid.NewGuid():N}");

    public SwitcherConfigurationProbeTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Probe_WithLoadableConfiguration_ReportsNoError()
    {
        var config = WriteConfiguration("""
            {
              "nodePilot": {
                "workflowAllowListPath": "D:\\lists\\nodepilot.txt",
                "cliPath": "..\\np\\np.exe",
                "profile": "switcher"
              },
              "systemCenterOrchestrator": {
                "runbookAllowListPath": "D:\\lists\\scorch.txt",
                "apiBaseUrl": "http://localhost:81"
              }
            }
            """);

        CreateProbe(config).Probe().Should().BeNull();
    }

    [Fact]
    public void Probe_WithUnreadableConfiguration_ReportsTheReason()
    {
        var config = WriteConfiguration("{ \"nodePilot\": ");

        CreateProbe(config).Probe().Should().Contain("could not be read").And.Contain(config);
    }

    [Fact]
    public void Probe_WithMissingConfiguration_ReportsTheReason()
    {
        var config = Path.Combine(_directory, "nowhere.json");

        CreateProbe(config).Probe().Should().Contain("not found");
    }

    [Fact]
    public async Task RefreshAsync_WithUnusableConfiguration_BlocksBothSwitchesAndReportsIt()
    {
        var strings = new StringCatalog();
        var logger = new RecordingLogger();
        var probe = new FakeConfigurationProbe { Error = "Switcher configuration could not be read: broken" };
        var viewModel = CreateViewModel(logger, probe, strings, new AcceptingUserInteraction());

        await viewModel.RefreshAsync();

        viewModel.CanSwitchToNodePilot.Should().BeFalse();
        viewModel.CanSwitchToSystemCenter.Should().BeFalse();
        viewModel.FooterStatusIsError.Should().BeTrue();
        viewModel.FooterStatusText.Should().Be(strings.ConfigurationInvalidStatus);
        viewModel.FooterDetailText.Should().Be(probe.Error);
        logger.Entries.Should().ContainSingle(entry => entry.Level == "ERROR")
            .Which.Message.Should().Contain("broken");
        logger.Entries.Should().NotContain(entry => entry.Message == strings.SystemCheckCompleted);
    }

    [Fact]
    public async Task RefreshAsync_WithTheSameConfigurationErrorTwice_LogsItOnce()
    {
        var logger = new RecordingLogger();
        var probe = new FakeConfigurationProbe { Error = "Switcher configuration could not be read: broken" };
        var viewModel = CreateViewModel(logger, probe, new StringCatalog(), new AcceptingUserInteraction());

        await viewModel.RefreshAsync();
        await viewModel.RefreshAsync();

        logger.Entries.Should().ContainSingle(entry => entry.Level == "ERROR");
    }

    [Fact]
    public async Task RefreshAsync_AfterTheConfigurationIsRepaired_ReleasesTheSwitchAndLogsTheSystemCheck()
    {
        var strings = new StringCatalog();
        var logger = new RecordingLogger();
        var probe = new FakeConfigurationProbe { Error = "broken" };
        var viewModel = CreateViewModel(logger, probe, strings, new AcceptingUserInteraction());
        await viewModel.RefreshAsync();

        probe.Error = null;
        await viewModel.RefreshAsync();

        viewModel.CanSwitchToSystemCenter.Should().BeTrue();
        viewModel.FooterStatusIsError.Should().BeFalse();
        logger.Entries.Should().Contain(entry => entry.Message == strings.SystemCheckCompleted);
    }

    // The file can break between two refreshes, so an enabled button is not a guarantee.
    [Fact]
    public async Task SwitchAsync_WithUnusableConfiguration_NeverAsksForConfirmation()
    {
        var logger = new RecordingLogger();
        var interaction = new RecordingUserInteraction();
        var probe = new FakeConfigurationProbe { Error = "broken" };
        var viewModel = CreateViewModel(logger, probe, new StringCatalog(), interaction);

        await viewModel.SwitchAsync(SwitchTarget.SystemCenterOrchestrator);

        interaction.Confirmations.Should().Be(0);
        interaction.Errors.Should().ContainSingle().Which.Should().Be("broken");
        logger.Entries.Should().NotContain(entry => entry.Message.StartsWith("Switch to", StringComparison.Ordinal));
    }

    private string WriteConfiguration(string content)
    {
        var path = Path.Combine(_directory, "switcher.json");
        File.WriteAllText(path, content);
        return path;
    }

    private SwitcherConfigurationProbe CreateProbe(string configurationPath) =>
        new(new SwitcherConfigurationLoader(
            ["switcher.exe", "--config", configurationPath], _directory, _directory));

    private static MainWindowViewModel CreateViewModel(
        RecordingLogger logger,
        ISwitcherConfigurationProbe probe,
        StringCatalog strings,
        IUserInteraction interaction)
    {
        var gateway = new FakeServiceControlGateway();
        gateway.Services["NodePilot"] = TestServices.Service(
            "NodePilot", ServiceRuntimeState.Running, ServiceStartMode.Automatic, delayed: true);
        gateway.Services["omanagement"] = TestServices.Service("omanagement");
        var discovery = new ServiceDiscovery(gateway, () => ["NodePilot"]);
        var coordinator = new SwitchCoordinator(
            gateway,
            discovery,
            logger,
            new NoOpWorkloadReconciler(),
            TestServices.FastOptions,
            processes: new FakeProcessPresenceProbe());
        return new MainWindowViewModel(coordinator, logger, interaction, probe, strings);
    }

    private sealed class AcceptingUserInteraction : IUserInteraction
    {
        public Task<bool> ConfirmSwitchAsync(SwitchTarget target, ManagedEnvironmentSnapshot snapshot) =>
            Task.FromResult(true);

        public void ShowError(string error) { }
    }

    private sealed class RecordingUserInteraction : IUserInteraction
    {
        public int Confirmations { get; private set; }
        public List<string> Errors { get; } = [];

        public Task<bool> ConfirmSwitchAsync(SwitchTarget target, ManagedEnvironmentSnapshot snapshot)
        {
            Confirmations++;
            return Task.FromResult(true);
        }

        public void ShowError(string error) => Errors.Add(error);
    }
}
