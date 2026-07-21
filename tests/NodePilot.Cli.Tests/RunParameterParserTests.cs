using FluentAssertions;
using NodePilot.Cli.Commands.Workflow;
using Xunit;

namespace NodePilot.Cli.Tests;

public class RunParameterParserTests
{
    [Fact]
    public void EmptyInput_ReturnsNull()
    {
        RunParameterParser.Parse(Array.Empty<string>()).Should().BeNull();
    }

    [Fact]
    public void SinglePair_Parses()
    {
        var dict = RunParameterParser.Parse(new[] { "env=prod" });
        dict.Should().NotBeNull();
        dict!["env"].Should().Be("prod");
    }

    [Fact]
    public void MultiplePairs_Parse()
    {
        var dict = RunParameterParser.Parse(new[] { "env=prod", "region=eu", "verbose=true" });
        dict.Should().HaveCount(3);
        dict!["region"].Should().Be("eu");
    }

    [Fact]
    public void ValueWithEqualsSign_KeepsTail()
    {
        var dict = RunParameterParser.Parse(new[] { "filter=name=foo" });
        dict!["filter"].Should().Be("name=foo");
    }

    [Fact]
    public void EmptyValue_IsAllowed()
    {
        var dict = RunParameterParser.Parse(new[] { "name=" });
        dict!["name"].Should().Be("");
    }

    [Fact]
    public void DuplicateKey_LastWins()
    {
        var dict = RunParameterParser.Parse(new[] { "env=stg", "env=prod" });
        dict!["env"].Should().Be("prod");
    }

    [Theory]
    [InlineData("noequals")]
    [InlineData("=valueonly")]
    [InlineData("")]
    public void Invalid_Throws(string raw)
    {
        Action act = () => RunParameterParser.Parse(new[] { raw });
        act.Should().Throw<ArgumentException>();
    }
}
