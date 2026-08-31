using System.Text.Json;
using FluentAssertions;
using NodePilot.EngineSwitcher.Configuration;
using Xunit;

namespace NodePilot.EngineSwitcher.Tests;

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

    // Nothing in the product ever creates the machine-wide file, so a switcher that names only
    // that path sends the operator to the one location that is always empty.
    [Fact]
    public void Load_PrefersMachineWideConfigurationOverTheOneNextToTheExecutable()
    {
        var programData = CreateDirectory("program-data");
        var application = CreateDirectory("app");
        WriteValidConfiguration(MachineConfigPath(programData), "np.exe", nodePilotAllowList: @"\\server\share\machine.txt");
        WriteValidConfiguration(Path.Combine(application, "engine-switcher.json"), "np.exe");
        File.WriteAllText(Path.Combine(SwitcherDirectory(programData), "np.exe"), string.Empty);

        var loaded = new SwitcherConfigurationLoader(["switcher.exe"], programData, application).Load();

        loaded.NodePilot.WorkflowAllowListPath.Should().Be(@"\\server\share\machine.txt");
    }

    [Fact]
    public void Load_FallsBackToTheConfigurationNextToTheExecutable()
    {
        var programData = CreateDirectory("program-data");
        var application = CreateDirectory("app");
        WriteValidConfiguration(Path.Combine(application, "engine-switcher.json"), "np.exe");
        File.WriteAllText(Path.Combine(application, "np.exe"), string.Empty);

        var loaded = new SwitcherConfigurationLoader(["switcher.exe"], programData, application).Load();

        loaded.NodePilot.CliPath.Should().Be(Path.Combine(application, "np.exe"));
    }

    [Fact]
    public void Load_WithoutAnyConfigurationNamesEveryLocationItChecked()
    {
        var programData = CreateDirectory("program-data");
        var application = CreateDirectory("app");

        var action = new SwitcherConfigurationLoader(["switcher.exe"], programData, application).Load;

        action.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MachineConfigPath(programData))
            .And.Contain(Path.Combine(application, "engine-switcher.json"));
    }

    [Fact]
    public void Load_WithMissingExplicitConfigurationNamesThatPath()
    {
        var missing = Path.Combine(_directory, "nowhere.json");

        var action = new SwitcherConfigurationLoader(
            ["switcher.exe", "--config", missing], _directory, _directory).Load;

        action.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(missing);
    }

    // The shipped template cannot carry a usable cliPath: it is resolved relative to the
    // configuration file, which lands in three different places.
    [Fact]
    public void Load_WithoutCliPathResolvesTheInstalledCli()
    {
        var installed = Path.Combine(_directory, "installed-np.exe");
        File.WriteAllText(installed, string.Empty);
        var config = Path.Combine(_directory, "custom.json");
        WriteValidConfiguration(config, cliPath: null);

        var loaded = new SwitcherConfigurationLoader(
            ["switcher.exe", "--config", config], _directory, _directory,
            () => [Path.Combine(_directory, "absent-np.exe"), installed]).Load();

        loaded.NodePilot.CliPath.Should().Be(installed);
    }

    [Fact]
    public void Load_WithoutCliPathAndWithoutAnInstallationNamesTheCandidates()
    {
        var candidate = Path.Combine(_directory, "absent-np.exe");
        var config = Path.Combine(_directory, "custom.json");
        WriteValidConfiguration(config, cliPath: null);

        var action = new SwitcherConfigurationLoader(
            ["switcher.exe", "--config", config], _directory, _directory, () => [candidate]).Load;

        action.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("NodePilot CLI not found").And.Contain(candidate);
    }

    [Fact]
    public void Load_WithAnExplicitCliPathIgnoresTheInstalledCli()
    {
        var configured = Path.Combine(_directory, "np.exe");
        File.WriteAllText(configured, string.Empty);
        var config = Path.Combine(_directory, "custom.json");
        WriteValidConfiguration(config, "np.exe");

        var loaded = new SwitcherConfigurationLoader(
            ["switcher.exe", "--config", config], _directory, _directory,
            () => [Path.Combine(_directory, "installed-np.exe")]).Load();

        loaded.NodePilot.CliPath.Should().Be(configured);
    }

    [Fact]
    public void Load_AcceptsSingleBackslashWindowsPaths()
    {
        const string allowList = @"D:\PaketeSCCM\SCCM_Scripts\Runbooks2Run_ITS.txt";
        var config = Path.Combine(_directory, "custom.json");
        WriteRawConfiguration(config, allowList, allowList, @"..\np\np.exe");

        var loaded = new SwitcherConfigurationLoader(
            ["switcher.exe", "--config", config], _directory, _directory).Load();

        loaded.NodePilot.WorkflowAllowListPath.Should().Be(allowList);
        loaded.SystemCenterOrchestrator.RunbookAllowListPath.Should().Be(allowList);
    }

    // The escaped form is the correct one and must keep loading exactly as before.
    [Fact]
    public void Load_KeepsEscapedBackslashPathsUnchanged()
    {
        var config = Path.Combine(_directory, "custom.json");
        File.WriteAllText(config, """
            {
              "nodePilot": {
                "workflowAllowListPath": "D:\\Pakete\\nodepilot.txt",
                "cliPath": "..\\np\\np.exe",
                "profile": "switcher"
              },
              "systemCenterOrchestrator": {
                "runbookAllowListPath": "\\\\server\\share\\scorch.txt",
                "apiBaseUrl": "http://localhost:81"
              }
            }
            """);

        var loaded = new SwitcherConfigurationLoader(
            ["switcher.exe", "--config", config], _directory, _directory).Load();

        loaded.NodePilot.WorkflowAllowListPath.Should().Be(@"D:\Pakete\nodepilot.txt");
        loaded.SystemCenterOrchestrator.RunbookAllowListPath.Should().Be(@"\\server\share\scorch.txt");
        loaded.NodePilot.CliPath.Should().Be(Path.GetFullPath(@"..\np\np.exe", _directory));
    }

    // Every one of these segments starts with a letter that is a valid JSON escape, so a repair that
    // only fixes invalid escapes would silently turn them into control characters.
    [Theory]
    [InlineData("new")]
    [InlineData("temp")]
    [InlineData("bin")]
    [InlineData("files")]
    [InlineData("reports")]
    [InlineData("users")]
    public void Load_AcceptsSingleBackslashPathsWhoseSegmentsLookLikeEscapes(string segment)
    {
        var allowList = $@"D:\{segment}\list.txt";
        var config = Path.Combine(_directory, "custom.json");
        WriteRawConfiguration(config, allowList, allowList, @"..\np\np.exe");

        var loaded = new SwitcherConfigurationLoader(
            ["switcher.exe", "--config", config], _directory, _directory).Load();

        loaded.NodePilot.WorkflowAllowListPath.Should().Be(allowList);
    }

    // Both allowlist paths must be fully qualified, so a single leading backslash can only mean UNC.
    [Fact]
    public void Load_PromotesAHandWrittenUncAllowListPath()
    {
        var config = Path.Combine(_directory, "custom.json");
        WriteRawConfiguration(config, @"\\server\share\nodepilot.txt", @"\\server\share\scorch.txt", @"..\np\np.exe");

        var loaded = new SwitcherConfigurationLoader(
            ["switcher.exe", "--config", config], _directory, _directory).Load();

        loaded.NodePilot.WorkflowAllowListPath.Should().Be(@"\\server\share\nodepilot.txt");
        loaded.SystemCenterOrchestrator.RunbookAllowListPath.Should().Be(@"\\server\share\scorch.txt");
    }

    // The CLI path may be relative, so a leading backslash stays drive-root-relative.
    [Fact]
    public void Load_KeepsADriveRootRelativeCliPath()
    {
        var config = Path.Combine(_directory, "custom.json");
        WriteRawConfiguration(config, @"D:\lists\nodepilot.txt", @"D:\lists\scorch.txt", @"\tools\np\np.exe");

        var loaded = new SwitcherConfigurationLoader(
            ["switcher.exe", "--config", config], _directory, _directory).Load();

        loaded.NodePilot.CliPath.Should().Be(Path.GetFullPath(@"\tools\np\np.exe", _directory));
        loaded.NodePilot.CliPath.Should().NotStartWith(@"\\");
    }

    // Tolerance is limited to the path properties; anywhere else a stray backslash stays an error.
    [Fact]
    public void Load_RejectsAStrayBackslashOutsideThePathProperties()
    {
        var config = Path.Combine(_directory, "custom.json");
        WriteRawConfiguration(config, @"D:\lists\nodepilot.txt", @"D:\lists\scorch.txt", @"..\np\np.exe", profile: @"ops\prod");

        var action = new SwitcherConfigurationLoader(
            ["switcher.exe", "--config", config], _directory, _directory).Load;

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Switcher configuration could not be read*");
    }

    [Fact]
    public void Load_WithUnreadableConfigurationNamesThePath()
    {
        var config = Path.Combine(_directory, "custom.json");
        File.WriteAllText(config, "{ \"nodePilot\": ");

        var action = new SwitcherConfigurationLoader(
            ["switcher.exe", "--config", config], _directory, _directory).Load;

        action.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(config);
    }

    [Fact]
    public void Load_ParsesTheShippedTemplate()
    {
        var template = Path.Combine(
            FindRepoRoot(), "src", "NodePilot.EngineSwitcher", "engine-switcher.json");
        var cli = Path.Combine(_directory, "np.exe");
        File.WriteAllText(cli, string.Empty);

        var loaded = new SwitcherConfigurationLoader(
            ["switcher.exe", "--config", template], _directory, _directory, () => [cli]).Load();

        loaded.NodePilot.WorkflowAllowListPath.Should()
            .Be(@"C:\ProgramData\NodePilot\EngineSwitcher\nodepilot-workflows.txt");
        loaded.SystemCenterOrchestrator.RunbookAllowListPath.Should()
            .Be(@"\\server\share\scorch-runbooks.txt");
        loaded.SystemCenterOrchestrator.ActiveJobsPath.Should().Be(
            "api/jobs?$select=Id,RunbookId,Status&$filter=Status eq 'Pending' or Status eq 'Running'");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        throw new InvalidOperationException($"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
    }

    private string CreateDirectory(string name)
    {
        var path = Path.Combine(_directory, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string SwitcherDirectory(string programData)
    {
        var path = Path.Combine(programData, "NodePilot", "EngineSwitcher");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string MachineConfigPath(string programData) =>
        Path.Combine(SwitcherDirectory(programData), "engine-switcher.json");

    private static void WriteValidConfiguration(
        string path,
        string? cliPath = null,
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

    /// <summary>Writes the values verbatim, the way an operator edits the file by hand.</summary>
    private static void WriteRawConfiguration(
        string path,
        string nodePilotAllowList,
        string scorchAllowList,
        string cliPath,
        string profile = "switcher")
    {
        File.WriteAllText(path, $$"""
            {
              "nodePilot": {
                "workflowAllowListPath": "{{nodePilotAllowList}}",
                "cliPath": "{{cliPath}}",
                "profile": "{{profile}}"
              },
              "systemCenterOrchestrator": {
                "runbookAllowListPath": "{{scorchAllowList}}",
                "apiBaseUrl": "http://localhost:81"
              }
            }
            """);
    }
}
