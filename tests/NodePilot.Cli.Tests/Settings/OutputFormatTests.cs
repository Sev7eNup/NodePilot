using System.Text.Json;
using FluentAssertions;
using NodePilot.Cli.Api;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using NodePilot.Cli.Tests.Infra;
using Xunit;

namespace NodePilot.Cli.Tests.Settings;

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

    [Fact]
    public void YamlEmitter_RoundTrip_KeepsMultilineEmptyAndNullLikeValues()
    {
        var value = new
        {
            Script = "$s = Get-Service\r\nif ($s) {\n\t\"ok: #1\" | Write-Output\n}\n  trailing \\ backslash \n",
            Tags = Array.Empty<string>(),
            Config = new Dictionary<string, object?>(),
            NullLike = new[]
            {
                "null", "Null", "NULL", "~", "", "true", "False", "yes", "No", "on", "OFF", "y", "n",
                "123", "-1", "1.5", "1e3", ".inf", ".NaN", "0x1F", "2026-11-04", "<<", "=",
                "- item", "? key", "a: b", "#hash", "@at", " leading", "trailing ", "plain text",
                "bell" + (char)7, "line" + (char)0x2028 + "break",
            },
            Missing = (string?)null,
            Nested = new object[]
            {
                new object[] { "a", new object[] { "b" } },
                new Dictionary<string, object?>(),
                Array.Empty<int>(),
                new { Inner = Array.Empty<string>(), Count = 2, Ratio = -0.5 },
            },
            Keys = new Dictionary<string, object?> { ["null"] = 1, ["true"] = 2, ["123"] = 3, ["a key: x"] = 4, ["plain"] = "v" },
        };

        var yaml = YamlEmitter.Emit(value);

        YamlRoundTrip.ToJson(yaml).Should().Be(
            YamlRoundTrip.Normalize(JsonSerializer.Serialize(value, NodePilotApiClient.JsonOptions)), yaml);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[[]]")]
    [InlineData("[{}]")]
    [InlineData("[[1,[2]],[\"a\",{\"b\":[]}]]")]
    [InlineData("\"null\"")]
    [InlineData("null")]
    public void YamlEmitter_RoundTrip_KeepsRootAndNestedCollections(string json)
    {
        using var doc = JsonDocument.Parse(json);

        var yaml = YamlEmitter.Emit(doc.RootElement);

        YamlRoundTrip.ToJson(yaml).Should().Be(YamlRoundTrip.Normalize(json), yaml);
    }
}
