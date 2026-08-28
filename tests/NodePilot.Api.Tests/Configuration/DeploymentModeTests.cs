using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Api.Configuration;
using NodePilot.Api.Configuration.Validators;
using Xunit;

namespace NodePilot.Api.Tests.Configuration;

public sealed class DeploymentModeTests
{
    [Theory]
    [InlineData("Desktop", true)]
    [InlineData("desktop", true)]
    [InlineData("  Desktop  ", true)]
    [InlineData("Server", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("nonsense", false)] // fail-safe: unknown values fall back to Server posture, never throw
    public void IsDesktop_ResolvesValue(string? value, bool expected)
        => DeploymentModeReader.IsDesktop(Build(value)).Should().Be(expected);

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Server", true)]
    [InlineData("Desktop", true)]
    [InlineData("desktop", true)]
    [InlineData("Deskptop", false)]
    [InlineData("prod", false)]
    public void IsRecognized_AllowsOnlyServerDesktopOrEmpty(string? value, bool expected)
        => DeploymentModeReader.IsRecognized(Build(value)).Should().Be(expected);

    [Fact]
    public void Validator_UnknownValue_Errors()
    {
        var issues = new List<BootValidationIssue>();
        new DeploymentModeBootValidator().Validate(Build("Deskptop"), issues);
        issues.Should().ContainSingle(i =>
            i.Severity == BootValidationSeverity.Error && i.ConfigKey == DeploymentModeReader.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Server")]
    [InlineData("Desktop")]
    public void Validator_ValidValue_NoIssues(string? value)
    {
        var issues = new List<BootValidationIssue>();
        new DeploymentModeBootValidator().Validate(Build(value), issues);
        issues.Should().BeEmpty();
    }

    private static IConfiguration Build(string? mode)
    {
        var values = new Dictionary<string, string?>();
        if (mode is not null) values[DeploymentModeReader.Key] = mode;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
