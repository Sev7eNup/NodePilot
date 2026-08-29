using System.Text.Json;
using FluentAssertions;
using NodePilot.ServiceSwitcher.Configuration;
using Xunit;

namespace NodePilot.ServiceSwitcher.Tests;

public sealed class SwitcherConfigurationLoaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"switcher-config-{Guid.NewGuid():N}");

    public SwitcherConfigurationLoaderTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Load_UsesExplicitConfigAndResolvesCliRelativeToIt()
    {
        var cli = Path.Combine(_directory, "np.exe");
        File.WriteAllText(cli, string.Empty);
        var config = Path.Combine(_directory, "custom.json");
        WriteValidConfiguration(config, "np.exe");
        var loader = new SwitcherConfigurationLoader(
            ["switcher.exe", "--config", config],
            Path.Combine(_directory, "program-data"),
            Path.Combine(_directory, "app"));

        var loaded = loader.Load();

        loaded.NodePilot.CliPath.Should().Be(Path.GetFullPath(cli));
        loaded.NodePilot.WorkflowAllowListPath.Should().Be(@"\\server\share\nodepilot.txt");
    }

    [Fact]
    public void Load_RejectsRemoteScorchApiOverHttpBecauseWindowsCredentialsWouldBeExposed()
    {
        var cli = Path.Combine(_directory, "np.exe");
        File.WriteAllText(cli, string.Empty);
        var config = Path.Combine(_directory, "custom.json");
        WriteValidConfiguration(config, "np.exe", "http://scorch.example.test:81");
        var loader = new SwitcherConfigurationLoader(
            ["switcher.exe", "--config", config], _directory, _directory);

        var loaded = loader.Load();
        var action = () => SwitcherConfigurationValidator.ValidateScorch(loaded.SystemCenterOrchestrator);

        action.Should().Throw<InvalidOperationException>().WithMessage("*must use HTTPS*");
    }

    [Fact]
    public void Load_AcceptsAbsoluteLocalAllowListPaths()
    {
        var nodePilotAllowList = Path.Combine(_directory, "nodepilot.txt");
        var scorchAllowList = Path.Combine(_directory, "scorch.txt");
        var config = Path.Combine(_directory, "custom.json");
        WriteValidConfiguration(config, "np.exe", nodePilotAllowList: nodePilotAllowList, scorchAllowList: scorchAllowList);
        var loader = new SwitcherConfigurationLoader(
            ["switcher.exe", "--config", config], _directory, _directory);

        var loaded = loader.Load();

        loaded.NodePilot.WorkflowAllowListPath.Should().Be(nodePilotAllowList);
        loaded.SystemCenterOrchestrator.RunbookAllowListPath.Should().Be(scorchAllowList);
    }

    [Theory]
    [InlineData("nodepilot.txt", @"\\server\share\scorch.txt")]
    [InlineData(@"\\server\share\nodepilot.txt", "scorch.txt")]
    public void Load_RejectsRelativeAllowListPaths(string nodePilotAllowList, string scorchAllowList)
    {
        var config = Path.Combine(_directory, "custom.json");
        WriteValidConfiguration(config, "np.exe", nodePilotAllowList: nodePilotAllowList, scorchAllowList: scorchAllowList);
        var loader = new SwitcherConfigurationLoader(
            ["switcher.exe", "--config", config], _directory, _directory);

        var action = loader.Load;

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*must be an absolute local or UNC path*");
    }

    private static void WriteValidConfiguration(
        string path,
        string cliPath,
        string apiBaseUrl = "http://localhost:81",
        string nodePilotAllowList = @"\\server\share\nodepilot.txt",
        string scorchAllowList = @"\\server\share\scorch.txt")
    {
        var value = new
        {
            nodePilot = new
            {
                workflowAllowListPath = nodePilotAllowList,
                cliPath,
                profile = "switcher",
            },
            systemCenterOrchestrator = new
            {
                runbookAllowListPath = scorchAllowList,
                apiBaseUrl,
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(value));
    }
}
