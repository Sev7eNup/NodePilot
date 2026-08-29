using FluentAssertions;
using NodePilot.ServiceSwitcher.Localization;
using NodePilot.ServiceSwitcher.Models;
using NodePilot.ServiceSwitcher.Services;
using NodePilot.ServiceSwitcher.ViewModels;
using Xunit;

namespace NodePilot.ServiceSwitcher.Tests;

public sealed class ActivityHistoryTests
{
    [Fact]
    public void ActivityLogger_RetainsCompleteSessionHistoryBeyondOneHundredEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"service-switcher-history-{Guid.NewGuid():N}");
        try
        {
            var logger = new ActivityLogger(directory);

            for (var index = 0; index < 150; index++) logger.Info($"Entry {index}");

            logger.Entries.Should().HaveCount(150);
            logger.Entries[0].Message.Should().Be("Entry 0");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ViewModel_ExposesEveryAvailableActivityEntry()
    {
        var logger = new RecordingLogger();
        for (var index = 0; index < 150; index++) logger.Info($"Entry {index}");
        var gateway = new FakeServiceControlGateway();
        gateway.Services["NodePilot"] = TestServices.Service(
            "NodePilot",
            ServiceRuntimeState.Running,
            ServiceStartMode.Automatic,
            delayed: true);
        var discovery = new ServiceDiscovery(gateway, () => ["NodePilot"]);
        var coordinator = new SwitchCoordinator(
            gateway, discovery, logger, new NoOpWorkloadReconciler(), TestServices.FastOptions);
        var viewModel = new MainWindowViewModel(
            coordinator,
            logger,
            new AcceptingUserInteraction(),
            new StringCatalog());

        await viewModel.RefreshAsync();

        viewModel.ActivityItems.Should().HaveCount(151);
        viewModel.ActivityItems.Should().Contain(item => item.Message.Contains("Entry 0", StringComparison.Ordinal));
        viewModel.ActivityItems.Should().Contain(item =>
            item.Message.Contains("System-Check", StringComparison.OrdinalIgnoreCase)
            || item.Message.Contains("System check", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ViewModel_ExposesSuccessAndErrorActivitySemantics()
    {
        var logger = new RecordingLogger();
        var gateway = new FakeServiceControlGateway();
        var discovery = new ServiceDiscovery(gateway, () => ["NodePilot"]);
        var coordinator = new SwitchCoordinator(
            gateway, discovery, logger, new NoOpWorkloadReconciler(), TestServices.FastOptions);
        var viewModel = new MainWindowViewModel(
            coordinator,
            logger,
            new AcceptingUserInteraction(),
            new StringCatalog());

        logger.Success("Switch completed successfully.");
        logger.Error("Switch failed.");

        viewModel.ActivityItems.Should().ContainSingle(item =>
            item.Message == "Switch completed successfully." && item.IsSuccess && !item.IsError);
        viewModel.ActivityItems.Should().ContainSingle(item =>
            item.Message == "Switch failed." && item.IsError && !item.IsSuccess);
    }

    private sealed class AcceptingUserInteraction : IUserInteraction
    {
        public Task<bool> ConfirmSwitchAsync(SwitchTarget target, ManagedEnvironmentSnapshot snapshot) =>
            Task.FromResult(true);

        public void ShowError(string error) { }
    }
}
