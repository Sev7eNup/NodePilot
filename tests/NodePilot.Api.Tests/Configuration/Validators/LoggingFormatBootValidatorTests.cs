using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Api.Configuration;
using NodePilot.Api.Configuration.Validators;
using Xunit;

namespace NodePilot.Api.Tests.Configuration.Validators;

public class LoggingFormatBootValidatorTests
{
    private static List<BootValidationIssue> Run(string? format)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:Format"] = format })
            .Build();
        var issues = new List<BootValidationIssue>();
        new LoggingFormatBootValidator().Validate(cfg, issues);
        return issues;
    }

    [Theory]
    [InlineData("text")]
    [InlineData("cmtrace")]
    [InlineData("json")]
    [InlineData("ecs-json")]
    [InlineData("CMTRACE")]    // case-insensitive
    [InlineData(" json ")]      // whitespace tolerant
    public void KnownFormats_NoIssues(string format)
    {
        Run(format).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrMissing_NoIssues(string? format)
    {
        // Empty config maps to LogFormatters' null-return path (plain text default) — fine.
        Run(format).Should().BeEmpty();
    }

    [Theory]
    [InlineData("ecs-jsom")]    // typo
    [InlineData("ECS-Json2")]   // version drift attempt
    [InlineData("syslog")]      // hopeful future format
    public void UnknownFormat_EmitsError(string format)
    {
        var issues = Run(format);
        issues.Should().ContainSingle(i =>
            i.ConfigKey == "Logging:Format" &&
            i.Severity == BootValidationSeverity.Error &&
            i.Message.Contains(format));
    }
}
