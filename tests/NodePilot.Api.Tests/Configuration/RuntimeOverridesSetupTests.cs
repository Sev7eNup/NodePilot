using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using NodePilot.Api.Configuration;
using Xunit;

namespace NodePilot.Api.Tests.Configuration;

/// <summary>
/// Verifies that <see cref="RuntimeOverridesSetup.FindInsertionIndex"/> places the
/// runtime-overrides JSON source at the correct position in the configuration source
/// list — directly after <c>appsettings.{Env}.json</c>, falling back to "after
/// appsettings.json" or "at the end" when the env-specific file isn't present.
///
/// Wrong placement here is silent + dangerous: if the override lands AFTER EnvVars,
/// the UI silently overrules deployment-injected secrets (broken K8s/Container UX);
/// if it lands BEFORE appsettings.json, defaults overrule UI saves (broken UI UX).
/// </summary>
public class RuntimeOverridesSetupTests
{
    private static FileConfigurationSource Json(string filename) =>
        new JsonConfigurationSource { Path = filename, Optional = true };

    [Fact]
    public void FindInsertionIndex_AfterEnvSpecificFile_WhenPresent()
    {
        var sources = new List<IConfigurationSource>
        {
            Json("appsettings.json"),
            Json("appsettings.Production.json"),
            new EnvironmentVariablesConfigurationSource(),
        };
        var idx = RuntimeOverridesSetup.FindInsertionIndex(sources, "Production");
        idx.Should().Be(2, "runtime overrides go directly after appsettings.Production.json so the UI beats Installer-Bootstrap but loses to EnvVars");
    }

    [Fact]
    public void FindInsertionIndex_AfterBaseFile_WhenNoEnvSpecificFile()
    {
        var sources = new List<IConfigurationSource>
        {
            Json("appsettings.json"),
            new EnvironmentVariablesConfigurationSource(),
        };
        var idx = RuntimeOverridesSetup.FindInsertionIndex(sources, "Production");
        idx.Should().Be(1, "fall back to inserting after the base appsettings.json when no env-specific file exists");
    }

    [Fact]
    public void FindInsertionIndex_NoJsonSources_AppendsAtEnd()
    {
        var sources = new List<IConfigurationSource>
        {
            new EnvironmentVariablesConfigurationSource(),
        };
        var idx = RuntimeOverridesSetup.FindInsertionIndex(sources, "Production");
        idx.Should().Be(sources.Count, "minimal hosts without any JSON sources still get the override appended deterministically");
    }

    [Fact]
    public void FindInsertionIndex_CaseInsensitiveOnEnvName()
    {
        var sources = new List<IConfigurationSource>
        {
            Json("appsettings.json"),
            Json("appsettings.PRODUCTION.json"),  // unusual casing
        };
        var idx = RuntimeOverridesSetup.FindInsertionIndex(sources, "Production");
        idx.Should().Be(2, "appsettings file lookup must tolerate filesystem-style casing");
    }

    [Fact]
    public void ResolveOverridesPath_AbsoluteOverride_UsedAsIs()
    {
        var explicitPath = Path.Combine(Path.GetTempPath(), "np-explicit-" + Guid.NewGuid().ToString("N") + ".json");
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RuntimeOverridesSetup.OverridesPathConfigKey] = explicitPath,
            })
            .Build();
        var resolved = RuntimeOverridesSetup.ResolveOverridesPath(cfg, "C:\\some\\content\\root");
        resolved.Should().Be(Path.GetFullPath(explicitPath));
    }

    [Fact]
    public void ResolveOverridesPath_RelativeOverride_ResolvedAgainstContentRoot()
    {
        var contentRoot = Path.GetTempPath();
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RuntimeOverridesSetup.OverridesPathConfigKey] = "subfolder/over.json",
            })
            .Build();
        var resolved = RuntimeOverridesSetup.ResolveOverridesPath(cfg, contentRoot);
        resolved.Should().Be(Path.GetFullPath(Path.Combine(contentRoot, "subfolder/over.json")));
    }

    [Fact]
    public void ResolveOverridesPath_NoConfig_DefaultsToContentRoot()
    {
        var contentRoot = Path.GetTempPath();
        var cfg = new ConfigurationBuilder().Build();
        var resolved = RuntimeOverridesSetup.ResolveOverridesPath(cfg, contentRoot);
        resolved.Should().Be(Path.GetFullPath(Path.Combine(contentRoot, RuntimeOverridesSetup.DefaultFilename)));
    }
}
