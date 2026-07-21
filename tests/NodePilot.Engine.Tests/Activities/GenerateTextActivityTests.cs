using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// GenerateTextActivity uses a non-seeded CSPRNG, so exact outputs can't be asserted. Generation
/// tests assert structural properties (length / charset membership / regex / mode behavior) and run
/// the probabilistic ones over many iterations to make a missed-bit failure essentially certain to
/// surface.
/// </summary>
public class GenerateTextActivityTests
{
    private const int Iterations = 200;

    private static JsonElement Cfg(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    private static JsonElement Raw(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static StepExecutionContext Ctx() => new()
    {
        WorkflowExecutionId = Guid.NewGuid(),
        StepId = "gen-1",
        StepLabel = "Generate",
        WorkflowName = "TestFlow",
    };

    private static Task<ActivityResult> Run(JsonElement config) =>
        new GenerateTextActivity().ExecuteAsync(Ctx(), config, CancellationToken.None);

    // ---- ReadLength ----

    [Fact]
    public void ReadLength_Missing_ReturnsDefault16() =>
        GenerateTextActivity.ReadLength(Cfg(new { })).Should().Be(16);

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ReadLength_BelowMin_ClampsTo1(int value) =>
        GenerateTextActivity.ReadLength(Cfg(new { length = value })).Should().Be(1);

    [Fact]
    public void ReadLength_AboveMax_ClampsTo1024() =>
        GenerateTextActivity.ReadLength(Cfg(new { length = 99999 })).Should().Be(1024);

    [Fact]
    public void ReadLength_NonNumberJson_ReturnsDefault() =>
        GenerateTextActivity.ReadLength(Raw("{\"length\":\"16\"}")).Should().Be(16);

    // ---- ReadMode (tolerant; never throws on malformed JSON) ----

    [Fact]
    public void ReadMode_Missing_ReturnsAlphanumeric() =>
        GenerateTextActivity.ReadMode(Cfg(new { })).Should().Be("alphanumeric");

    [Fact]
    public void ReadMode_Unknown_FallsBackToAlphanumeric() =>
        GenerateTextActivity.ReadMode(Cfg(new { mode = "bogus" })).Should().Be("alphanumeric");

    [Fact]
    public void ReadMode_MixedCaseAndWhitespace_Normalized() =>
        GenerateTextActivity.ReadMode(Cfg(new { mode = " GUID " })).Should().Be("guid");

    [Fact]
    public void ReadMode_NonStringJson_DoesNotThrow_FallsBack()
    {
        // mode:5 would throw under JsonElement.GetString(); ReadMode must guard ValueKind.
        var act = () => GenerateTextActivity.ReadMode(Raw("{\"mode\":5}"));
        act.Should().NotThrow();
        GenerateTextActivity.ReadMode(Raw("{\"mode\":5}")).Should().Be("alphanumeric");
    }

    // ---- Deduplicate / RemoveAmbiguous ----

    [Fact]
    public void Deduplicate_RepeatedChars_KeepsFirstOccurrenceOnly() =>
        GenerateTextActivity.Deduplicate("aabXa").Should().Be("abX");

    [Fact]
    public void RemoveAmbiguous_StripsConfusableGlyphs()
    {
        var result = GenerateTextActivity.RemoveAmbiguous("0O1lIaA3");
        result.Should().NotContainAny("0", "O", "1", "l", "I");
        result.Should().Contain("a").And.Contain("A").And.Contain("3");
    }

    // ---- TryBuildCharset ----

    [Fact]
    public void TryBuildCharset_CustomEmpty_ReturnsFalseWithError()
    {
        var ok = GenerateTextActivity.TryBuildCharset("custom", Cfg(new { customCharset = "" }), out var cs, out var err);
        ok.Should().BeFalse();
        cs.Should().BeEmpty();
        err.Should().Contain("customCharset");
    }

    [Fact]
    public void TryBuildCharset_CustomWhitespaceOnly_ReturnsFalse()
    {
        var ok = GenerateTextActivity.TryBuildCharset("custom", Cfg(new { customCharset = "   " }), out _, out var err);
        ok.Should().BeFalse();
        err.Should().Contain("customCharset");
    }

    [Fact]
    public void TryBuildCharset_ExcludeAmbiguous_RemovesAmbiguousFromPreset()
    {
        var ok = GenerateTextActivity.TryBuildCharset("alphanumeric", Cfg(new { excludeAmbiguous = true }), out var cs, out _);
        ok.Should().BeTrue();
        cs.Should().NotContainAny("0", "O", "1", "l", "I");
    }

    // ---- GenerateFromCharset ----

    [Fact]
    public void GenerateFromCharset_ProducesRequestedLength() =>
        GenerateTextActivity.GenerateFromCharset("abc", 25).Should().HaveLength(25);

    // ---- Execute: charset membership per mode ----

    [Fact]
    public async Task ExecuteAsync_AlphanumericDefault_LengthAndCharset()
    {
        var result = await Run(Cfg(new { }));
        result.Success.Should().BeTrue();
        result.Output!.Should().HaveLength(16);
        result.Output.Should().MatchRegex("^[A-Za-z0-9]+$");
    }

    [Fact]
    public async Task ExecuteAsync_Alphabetic_OnlyLetters()
    {
        for (var i = 0; i < Iterations; i++)
            (await Run(Cfg(new { mode = "alphabetic", length = 20 }))).Output.Should().MatchRegex("^[A-Za-z]{20}$");
    }

    [Fact]
    public async Task ExecuteAsync_Numeric_OnlyDigits()
    {
        for (var i = 0; i < Iterations; i++)
            (await Run(Cfg(new { mode = "numeric", length = 12 }))).Output.Should().MatchRegex("^[0-9]{12}$");
    }

    [Fact]
    public async Task ExecuteAsync_Hex_OnlyLowercaseHex()
    {
        for (var i = 0; i < Iterations; i++)
            (await Run(Cfg(new { mode = "hex", length = 32 }))).Output.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public async Task ExecuteAsync_Custom_OnlyCustomChars()
    {
        for (var i = 0; i < Iterations; i++)
            (await Run(Cfg(new { mode = "custom", customCharset = "ABC", length = 10 }))).Output.Should().MatchRegex("^[ABC]{10}$");
    }

    [Fact]
    public async Task ExecuteAsync_Password_OnlyUsesAllowedSymbols()
    {
        // Minimal password: charset is letters+digits+the curated symbol set; no policy guarantee,
        // but every produced char must come from that union (notably none of the excluded metachars).
        var allowed = new Regex("^[A-Za-z0-9!#$%*+\\-=?@^_~]+$");
        for (var i = 0; i < Iterations; i++)
            (await Run(Cfg(new { mode = "password", length = 24 }))).Output.Should().MatchRegex(allowed);
    }

    [Fact]
    public async Task ExecuteAsync_ExcludeAmbiguous_NoConfusableChars()
    {
        for (var i = 0; i < Iterations; i++)
            (await Run(Cfg(new { mode = "alphanumeric", excludeAmbiguous = true, length = 40 })))
                .Output!.Should().NotContainAny("0", "O", "o", "1", "l", "I", "i");
    }

    // ---- Execute: guid ----

    [Fact]
    public async Task ExecuteAsync_Guid_MatchesGuidRegex()
    {
        var result = await Run(Cfg(new { mode = "guid" }));
        result.Success.Should().BeTrue();
        result.Output.Should().MatchRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
    }

    [Fact]
    public async Task ExecuteAsync_Guid_IgnoresLengthAndCharsetConfig()
    {
        var result = await Run(Cfg(new { mode = "guid", length = 999, customCharset = "X" }));
        result.Output.Should().HaveLength(36); // "D" format, length config ignored
    }

    [Fact]
    public async Task ExecuteAsync_Guid_TwoCalls_ProduceDifferentValues()
    {
        var a = await Run(Cfg(new { mode = "guid" }));
        var b = await Run(Cfg(new { mode = "guid" }));
        a.Output.Should().NotBe(b.Output);
    }

    // ---- Execute: result shape & failures ----

    [Fact]
    public async Task ExecuteAsync_Success_OutputEqualsTextParam()
    {
        var result = await Run(Cfg(new { length = 16 }));
        result.OutputParameters["text"].Should().Be(result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_Success_LengthParamMatchesActualLength()
    {
        var result = await Run(Cfg(new { length = 21 }));
        result.OutputParameters["length"].Should().Be("21");
    }

    [Fact]
    public async Task ExecuteAsync_CustomEmpty_ReturnsFailureNotThrow()
    {
        var result = await Run(Cfg(new { mode = "custom", customCharset = "" }));
        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("customCharset");
        result.OutputParameters.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_MalformedModeJson_DoesNotThrow_FallsBackToAlphanumeric()
    {
        var result = await Run(Raw("{\"mode\":5,\"length\":8}"));
        result.Success.Should().BeTrue();
        result.Output.Should().MatchRegex("^[A-Za-z0-9]{8}$");
    }
}
