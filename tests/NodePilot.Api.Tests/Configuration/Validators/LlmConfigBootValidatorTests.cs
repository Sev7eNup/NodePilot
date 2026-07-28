using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Api.Configuration;
using NodePilot.Api.Configuration.Validators;
using Xunit;

namespace NodePilot.Api.Tests.Configuration.Validators;

/// <summary>
/// Mirrors the boot-time SSRF check in AddNodePilotAi. Whatever AddNodePilotAi would
/// hard-fail with, the Settings-API Save must reject with the same key — otherwise the
/// UI silently persists a config that wedges the next restart.
/// </summary>
public class LlmConfigBootValidatorTests
{
    private static List<BootValidationIssue> Run(Dictionary<string, string?> kv)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(kv).Build();
        var issues = new List<BootValidationIssue>();
        new LlmConfigBootValidator().Validate(cfg, issues);
        return issues;
    }

    [Fact]
    public void Disabled_NoIssues_EvenWithMetadataUrl()
    {
        // Default-off Llm:Enabled must not trip the validator — operators with an
        // unused AI block but an experimental BaseUrl pointer should be able to boot.
        var issues = Run(new()
        {
            ["Llm:Enabled"] = "false",
            ["Llm:Profiles:a:BaseUrl"] = "http://169.254.169.254/",
        });
        issues.Should().BeEmpty();
    }

    [Fact]
    public void Enabled_NormalUrl_NoIssues()
    {
        Run(new()
        {
            ["Llm:Enabled"] = "true",
            ["Llm:ActiveProfileId"] = "a",
            ["Llm:Profiles:a:BaseUrl"] = "http://127.0.0.1:1234/v1",
        }).Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://169.254.169.254/")]
    [InlineData("https://metadata.google.internal/v1")]
    [InlineData("http://metadata.azure.com/")]
    public void Enabled_MetadataEndpoint_EmitsError(string baseUrl)
    {
        var issues = Run(new()
        {
            ["Llm:Enabled"] = "true",
            ["Llm:ActiveProfileId"] = "a",
            ["Llm:Profiles:a:BaseUrl"] = baseUrl,
        });
        issues.Should().ContainSingle(i =>
            i.ConfigKey == "Llm:Profiles:a:BaseUrl" && i.Severity == BootValidationSeverity.Error);
    }

    [Fact]
    public void Enabled_MetadataEndpointInInactiveProfile_EmitsError()
    {
        // Switching the active profile is a settings save with no restart, so an inactive profile
        // pointing at a metadata endpoint must be rejected too — otherwise it only detonates later.
        var issues = Run(new()
        {
            ["Llm:Enabled"] = "true",
            ["Llm:ActiveProfileId"] = "good",
            ["Llm:Profiles:good:BaseUrl"] = "http://127.0.0.1:1234/v1",
            ["Llm:Profiles:parked:BaseUrl"] = "http://169.254.169.254/",
        });
        issues.Should().ContainSingle(i =>
            i.ConfigKey == "Llm:Profiles:parked:BaseUrl" && i.Severity == BootValidationSeverity.Error);
    }

    [Fact]
    public void Enabled_WithoutActiveProfile_EmitsWarningNotError()
    {
        // Deliberately not an Error: the AI features are opt-in and a half-finished profile setup
        // must not keep the service from booting. The endpoints answer 503 instead.
        var issues = Run(new() { ["Llm:Enabled"] = "true" });

        issues.Should().ContainSingle(i =>
            i.ConfigKey == "Llm:ActiveProfileId" && i.Severity == BootValidationSeverity.Warning);
        issues.Should().NotContain(i => i.Severity == BootValidationSeverity.Error);
    }

    [Fact]
    public void Enabled_ActiveProfileIdPointingNowhere_EmitsWarning()
    {
        var issues = Run(new()
        {
            ["Llm:Enabled"] = "true",
            ["Llm:ActiveProfileId"] = "gone",
            ["Llm:Profiles:a:BaseUrl"] = "http://127.0.0.1:1234/v1",
        });

        issues.Should().ContainSingle(i =>
            i.ConfigKey == "Llm:ActiveProfileId" && i.Severity == BootValidationSeverity.Warning);
    }
}
