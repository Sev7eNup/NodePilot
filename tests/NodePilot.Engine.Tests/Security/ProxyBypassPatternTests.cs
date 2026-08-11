using FluentAssertions;
using NodePilot.Core.Net;
using Xunit;

namespace NodePilot.Engine.Tests.Security;

/// <summary>
/// The bypass-glob translation shared by <c>RestApi:Proxy:BypassList</c> and
/// <c>Llm:Proxy:BypassList</c>. Lives here rather than in a Core test project because Core
/// has none — the consuming suites cover it.
/// </summary>
public class ProxyBypassPatternTests
{
    [Fact]
    public void ToRegex_HandlesWildcardsAndLiterals()
    {
        // WebProxy.BypassList matches patterns against the full URI; the helper wraps the
        // host pattern with scheme/port/path suffixes so plain hostnames still bypass.
        ProxyBypassPattern.ToRegex("*.internal")
            .Should().Be(@"^https?://.*\.internal(:\d+)?(/.*)?$");
        ProxyBypassPattern.ToRegex("localhost")
            .Should().Be(@"^https?://localhost(:\d+)?(/.*)?$");
        ProxyBypassPattern.ToRegex("10.0.0.1")
            .Should().Be(@"^https?://10\.0\.0\.1(:\d+)?(/.*)?$");
    }

    [Fact]
    public void ToRegex_TrimsSurroundingWhitespace()
    {
        // Operators paste lists; a stray space must not produce a pattern that never matches.
        ProxyBypassPattern.ToRegex("  localhost  ")
            .Should().Be(@"^https?://localhost(:\d+)?(/.*)?$");
    }

    [Theory]
    [InlineData("localhost", "http://localhost:11434/v1/models", true)]
    [InlineData("localhost", "https://api.openai.com/v1/models", false)]
    [InlineData("*.internal", "https://llm.internal/v1/chat/completions", true)]
    [InlineData("*.internal", "https://llm.internal.example.com/v1", false)]
    [InlineData("127.0.0.1", "http://127.0.0.1:1234/v1", true)]
    public void ToRegex_ProducedPattern_MatchesTheIntendedUris(string pattern, string uri, bool expected)
    {
        var proxy = new System.Net.WebProxy(
            new System.Uri("http://proxy.corp.local:8080"),
            BypassOnLocal: false,
            BypassList: [ProxyBypassPattern.ToRegex(pattern)]);

        proxy.IsBypassed(new System.Uri(uri)).Should().Be(expected);
    }
}
