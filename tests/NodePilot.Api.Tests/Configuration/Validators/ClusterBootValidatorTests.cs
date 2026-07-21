using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Api.Configuration;
using NodePilot.Api.Configuration.Validators;
using Xunit;

namespace NodePilot.Api.Tests.Configuration.Validators;

/// <summary>
/// Migrated from the deleted ClusterConfigValidatorTests — same rules, expressed
/// through the IBootValidator interface so the Save-side path (Settings API) can
/// reuse the validator against a simulated post-save configuration.
/// </summary>
public class ClusterBootValidatorTests
{
    private static IConfiguration Build(IDictionary<string, string?> kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv).Build();

    private static List<BootValidationIssue> Run(IConfiguration cfg)
    {
        var issues = new List<BootValidationIssue>();
        new ClusterBootValidator().Validate(cfg, issues);
        return issues;
    }

    [Fact]
    public void ClusterDisabled_EmitsNothing_EvenWithMissingJwtKeys()
    {
        // Single-node mode tolerates missing Jwt:* — the key auto-generates and the
        // issuer/audience fall through to "NodePilot" defaults.
        var issues = Run(Build(new Dictionary<string, string?> { ["Cluster:Enabled"] = "false" }));
        issues.Should().BeEmpty();
    }

    [Fact]
    public void ClusterEnabled_AllJwtKeysSet_EmitsNothing()
    {
        var issues = Run(Build(new Dictionary<string, string?>
        {
            ["Cluster:Enabled"] = "true",
            ["Cluster:NodeId"] = "node-a",
            ["Jwt:Key"]      = "Z" + new string('A', 31),
            ["Jwt:Issuer"]   = "NodePilot-Prod",
            ["Jwt:Audience"] = "NodePilot-Prod",
        }));
        issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Jwt:Key")]
    [InlineData("Jwt:Issuer")]
    [InlineData("Jwt:Audience")]
    public void ClusterEnabled_MissingKey_EmitsErrorForThatKey(string missingKey)
    {
        var kv = new Dictionary<string, string?>
        {
            ["Cluster:Enabled"] = "true",
            ["Cluster:NodeId"] = "node-a",
            ["Jwt:Key"]      = "Z" + new string('A', 31),
            ["Jwt:Issuer"]   = "NodePilot-Prod",
            ["Jwt:Audience"] = "NodePilot-Prod",
        };
        kv[missingKey] = null;

        var issues = Run(Build(kv));
        issues.Should().ContainSingle(i => i.ConfigKey == missingKey && i.Severity == BootValidationSeverity.Error);
    }

    [Fact]
    public void ClusterEnabled_AllKeysMissing_EmitsAllErrors()
    {
        var issues = Run(Build(new Dictionary<string, string?>
        {
            ["Cluster:Enabled"] = "true",
            ["Cluster:NodeId"] = "node-a",
        }));
        issues.Select(i => i.ConfigKey).Should().BeEquivalentTo(new[] { "Jwt:Key", "Jwt:Issuer", "Jwt:Audience" });
    }

    [Theory]
    [InlineData("a1b2c3d4e5f6")]                        // 12-char hex (Docker default)
    [InlineData("01234567890abcdef")]                   // 17-char hex
    [InlineData("12345678-1234-1234-1234-1234567890ab")] // UUID-shape
    public void LooksLikeContainerHash_RejectsDockerStyleHosts(string hostname)
    {
        ClusterBootValidator.LooksLikeContainerHash(hostname).Should().BeTrue();
    }

    [Theory]
    [InlineData("nodepilot-a")]
    [InlineData("PROD-WEB-01")]
    [InlineData("server.firma.local")]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("xyz")]
    public void LooksLikeContainerHash_AcceptsNormalHostnames(string hostname)
    {
        ClusterBootValidator.LooksLikeContainerHash(hostname).Should().BeFalse();
    }
}
