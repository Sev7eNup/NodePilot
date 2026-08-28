using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Api.Configuration;
using Xunit;

namespace NodePilot.Api.Tests.Configuration;

/// <summary>
/// Verifies that <see cref="EffectiveSourceDetector"/> classifies the major provider
/// types correctly. The classification drives the Settings UI's read-only badging for
/// env/cli-overridden fields, so a misclassification would either let an operator
/// "save" a value that won't take effect or block a save that would.
/// </summary>
public sealed class EffectiveSourceDetectorTests : IDisposable
{
    private readonly string _tempDir;

    public EffectiveSourceDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "np-eff-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteJson(string filename, string body)
    {
        var path = Path.Combine(_tempDir, filename);
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void Default_NoProviderHasKey_ReturnsDefault()
    {
        var root = (IConfigurationRoot)new ConfigurationBuilder().Build();
        EffectiveSourceDetector.Detect(root, "Missing:Key").Should().Be(EffectiveSourceDetector.SourceDefault);
    }

    [Fact]
    public void Json_BaseAppsettings_ClassifiedAsAppsettings()
    {
        var path = WriteJson("appsettings.json", "{\"X\":{\"V\":\"a\"}}");
        var root = (IConfigurationRoot)new ConfigurationBuilder().AddJsonFile(path).Build();
        EffectiveSourceDetector.Detect(root, "X:V").Should().Be(EffectiveSourceDetector.SourceAppsettings);
    }

    [Fact]
    public void Json_EnvSpecificAppsettings_ClassifiedAsProduction()
    {
        var path = WriteJson("appsettings.Production.json", "{\"X\":{\"V\":\"p\"}}");
        var root = (IConfigurationRoot)new ConfigurationBuilder().AddJsonFile(path).Build();
        EffectiveSourceDetector.Detect(root, "X:V").Should().Be(EffectiveSourceDetector.SourceProduction);
    }

    [Fact]
    public void Env_ClassifiedAsEnv()
    {
        var envKey = "NP_TEST_EFF_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envKey, "v");
        try
        {
            var root = (IConfigurationRoot)new ConfigurationBuilder().AddEnvironmentVariables().Build();
            EffectiveSourceDetector.Detect(root, envKey).Should().Be(EffectiveSourceDetector.SourceEnv);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, null);
        }
    }

    [Fact]
    public void Env_ArrayChild_ClassifiesParentCollectionAsEnv()
    {
        var prefix = "NP_TEST_SCOPE_" + Guid.NewGuid().ToString("N") + "_";
        var childKey = prefix + "ExternalTrigger__AllowedWorkflowIds__0";
        Environment.SetEnvironmentVariable(childKey, Guid.NewGuid().ToString());
        try
        {
            var root = (IConfigurationRoot)new ConfigurationBuilder()
                .AddEnvironmentVariables(prefix)
                .Build();

            EffectiveSourceDetector.Detect(root, "ExternalTrigger:AllowedWorkflowIds")
                .Should().Be(EffectiveSourceDetector.SourceEnv,
                    "array-valued env overrides define child indices rather than the parent key");
        }
        finally
        {
            Environment.SetEnvironmentVariable(childKey, null);
        }
    }

    [Fact]
    public void Cli_ClassifiedAsCli()
    {
        var root = (IConfigurationRoot)new ConfigurationBuilder()
            .AddCommandLine(new[] { "--X:V=cli" })
            .Build();
        EffectiveSourceDetector.Detect(root, "X:V").Should().Be(EffectiveSourceDetector.SourceCli);
    }

    [Fact]
    public void LastProviderWins_OverlayingSourcesRespectChainOrder()
    {
        var basePath = WriteJson("appsettings.json", "{\"X\":\"a\"}");
        var prodPath = WriteJson("appsettings.Production.json", "{\"X\":\"b\"}");
        var root = (IConfigurationRoot)new ConfigurationBuilder()
            .AddJsonFile(basePath)
            .AddJsonFile(prodPath)
            .Build();
        // Both files define the key; the later one (production) wins the lookup, so the
        // detector must report production, not appsettings — otherwise the UI would let the
        // operator save without noticing production.json still overrides the value.
        EffectiveSourceDetector.Detect(root, "X").Should().Be(EffectiveSourceDetector.SourceProduction);
    }

