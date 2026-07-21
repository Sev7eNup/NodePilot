using FluentAssertions;
using NodePilot.Cli.Settings;
using Xunit;

namespace NodePilot.Cli.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir;

    public ConfigStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "np-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Load_NoFile_ReturnsEmptyConfig()
    {
        var store = new ConfigStore(_dir);
        var cfg = store.Load();
        cfg.DefaultProfile.Should().Be("default");
        cfg.Profiles.Should().BeEmpty();
    }

    [Fact]
    public void SaveAndLoad_Roundtrips()
    {
        var store = new ConfigStore(_dir);
        var cfg = new CliConfig { DefaultProfile = "prod" };
        cfg.Profiles["prod"] = new ProfileEntry { Server = "https://np.example/" };
        store.Save(cfg);

        var loaded = new ConfigStore(_dir).Load();
        loaded.DefaultProfile.Should().Be("prod");
        loaded.Profiles.Should().ContainKey("prod");
        loaded.Profiles["prod"].Server.Should().Be("https://np.example/");
    }

    [Fact]
    public void ResolveServer_PrefersFlagOverEnvOverProfile()
    {
        var store = new ConfigStore(_dir);
        var cfg = new CliConfig();
        cfg.Profiles["default"] = new ProfileEntry { Server = "https://from-profile" };

        try
        {
            Environment.SetEnvironmentVariable("NODEPILOT_SERVER", "https://from-env");

            store.ResolveServer(cliFlag: "https://from-flag", profile: "default", config: cfg)
                .Should().Be("https://from-flag");
            store.ResolveServer(cliFlag: null, profile: "default", config: cfg)
                .Should().Be("https://from-env");

            Environment.SetEnvironmentVariable("NODEPILOT_SERVER", null);
            store.ResolveServer(cliFlag: null, profile: "default", config: cfg)
                .Should().Be("https://from-profile");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NODEPILOT_SERVER", null);
        }
    }

    [Fact]
    public void ResolveProfileName_FallsBackToDefault()
    {
        var store = new ConfigStore(_dir);
        var cfg = new CliConfig { DefaultProfile = "prod" };
        store.ResolveProfileName(null, cfg).Should().Be("prod");
        store.ResolveProfileName("dev", cfg).Should().Be("dev");
    }
}
