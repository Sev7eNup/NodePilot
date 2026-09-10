using FluentAssertions;
using NodePilot.Cli.Settings;
using Xunit;
using NodePilot.Core.Clients;

namespace NodePilot.Cli.Tests.Settings;

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
    public void ResolveTlsThumbprint_PrefersFlagOverEnvOverProfile()
    {
        var store = new ConfigStore(_dir);
        var cfg = new CliConfig();
        cfg.Profiles["default"] = new ProfileEntry
        {
            Server = "https://np.example",
            TlsThumbprint = new string('C', 64),
        };

        try
        {
            Environment.SetEnvironmentVariable(
                ConfigStore.TlsThumbprintEnvironmentVariable, new string('B', 64));

            store.ResolveTlsThumbprint(new string('a', 64), "default", "https://np.example", cfg)
                .Value.Should().Be(new string('A', 64));
            store.ResolveTlsThumbprint(null, "default", "https://np.example", cfg)
                .Value.Should().Be(new string('B', 64));

            Environment.SetEnvironmentVariable(ConfigStore.TlsThumbprintEnvironmentVariable, null);
            store.ResolveTlsThumbprint(null, "default", "https://np.example", cfg)
                .Value.Should().Be(new string('C', 64));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigStore.TlsThumbprintEnvironmentVariable, null);
        }
    }

    [Fact]
    public void ResolveTlsThumbprint_ProfilePinFromAnotherOrigin_IsIgnoredWithReason()
    {
        // A pin overrides hostname validation, so it must not authenticate a different server.
        var store = new ConfigStore(_dir);
        var cfg = new CliConfig();
        cfg.Profiles["default"] = new ProfileEntry
        {
            Server = "https://np.example",
            TlsThumbprint = new string('C', 64),
        };

        var resolved = store.ResolveTlsThumbprint(null, "default", "https://other.example", cfg);

        resolved.Value.Should().BeNull();
        resolved.IgnoredReason.Should().Contain("https://np.example");
    }

    [Fact]
    public void ResolveTlsThumbprint_Sha1Thumbprint_ThrowsWithSha256Guidance()
    {
        var store = new ConfigStore(_dir);

        var act = () => store.ResolveTlsThumbprint(new string('A', 40), "default", "https://np.example", new CliConfig());

        act.Should().Throw<InvalidOperationException>().WithMessage("*SHA-256*");
    }

    [Fact]
    public void ResolveTlsThumbprint_UnusableProfilePin_ThrowsInsteadOfConnectingUnpinned()
    {
        var store = new ConfigStore(_dir);
        var cfg = new CliConfig();
        cfg.Profiles["default"] = new ProfileEntry { Server = "https://np.example", TlsThumbprint = "nonsense" };

        var act = () => store.ResolveTlsThumbprint(null, "default", "https://np.example", cfg);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ResolveSkipTlsVerification_FlagOrEnvironment_EnablesBypass()
    {
        try
        {
            ConfigStore.ResolveSkipTlsVerification(cliFlag: true).Should().BeTrue();
            ConfigStore.ResolveSkipTlsVerification(cliFlag: false).Should().BeFalse();

            Environment.SetEnvironmentVariable(ConfigStore.SkipTlsVerificationEnvironmentVariable, "1");
            ConfigStore.ResolveSkipTlsVerification(cliFlag: false).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigStore.SkipTlsVerificationEnvironmentVariable, null);
        }
    }

    [Fact]
    public void SaveAndLoad_RoundtripsTlsThumbprint()
    {
        var store = new ConfigStore(_dir);
        var cfg = new CliConfig();
        cfg.Profiles["default"] = new ProfileEntry
        {
            Server = "https://np.example",
            TlsThumbprint = new string('A', 64),
        };
        store.Save(cfg);

        new ConfigStore(_dir).Load().Profiles["default"].TlsThumbprint.Should().Be(new string('A', 64));
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
