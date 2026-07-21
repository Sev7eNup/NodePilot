using FluentAssertions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// Regex coverage for the parameter-key injection guard (internal security finding "C-1").
/// Without this validation, an attacker-controlled upstream output variable named e.g.
///   <c>x';Remove-Item C:\ -Recurse -Force;$__npOut['y</c>
/// would break out of the single-quoted PowerShell literal that ProcessExecutionEngine
/// inlines into the wrapper script. The validator is a one-liner in production but
/// security-load-bearing — pin every shape that should and shouldn't pass.
/// </summary>
public class ParameterKeyValidatorTests
{
    [Theory]
    [InlineData("name")]
    [InlineData("ServerName")]
    [InlineData("user_id")]
    [InlineData("CamelCase123")]
    [InlineData("a")]
    [InlineData("X")]
    [InlineData("snake_case_with_numbers_42")]
    [InlineData("UPPERCASE")]
    [InlineData("with_trailing_underscore_")]
    [InlineData("_leading_underscore")]
    public void IsValid_AcceptsAlphaNumericUnderscore(string key)
    {
        ParameterKeyValidator.IsValid(key).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void IsValid_RejectsNullOrEmpty(string? key)
    {
        ParameterKeyValidator.IsValid(key).Should().BeFalse();
    }

    [Theory]
    [InlineData("with space")]
    [InlineData("with-hyphen")]
    [InlineData("with.dot")]
    [InlineData("with;semicolon")]
    [InlineData("with$dollar")]
    [InlineData("with'quote")]
    [InlineData("with\"doublequote")]
    [InlineData("with`backtick")]
    [InlineData("with\nnewline")]
    [InlineData("with\ttab")]
    public void IsValid_RejectsCharactersThatBreakSingleQuotedString(string key)
    {
        ParameterKeyValidator.IsValid(key).Should().BeFalse();
    }

    [Theory]
    [InlineData("x';Remove-Item C:\\ -Recurse -Force;$__npOut['y")]
    [InlineData("$(rm -rf /)")]
    [InlineData("'; Get-Content C:\\Windows\\System32\\config\\SAM; '")]
    [InlineData("name;Invoke-Expression $cmd;done")]
    public void IsValid_RejectsKnownInjectionPayloads(string payload)
    {
        // Regression catalogue for the "C-1" injection guard: each of these payloads would have
        // broken out of the single-quoted literal and run arbitrary PowerShell before the guard existed.
        ParameterKeyValidator.IsValid(payload).Should().BeFalse();
    }

    [Theory]
    [InlineData("naïve")]      // diacritic
    [InlineData("日本語")]      // CJK
    [InlineData("café")]       // accented
    [InlineData("emoji🚀")]    // emoji (would tokenise oddly inside single-quoted literal)
    public void IsValid_RejectsNonAsciiAlphaNumeric(string key)
    {
        // Strict ASCII-only allow-list. Unicode alphabetic chars are technically safe
        // inside a single-quoted PS literal, but the corresponding PowerShell variable
        // names are not always representable in the wrapper script's variable-binding
        // syntax. Keeping the allow-list ASCII avoids surprise downstream.
        ParameterKeyValidator.IsValid(key).Should().BeFalse();
    }
}
