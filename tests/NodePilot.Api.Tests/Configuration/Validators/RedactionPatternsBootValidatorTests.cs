using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Api.Configuration;
using NodePilot.Api.Configuration.Validators;
using Xunit;

namespace NodePilot.Api.Tests.Configuration.Validators;

public class RedactionPatternsBootValidatorTests
{
    private static List<BootValidationIssue> Run(params string?[] patterns)
    {
        var values = patterns
            .Select((pattern, index) => new KeyValuePair<string, string?>($"Logging:Redaction:Patterns:{index}", pattern));
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var issues = new List<BootValidationIssue>();
        new RedactionPatternsBootValidator().Validate(cfg, issues);
        return issues;
    }

    [Fact]
    public void Validate_NoPatterns_NoIssues()
    {
        Run().Should().BeEmpty();
    }

    [Fact]
    public void Validate_ValidAndBlankPatterns_NoIssues()
    {
        Run(@"(?i)token=\S+", "", "   ").Should().BeEmpty();
    }

    [Fact]
    public void Validate_InvalidPattern_EmitsErrorNamingTheEntry()
    {
        var issues = Run(@"(?i)token=\S+", "(unclosed");

        issues.Should().ContainSingle(i =>
            i.ConfigKey == "Logging:Redaction:Patterns:1" &&
            i.Severity == BootValidationSeverity.Error);
    }
}