    [Fact]
    public void DetectMany_BulkLookup_PreservesOrderingPerKey()
    {
        var envKey1 = "NP_TEST_BULK_A_" + Guid.NewGuid().ToString("N");
        var envKey2 = "NP_TEST_BULK_B_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envKey1, "v");
        try
        {
            var path = WriteJson("appsettings.json", "{\"X\":\"a\"}");
            var root = (IConfigurationRoot)new ConfigurationBuilder()
                .AddJsonFile(path)
                .AddEnvironmentVariables()
                .Build();
            var map = EffectiveSourceDetector.DetectMany(root, new[] { "X", envKey1, envKey2 });
            map["X"].Should().Be(EffectiveSourceDetector.SourceAppsettings);
            map[envKey1].Should().Be(EffectiveSourceDetector.SourceEnv);
            map[envKey2].Should().Be(EffectiveSourceDetector.SourceDefault);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey1, null);
        }
    }

    // ---- DetectNonRuntimeSource -------------------------------------------------

    [Fact]
    public void DetectNonRuntimeSource_OnlyInRuntimeFile_ReturnsNull()
    {
        // The Settings UI fully owns this key, so removing it there deletes it entirely.
        var runtime = WriteJson("appsettings.runtime.json", "{\"Llm\":{\"Profiles\":{\"a\":{\"BaseUrl\":\"http://a/v1\"}}}}");
        var root = (IConfigurationRoot)new ConfigurationBuilder().AddJsonFile(runtime).Build();

        EffectiveSourceDetector.DetectNonRuntimeSource(root, new[] { "Llm:Profiles:a:BaseUrl" })
            .Should().BeNull();
    }

    [Fact]
    public void DetectNonRuntimeSource_ShadowedByTheRuntimeFile_StillReportsTheBaseSource()
    {
        // Detect() reports "runtime" here because the runtime file sits on top, but the
        // appsettings.json entry would resurface once the runtime entry is removed. The
        // profile must not be reported as deletable.
        var basePath = WriteJson("appsettings.json", "{\"Llm\":{\"Profiles\":{\"a\":{\"BaseUrl\":\"http://base/v1\"}}}}");
        var runtime = WriteJson("appsettings.runtime.json", "{\"Llm\":{\"Profiles\":{\"a\":{\"BaseUrl\":\"http://override/v1\"}}}}");
        var root = (IConfigurationRoot)new ConfigurationBuilder()
            .AddJsonFile(basePath).AddJsonFile(runtime).Build();

        EffectiveSourceDetector.Detect(root, "Llm:Profiles:a:BaseUrl")
            .Should().Be(EffectiveSourceDetector.SourceRuntime);
        EffectiveSourceDetector.DetectNonRuntimeSource(root, new[] { "Llm:Profiles:a:BaseUrl" })
            .Should().Be(EffectiveSourceDetector.SourceAppsettings);
    }

    [Fact]
    public void DetectNonRuntimeSource_AnyOfTheKeysCounts()
    {
        // A partially-defined profile (only Model in the base config) is enough to pin it.
        var basePath = WriteJson("appsettings.json", "{\"Llm\":{\"Profiles\":{\"a\":{\"Model\":\"m\"}}}}");
        var root = (IConfigurationRoot)new ConfigurationBuilder().AddJsonFile(basePath).Build();

        EffectiveSourceDetector.DetectNonRuntimeSource(root,
            new[] { "Llm:Profiles:a:BaseUrl", "Llm:Profiles:a:Model" })
            .Should().Be(EffectiveSourceDetector.SourceAppsettings);
    }

    [Fact]
    public void DetectNonRuntimeSource_NoProviderHasAnyKey_ReturnsNull()
    {
        var root = (IConfigurationRoot)new ConfigurationBuilder().Build();

        EffectiveSourceDetector.DetectNonRuntimeSource(root, new[] { "Llm:Profiles:ghost:BaseUrl" })
            .Should().BeNull();
    }
}
