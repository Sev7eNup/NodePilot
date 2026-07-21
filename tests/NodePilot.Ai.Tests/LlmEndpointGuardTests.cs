using FluentAssertions;
using NodePilot.Ai;
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
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1")]
    [InlineData("https://metadata.azure.com/metadata/instance")]
    public void NormalizeAndValidateBaseUrl_CloudMetadata_Throws(string input)
    {
        var act = () => LlmEndpointGuard.NormalizeAndValidateBaseUrl(input);
        act.Should().Throw<LlmException>().Where(e => e.Message.Contains("cloud-metadata"));
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
}
