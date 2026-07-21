using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Security;
using Xunit;

namespace NodePilot.Engine.Tests.Security;

public class OutputRedactorTests
{
    private static OutputRedactor Default() => new();

    private static OutputRedactor WithTestingEnvDisabled()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:Redaction:Enabled"] = "false" })
            .Build();
        // Set Testing env so the flag is honoured
        var prev = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");
        try { return new OutputRedactor(cfg); }
        finally { Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", prev); }
    }

    [Fact]
    public void Redact_NullInput_ReturnsNull()
    {
        Default().Redact((string?)null).Should().BeNull();
    }

    [Fact]
    public void Redact_EmptyString_ReturnsEmpty()
    {
        Default().Redact("").Should().BeEmpty();
    }

    [Fact]
    public void Redact_KeyEqualsValue_RedactsValue()
    {
        var result = Default().Redact("password=supersecret");
        result.Should().Contain("password=");
        result.Should().Contain("***");
        result.Should().NotContain("supersecret");
    }

    [Fact]
    public void Redact_KeyColonValue_RedactsValue()
    {
        var result = Default().Redact("token: mytoken123");
        result.Should().Contain("***");
        result.Should().NotContain("mytoken123");
    }

    [Fact]
    public void Redact_JsonPasswordField_RedactsValue()
    {
        var result = Default().Redact("{\"password\": \"abc123\"}");
        result.Should().Contain("***");
        result.Should().NotContain("abc123");
    }

    [Fact]
    public void Redact_ApiKey_RedactsValue()
    {
        var result = Default().Redact("api_key=AAAAABBBBBCCCCC");
        result.Should().Contain("***");
        result.Should().NotContain("AAAAABBBBBCCCCC");
    }

    [Fact]
    public void Redact_BearerToken_RedactsValue()
    {
        var result = Default().Redact("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9");
        result.Should().Contain("***");
        result.Should().NotContain("eyJhbGciOiJIUzI1NiJ9");
    }

    [Fact]
    public void Redact_ConnectionStringPassword_RedactsValue()
    {
        var result = Default().Redact("Server=db;Password=secret;Database=test");
        result.Should().Contain("***");
        result.Should().NotContain("=secret");
    }

    [Fact]
    public void Redact_CleanInput_ReturnsUnchanged()
    {
        var input = "Workflow completed. Steps=5. Duration=1200ms.";
        Default().Redact(input).Should().Be(input);
    }

    [Fact]
    public void Redact_DoubleQuotedPassword_RedactsValue()
    {
        var result = Default().Redact("password = \"my secret value\"");
        result.Should().Contain("***");
        result.Should().NotContain("my secret value");
    }

    [Fact]
    public void Redact_SingleQuotedPassword_RedactsValue()
    {
        var result = Default().Redact("password = 'my secret value'");
        result.Should().Contain("***");
        result.Should().NotContain("my secret value");
    }

    [Fact]
    public void Redact_ActivityResult_RedactsBothOutputs()
    {
        var result = new ActivityResult
        {
            Success = true,
            Output = "token=abc123",
            ErrorOutput = "password=err_secret",
            OutputParameters = new Dictionary<string, string> { ["msg"] = "token=paramvalue" }
        };
        var redacted = Default().Redact(result);
        redacted.Output.Should().NotContain("abc123");
        redacted.ErrorOutput.Should().NotContain("err_secret");
        redacted.OutputParameters["msg"].Should().NotContain("paramvalue");
    }

    [Fact]
    public void Redact_ActivityResult_PreservesSuccessAndDuration()
    {
        var dur = TimeSpan.FromSeconds(3);
        var result = new ActivityResult { Success = true, Duration = dur, Output = "ok" };
        var redacted = Default().Redact(result);
        redacted.Success.Should().BeTrue();
        redacted.Duration.Should().Be(dur);
    }

    [Theory]
    [InlineData("dbPassword")]
    [InlineData("step.param.clientSecret")]
    [InlineData("manual.apiKey")]
    [InlineData("webhookHeader_X-NodePilot-Signature")]
    [InlineData("connectionString")]
    [InlineData("refreshToken")]
    public void RedactNamedValue_SensitiveQualifiedOrCamelCaseName_RedactsOpaqueValue(string name)
    {
        Default().RedactNamedValue(name, "opaque-value-with-no-regex-trigger")
            .Should().Be(OutputRedactor.Placeholder);
    }

    [Theory]
    [InlineData("promptTokens")]
    [InlineData("completionTokens")]
    [InlineData("totalTokens")]
    [InlineData("monkey")]
    [InlineData("publicKeyAlgorithm")]
    [InlineData("jwtIssuer")]
    public void RedactNamedValue_NonSecretMetadataName_PreservesCleanValue(string name)
    {
        Default().RedactNamedValue(name, "42").Should().Be("42");
    }

    [Fact]
    public void Redact_ActivityResult_UsesParameterNameButPreservesRawDownstreamResult()
    {
        var raw = new ActivityResult
        {
            Success = true,
            OutputParameters = new Dictionary<string, string>
            {
                ["dbPassword"] = "hunter2",
                ["promptTokens"] = "42",
            }
        };

        var sanitized = Default().Redact(raw);

        sanitized.OutputParameters["dbPassword"].Should().Be(OutputRedactor.Placeholder);
        sanitized.OutputParameters["promptTokens"].Should().Be("42");
        raw.OutputParameters["dbPassword"].Should().Be("hunter2",
            "the engine must retain the raw result for explicit downstream workflow data flow");
    }

    [Fact]
    public void Redact_WhenDisabledInTestingEnv_LeavesValueUnchanged()
    {
        var redactor = WithTestingEnvDisabled();
        var sensitive = "password=toplevel_secret";
        redactor.Redact(sensitive).Should().Be(sensitive);
        redactor.RedactNamedValue("dbPassword", "opaque-secret").Should().Be("opaque-secret");
    }

    [Fact]
    public void Redact_CustomPattern_Applied()
    {
        var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", null);
        try
        {
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:Redaction:Patterns:0"] = @"(CUSTOM_SENSITIVE_\w+\s*=\s*)(\w+)"
                })
                .Build();
            var redactor = new OutputRedactor(cfg);
            var result = redactor.Redact("CUSTOM_SENSITIVE_KEY=myvalue");
            result.Should().Contain("***");
            result.Should().NotContain("myvalue");
        }
        finally { Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", env); }
    }

    [Fact]
    public void Placeholder_IsThreeStars()
    {
        OutputRedactor.Placeholder.Should().Be("***");
    }
}
