using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

/// <summary>
/// Guards two properties of the shipped developer configuration that a stranger cloning this
/// repository depends on, and that both regressed silently once already.
///
/// <para><b>No machine-specific paths in tracked config.</b> <c>appsettings.Development.json</c>
/// carried <c>"SourceCodeRootPath": "e:\\NodePilot"</c> — one developer's checkout, published to
/// everyone. It never crashed: the source-knowledge reader just reports itself unavailable when
/// the directory is missing, so the feature was quietly dead for every other clone. Machine paths
/// belong in the gitignored <c>appsettings.runtime.json</c>, which is why that file is excluded
/// here rather than scanned.</para>
///
/// <para><b>One dev port.</b> <c>launchSettings.json</c> bound 5068 while every doc and the Vite
/// dev-server proxy said 5000. A contributor who typed a bare <c>dotnet run</c> got a backend on
/// a port the frontend never talks to, and every API call failed with no hint as to why.</para>
/// </summary>
public sealed class AppSettingsHygieneTests
{
    /// <summary>Drive-letter absolute paths inside JSON strings, e.g. "e:\\NodePilot" or "C:/foo".</summary>
    private static readonly Regex AbsoluteWindowsPath = new(@"""[A-Za-z]:(\\\\|/)", RegexOptions.Compiled);

    private static readonly Regex LocalhostPort = new(@"http://localhost:(\d+)", RegexOptions.Compiled);

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
        var proxyPorts = LocalhostPort.Matches(viteConfig)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToArray();

        proxyPorts.Should().NotBeEmpty("vite.config.ts proxies /api, /healthz and /hubs to the backend");
        proxyPorts.Should().AllBe(launchPort.Groups[1].Value,
            "a bare `dotnet run` must land on the port the Vite dev server proxies to, otherwise " +
            "every API call from the frontend fails with no visible cause");
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
