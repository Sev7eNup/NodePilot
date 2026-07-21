using FluentAssertions;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Xunit;

namespace NodePilot.Cli.Tests;

public class OutputFormatTests
{
    [Theory]
    [InlineData("json", OutputFormat.Json)]
    [InlineData("JSON", OutputFormat.Json)]
    [InlineData("yaml", OutputFormat.Yaml)]
    [InlineData("yml", OutputFormat.Yaml)]
    [InlineData("table", OutputFormat.Table)]
    public void Resolve_ParsesExplicitFormat(string raw, OutputFormat expected)
    {
        OutputFormatParser.Resolve(raw).Should().Be(expected);
    }

    [Fact]
    public void Resolve_UnknownFormat_Throws()
    {
        Action act = () => OutputFormatParser.Resolve("xml");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void YamlEmitter_EmitsScalarsObjectsAndArrays()
    {
        var value = new
        {
            Name = "Alpha",
            Count = 3,
            Active = true,
            Tags = new[] { "ops", "demo" },
            Detail = new { Owner = "ops-example", When = "2026-04-26" },
        };
        var yaml = YamlEmitter.Emit(value);
        yaml.Should().Contain("name: Alpha");
        yaml.Should().Contain("count: 3");
        yaml.Should().Contain("active: true");
        yaml.Should().Contain("tags:");
        yaml.Should().Contain("- ops");
        yaml.Should().Contain("- demo");
        yaml.Should().Contain("detail:");
        yaml.Should().Contain("owner: ops-example");
    }
}
