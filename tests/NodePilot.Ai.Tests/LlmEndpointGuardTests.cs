using FluentAssertions;
using Xunit;

namespace NodePilot.Ai.Tests;

public sealed class LlmEndpointGuardTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1")]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/v1")]
    [InlineData("  https://host/v1//  ", "https://host/v1")]
    public void NormalizeAndValidateBaseUrl_ValidUrl_TrimsTrailingSlash(string input, string expected)
    {
        LlmEndpointGuard.NormalizeAndValidateBaseUrl(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeAndValidateBaseUrl_Empty_Throws(string? input)
    {
        var act = () => LlmEndpointGuard.NormalizeAndValidateBaseUrl(input);
        act.Should().Throw<LlmException>();
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://host/v1")]
    [InlineData("file:///etc/passwd")]
    public void NormalizeAndValidateBaseUrl_NonHttpOrRelative_Throws(string input)
    {
        var act = () => LlmEndpointGuard.NormalizeAndValidateBaseUrl(input);
        act.Should().Throw<LlmException>().Where(e => e.Message.Contains("absolute http/https"));
    }

    [Theory]
    [InlineData("http://api.openai.com/v1")]
    [InlineData("http://10.20.30.40:11434/v1")]
    [InlineData("http://localhost.example.com:11434/v1")]
    public void NormalizeAndValidateBaseUrl_RemotePlaintext_Throws(string input)
    {
        var act = () => LlmEndpointGuard.NormalizeAndValidateBaseUrl(input);

        act.Should().Throw<LlmException>()
            .Where(e => e.Message.Contains("plaintext HTTP") && e.Message.Contains("HTTPS"));
    }

    [Theory]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://127.42.7.9:11434/v1")]
    [InlineData("http://[::1]:11434/v1")]
    public void NormalizeAndValidateBaseUrl_LiteralLoopbackPlaintext_IsAllowed(string input)
    {
        LlmEndpointGuard.NormalizeAndValidateBaseUrl(input).Should().Be(input);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1")]
    [InlineData("https://metadata.azure.com/metadata/instance")]
    public void NormalizeAndValidateBaseUrl_CloudMetadata_Throws(string input)
    {
        var act = () => LlmEndpointGuard.NormalizeAndValidateBaseUrl(input);
        act.Should().Throw<LlmException>().Where(e => e.Message.Contains("cloud-metadata"));
    }

    // ---- Dialect resolution ---------------------------------------------------------
    // The BaseUrl path is the only signal for which wire dialect an endpoint speaks, and it also
    // decides whether the endpoint suffix still has to be appended. Pasting the full endpoint URL
    // used to produce a double-append (POST /v1/responses/chat/completions → HTTP 404).

    [Fact]
    public void ResolveEndpoint_ResponsesPath_PostsVerbatimAndStripsSuffixForRoot()
    {
        var target = LlmEndpointGuard.ResolveEndpoint("https://api.openai.com/v1/responses");

        target.Flavor.Should().Be(LlmApiFlavor.Responses);
        target.PostUrl.Should().Be("https://api.openai.com/v1/responses");
        target.ApiRoot.Should().Be("https://api.openai.com/v1");
    }

    [Fact]
    public void ResolveEndpoint_ChatCompletionsPath_DoesNotAppendTheSuffixTwice()
    {
        var target = LlmEndpointGuard.ResolveEndpoint("https://api.openai.com/v1/chat/completions");

        target.Flavor.Should().Be(LlmApiFlavor.ChatCompletions);
        target.PostUrl.Should().Be("https://api.openai.com/v1/chat/completions");
        target.ApiRoot.Should().Be("https://api.openai.com/v1");
    }

    [Fact]
    public void ResolveEndpoint_PlainRoot_AppendsChatCompletions()
    {
        var target = LlmEndpointGuard.ResolveEndpoint("https://api.openai.com/v1");

        target.Flavor.Should().Be(LlmApiFlavor.ChatCompletions);
        target.PostUrl.Should().Be("https://api.openai.com/v1/chat/completions");
        target.ApiRoot.Should().Be("https://api.openai.com/v1");
    }

    [Fact]
    public void ResolveEndpoint_HostRootWithoutPath_AppendsChatCompletions()
    {
        var target = LlmEndpointGuard.ResolveEndpoint("http://localhost:11434");

        target.Flavor.Should().Be(LlmApiFlavor.ChatCompletions);
        target.PostUrl.Should().Be("http://localhost:11434/chat/completions");
        target.ApiRoot.Should().Be("http://localhost:11434");
    }

    [Theory]
    [InlineData("https://api.openai.com/v1/responses/")]
    [InlineData("  https://api.openai.com/v1/responses//  ")]
    public void ResolveEndpoint_TrailingSlash_IsNormalizedBeforeDetection(string input)
    {
        var target = LlmEndpointGuard.ResolveEndpoint(input);

        target.Flavor.Should().Be(LlmApiFlavor.Responses);
        target.PostUrl.Should().Be("https://api.openai.com/v1/responses");
    }

    [Theory]
    [InlineData("https://api.openai.com/v1/Responses", LlmApiFlavor.Responses)]
    [InlineData("https://api.openai.com/v1/Chat/Completions", LlmApiFlavor.ChatCompletions)]
    public void ResolveEndpoint_MixedCaseSuffix_IsDetected(string input, LlmApiFlavor expected)
    {
        var target = LlmEndpointGuard.ResolveEndpoint(input);

        target.Flavor.Should().Be(expected);
        target.PostUrl.Should().Be(input);
    }

    [Fact]
    public void ResolveEndpoint_HostNamedResponses_IsNotTreatedAsResponsesDialect()
    {
        // The suffix is a path, not a hostname — matching on the whole string only works because
        // the constants carry a leading slash.
        var target = LlmEndpointGuard.ResolveEndpoint("https://responses.example.test");

        target.Flavor.Should().Be(LlmApiFlavor.ChatCompletions);
        target.PostUrl.Should().Be("https://responses.example.test/chat/completions");
    }

    [Theory]
    [InlineData("http://169.254.169.254/v1/responses")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void ResolveEndpoint_GuardRejectedBaseUrl_Throws(string input)
    {
        // Dialect detection must never run ahead of the SSRF/format guard.
        var act = () => LlmEndpointGuard.ResolveEndpoint(input);
        act.Should().Throw<LlmException>();
    }

    [Theory]
    [InlineData("http://169.254.169.254/", true)]
    [InlineData("http://metadata.google.internal", true)]
    [InlineData("https://metadata.azure.com", true)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("http://localhost:11434/v1", false)]
    [InlineData("http://10.0.0.5:1234/v1", false)]
    public void IsCloudMetadataEndpoint_ClassifiesCorrectly(string baseUrl, bool expected)
    {
        LlmEndpointGuard.IsCloudMetadataEndpoint(baseUrl).Should().Be(expected);
    }

    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://127.0.0.1:11434", true)]
    [InlineData("http://[::1]:11434", true)]
    [InlineData("http://localhost.example.com:11434", false)]
    [InlineData("http://10.0.0.5:11434", false)]
    public void IsLiteralLoopbackEndpoint_ClassifiesWithoutDns(string input, bool expected)
    {
        LlmEndpointGuard.IsLiteralLoopbackEndpoint(new Uri(input)).Should().Be(expected);
    }
}
