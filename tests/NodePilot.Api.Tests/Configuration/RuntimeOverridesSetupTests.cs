using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using NodePilot.Api.Configuration;
using NodePilot.Core.Interfaces;
using System.Text.Json.Nodes;
using Xunit;

namespace NodePilot.Api.Tests.Configuration;

/// <summary>
/// Verifies that <see cref="RuntimeOverridesSetup.FindInsertionIndex"/> places the
/// runtime-overrides JSON source right after <c>appsettings.{Env}.json</c>, falling back
/// to right after appsettings.json, or to the end, when no env-specific file exists.
/// Wrong placement is dangerous: after env vars, the UI silently loses to deployment
/// secrets; before appsettings.json, defaults silently override UI saves.
/// </summary>
public class RuntimeOverridesSetupTests
{
    private sealed class ReversibleProtector : ISecretProtector
    {
        public string ProviderName => "Test";

        public byte[] Protect(string plaintext) =>
            System.Text.Encoding.UTF8.GetBytes("protected:" + plaintext);

        public string Unprotect(byte[] ciphertext)
        {
            var value = System.Text.Encoding.UTF8.GetString(ciphertext);
            return value.StartsWith("protected:", StringComparison.Ordinal)
                ? value["protected:".Length..]
                : throw new InvalidOperationException("Unexpected ciphertext.");
        }
    }

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

    [Fact]
    public void MigrateLegacyPlaintextSecrets_OtlpHeaders_EncryptsPrimaryAndBackupFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "nodepilot-runtime-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string primaryHeader = "X-Collector-Key=primary-legacy-value";
            const string backupHeader = "X-Collector-Key=backup-legacy-value";
            var path = Path.Combine(root, RuntimeOverridesSetup.DefaultFilename);
            File.WriteAllText(path, RuntimeJson(primaryHeader));
            File.WriteAllText(path + ".bak.legacy", RuntimeJson(backupHeader));

            RuntimeOverridesSetup.MigrateLegacyPlaintextSecrets(
                path,
                new ReversibleProtector());

            foreach (var candidate in new[] { path, path + ".bak.legacy" })
            {
                var persisted = File.ReadAllText(candidate);
                persisted.Should().Contain(EncryptingJsonConfigurationProvider.EncryptedValuePrefix);
                persisted.Should().NotContain("legacy-value");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MigrateLegacyPlaintextSecrets_MatchesConfigurationKeysCaseInsensitively()
    {
        var root = Path.Combine(Path.GetTempPath(), "nodepilot-runtime-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string legacySecret = "x-collector-key=lowercase-config-secret";
            var path = Path.Combine(root, RuntimeOverridesSetup.DefaultFilename);
            File.WriteAllText(path, $$"""
            {
              "opentelemetry": {
                "otlp": {
                  "headers": "{{legacySecret}}"
                }
              }
            }
            """);

            RuntimeOverridesSetup.MigrateLegacyPlaintextSecrets(path, new ReversibleProtector());

            var persisted = File.ReadAllText(path);
            persisted.Should().Contain(EncryptingJsonConfigurationProvider.EncryptedValuePrefix);
            persisted.Should().NotContain(legacySecret);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MigrateLegacyPlaintextSecrets_ExistingCiphertext_IsByteStable()
    {
        var root = Path.Combine(Path.GetTempPath(), "nodepilot-runtime-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var protector = new ReversibleProtector();
            var ciphertext = EncryptingJsonConfigurationProvider.EncryptForPersist("already-protected", protector);
            var path = Path.Combine(root, RuntimeOverridesSetup.DefaultFilename);
            File.WriteAllText(path, RuntimeJson(ciphertext));

            RuntimeOverridesSetup.MigrateLegacyPlaintextSecrets(path, protector);

            File.ReadAllText(path).Should().Contain(ciphertext);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string RuntimeJson(string headers) => new JsonObject
    {
        ["OpenTelemetry"] = new JsonObject
        {
            ["Otlp"] = new JsonObject { ["Headers"] = headers },
        },
    }.ToJsonString();
}
