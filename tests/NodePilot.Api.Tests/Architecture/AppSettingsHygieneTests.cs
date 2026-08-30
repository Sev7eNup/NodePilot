using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

/// <summary>
/// Guards two properties of the shipped developer configuration that a stranger cloning this
/// repository depends on.
///
/// <para>No machine-specific paths in tracked config: <c>appsettings.Development.json</c> must
/// not carry an absolute path like <c>"SourceCodeRootPath": "e:\\NodePilot"</c>, since that only
/// works on one checkout. The source-knowledge reader fails silently when the directory is
/// missing, so a stray absolute path leaves the feature quietly dead on every other clone.
/// Machine paths belong in the gitignored <c>appsettings.runtime.json</c>, which is why that
/// file is excluded here rather than scanned.</para>
///
/// <para>One dev port: <c>launchSettings.json</c> must bind the same port every doc and the
/// Vite dev-server proxy expect. A mismatch sends a bare <c>dotnet run</c> to a port the
/// frontend never talks to, and every API call fails with no visible cause.</para>
/// </summary>
public sealed class AppSettingsHygieneTests
{
    /// <summary>Drive-letter absolute paths inside JSON strings, e.g. "e:\\NodePilot" or
    /// "C:/foo".</summary>
    private static readonly Regex AbsoluteWindowsPath = new(@"""[A-Za-z]:(\\\\|/)", RegexOptions.Compiled);

    private static readonly Regex LocalhostPort = new(@"http://localhost:(\d+)", RegexOptions.Compiled);

    [Fact]
    public void ApiProject_PublishItems_ExcludeLocalAndDevelopmentSettingsButKeepBaseSettings()
    {
        var apiDirectory = Path.Combine(FindRepoRoot(), "src", "NodePilot.Api");
        var project = XDocument.Load(Path.Combine(apiDirectory, "NodePilot.Api.csproj"));

        var excludedFromPublish = project.Descendants("Content")
            .Where(item => string.Equals(
                item.Attribute("CopyToPublishDirectory")?.Value
                    ?? item.Element("CopyToPublishDirectory")?.Value,
                "Never",
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => (item.Attribute("Update")?.Value ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();

        excludedFromPublish.Should().ContainEquivalentOf("appsettings.Development.json");
        excludedFromPublish.Should().ContainEquivalentOf("appsettings.runtime.json");
        excludedFromPublish.Should().NotContainEquivalentOf("appsettings.json");
        File.Exists(Path.Combine(apiDirectory, "appsettings.json")).Should().BeTrue(
            "the environment-neutral base configuration is required in every published application");

        var gate = project.Descendants("Target").SingleOrDefault(target =>
            string.Equals(target.Attribute("Name")?.Value, "ValidatePublishedSettingsHygiene", StringComparison.Ordinal));
        gate.Should().NotBeNull("every dotnet publish entry point must validate its final output");
        gate!.Attribute("AfterTargets")?.Value.Should().Be("Publish");

        var failureConditions = gate.Descendants("Error")
            .Select(error => $"{error.Attribute("Condition")?.Value} {error.Attribute("Text")?.Value}")
            .ToArray();
        failureConditions.Should().Contain(text => text.Contains("appsettings.Development.json", StringComparison.Ordinal));
        failureConditions.Should().Contain(text => text.Contains("appsettings.runtime.json", StringComparison.Ordinal));
        failureConditions.Should().Contain(text => text.Contains("appsettings.json", StringComparison.Ordinal));
    }

    [Fact]
    public void TrackedAppSettings_DoNotCarryMachineSpecificPaths()
    {
        var apiDirectory = Path.Combine(FindRepoRoot(), "src", "NodePilot.Api");
        var tracked = Directory.GetFiles(apiDirectory, "appsettings*.json")
            // The runtime-overrides file is gitignored and is the documented home for local
            // absolute paths — scanning it would fail on exactly the intended usage.
            .Where(f => !Path.GetFileName(f).Equals("appsettings.runtime.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        tracked.Should().NotBeEmpty("the API ships appsettings.json and appsettings.Development.json");

        var offenders = new List<string>();
        foreach (var file in tracked)
        {
            foreach (var (line, index) in File.ReadAllLines(file).Select((l, i) => (l, i)))
            {
                // Comments carry example paths on purpose (the Development file documents how to
                // set SourceCodeRootPath yourself); only real settings are in scope.
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (AbsoluteWindowsPath.IsMatch(line))
                    offenders.Add($"{Path.GetFileName(file)}:{index + 1}: {line.Trim()}");
            }
        }

        offenders.Should().BeEmpty(
            "tracked appsettings must not contain machine-specific absolute paths — put them in " +
            "the gitignored appsettings.runtime.json or an environment variable instead");
    }

    [Fact]
    public void LaunchSettings_BindsThePortTheViteProxyTargets()
    {
        var repoRoot = FindRepoRoot();

        var launchSettings = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repoRoot, "src", "NodePilot.Api", "Properties", "launchSettings.json")));
        var applicationUrl = launchSettings.RootElement
            .GetProperty("profiles").GetProperty("http").GetProperty("applicationUrl").GetString();

        var launchPort = LocalhostPort.Match(applicationUrl ?? string.Empty);
        launchPort.Success.Should().BeTrue($"launchSettings applicationUrl '{applicationUrl}' should be an http://localhost:<port> URL");

        var viteConfig = File.ReadAllText(Path.Combine(repoRoot, "src", "nodepilot-ui", "vite.config.ts"));

        // /docs is the one proxy that does not target the backend: the documentation site is a
        // second dev server. It is checked against that project's own port below, and pinned from
        // the frontend side by docsSiteRouting.test.ts.
        var viteLines = viteConfig.Split('\n');
        var proxyPorts = viteLines
            .Where(line => !line.Contains("'/docs'"))
            .SelectMany(line => LocalhostPort.Matches(line).Select(m => m.Groups[1].Value))
            .Distinct()
            .ToArray();

        proxyPorts.Should().NotBeEmpty("vite.config.ts proxies /api, /healthz and /hubs to the backend");
        proxyPorts.Should().AllBe(launchPort.Groups[1].Value,
            "a bare `dotnet run` must land on the port the Vite dev server proxies to, otherwise " +
            "every API call from the frontend fails with no visible cause");

        var docsProxyLine = viteLines.SingleOrDefault(line => line.Contains("'/docs'"));
        docsProxyLine.Should().NotBeNull(
            "without the /docs proxy the header's help button lands on the SPA's not-found page in dev");

        var docsProxyPort = LocalhostPort.Match(docsProxyLine!);
        docsProxyPort.Success.Should().BeTrue("the /docs proxy must name a localhost target");
        docsProxyPort.Groups[1].Value.Should().NotBe(launchPort.Groups[1].Value,
            "the documentation dev server is a separate app; pointing /docs at the backend would " +
            "serve the API's own wwwroot, which is empty in a source checkout");

        var docsViteConfig = File.ReadAllText(
            Path.Combine(repoRoot, "src", "nodepilot-docs-ui", "vite.config.ts"));
        var docsServerPort = Regex.Match(docsViteConfig, @"port:\s*(\d+)");
        docsServerPort.Success.Should().BeTrue("nodepilot-docs-ui must pin its dev server port");
        docsProxyPort.Groups[1].Value.Should().Be(docsServerPort.Groups[1].Value,
            "the proxy target and the documentation dev server have to agree on the port");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NodePilot.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new InvalidOperationException($"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
        return directory.FullName;
    }
}
