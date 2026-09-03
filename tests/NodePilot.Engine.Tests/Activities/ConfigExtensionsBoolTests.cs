using System.Text.Json;
using FluentAssertions;
using NodePilot.Engine.Activities;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// The variable resolver substitutes {{...}} textually inside the raw config JSON, and a template
/// can only sit inside a JSON string — so a templated boolean always arrives quoted. Deciding by
/// ValueKind alone made a resolved "false" read as TRUE for every knob whose default is true.
/// </summary>
public class ConfigExtensionsBoolTests
{
    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("True", true)]
    [InlineData("FALSE", false)]
    public void GetBool_QuotedBoolean_IsHonouredInBothDefaultDirections(string value, bool expected)
    {
        var config = Cfg($"{{\"flag\":\"{value}\"}}");

        config.GetBool("flag", defaultValue: false).Should().Be(expected);
        config.GetBool("flag", defaultValue: true).Should().Be(expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetBool_MissingKey_UsesTheDefault(bool defaultValue)
        => Cfg("{}").GetBool("flag", defaultValue).Should().Be(defaultValue);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetBool_UnparsableString_UsesTheDefault(bool defaultValue)
        => Cfg("{\"flag\":\"maybe\"}").GetBool("flag", defaultValue).Should().Be(defaultValue);

    [Fact]
    public void GetBool_RealJsonBooleans_AreUnchanged()
    {
        Cfg("{\"flag\":true}").GetBool("flag", defaultValue: false).Should().BeTrue();
        Cfg("{\"flag\":false}").GetBool("flag", defaultValue: true).Should().BeFalse();
    }
}