using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Api.Configuration;
using NodePilot.Api.Configuration.Validators;
using Xunit;

namespace NodePilot.Api.Tests.Configuration.Validators;

/// <summary>
/// Mirrors the runtime checks in <c>SecretProtectorBootstrapFactory</c> so the
/// Settings API can reject a Save that would brick the next boot.
/// </summary>
public class SecretsConsistencyBootValidatorTests
{
    private static List<BootValidationIssue> Run(Dictionary<string, string?> kv)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(kv).Build();
        var issues = new List<BootValidationIssue>();
        new SecretsConsistencyBootValidator().Validate(cfg, issues);
        return issues;
    }

    [Fact]
    public void Default_NoIssues()
    {
        Run(new Dictionary<string, string?>()).Should().BeEmpty();
    }

    [Fact]
    public void UnknownProvider_EmitsError_OnProviderKey()
    {
        var issues = Run(new Dictionary<string, string?> { ["Secrets:Provider"] = "AesGCMm" });
        issues.Should().ContainSingle(i =>
            i.ConfigKey == "Secrets:Provider" &&
            i.Severity == BootValidationSeverity.Error &&
            i.Message.Contains("AesGCMm"));
    }

    [Fact]
    public void Cluster_PlusDpapi_EmitsError()
    {
        var issues = Run(new Dictionary<string, string?>
        {
            ["Cluster:Enabled"] = "true",
            ["Secrets:Provider"] = "Dpapi",
        });
        issues.Should().ContainSingle(i =>
            i.ConfigKey == "Secrets:Provider" &&
            i.Severity == BootValidationSeverity.Error &&
            i.Message.Contains("AesGcm", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cluster_PlusAesGcm_MissingKey_EmitsError()
    {
        var issues = Run(new Dictionary<string, string?>
        {
            ["Cluster:Enabled"] = "true",
            ["Secrets:Provider"] = "AesGcm",
        });
        issues.Should().ContainSingle(i =>
            i.ConfigKey == "Secrets:MasterKey" && i.Severity == BootValidationSeverity.Error);
    }

    [Fact]
    public void Cluster_PlusAesGcm_WithKey_NoIssues()
    {
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var issues = Run(new Dictionary<string, string?>
        {
            ["Cluster:Enabled"] = "true",
            ["Secrets:Provider"] = "AesGcm",
            ["Secrets:MasterKey"] = key,
        });
        issues.Should().BeEmpty();
    }

    [Fact]
    public void Cluster_PlusAesGcm_WithKeyFile_NoIssues()
    {
        var issues = Run(new Dictionary<string, string?>
        {
            ["Cluster:Enabled"] = "true",
            ["Secrets:Provider"] = "AesGcm",
            ["Secrets:MasterKeyFile"] = "C:\\ProgramData\\NodePilot\\secrets\\masterkey.txt",
        });
        issues.Should().BeEmpty();
    }

    [Fact]
    public void UnknownProvider_ShortCircuits_DoesNotChainOtherChecks()
    {
        // Once the provider name is wrong, downstream checks (cluster conflict, master key)
        // become noise — the operator only needs to fix the typo, anything else would just
        // pile on irrelevant findings. Asserting "exactly one issue" here is the contract.
        var issues = Run(new Dictionary<string, string?>
        {
            ["Cluster:Enabled"] = "true",
            ["Secrets:Provider"] = "Garbage",
        });
        issues.Should().ContainSingle();
    }
}
