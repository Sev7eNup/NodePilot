using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public class JsonQueryActivityTests
{
    private readonly JsonQueryActivity _activity = new();

    private static JsonElement Cfg(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    private static StepExecutionContext Ctx() =>
        new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "json-1" };

    private const string ItemsJson =
        "{\"items\":[{\"name\":\"foo\",\"price\":9.99},{\"name\":\"bar\",\"price\":19.50}]}";

    [Fact]
    public async Task ExecuteAsync_SingleModeScalar_ReturnsScalarValue()
    {
        var cfg = Cfg(new { source = "inline", content = ItemsJson, jsonPath = "$.items[0].name", resultMode = "single" });
        var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("foo");
        result.OutputParameters["count"].Should().Be("1");
    }

    [Fact]
    public async Task ExecuteAsync_SingleModeObject_ReturnsJsonString()
    {
        var cfg = Cfg(new { source = "inline", content = ItemsJson, jsonPath = "$.items[0]", resultMode = "single" });
        var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("\"foo\"").And.Contain("9.99");
    }

    [Fact]
    public async Task ExecuteAsync_AllMode_ReturnsJsonArray()
    {
        var cfg = Cfg(new { source = "inline", content = ItemsJson, jsonPath = "$.items[*].name", resultMode = "all" });
        var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("foo").And.Contain("bar");
        result.OutputParameters["count"].Should().Be("2");
    }

    [Fact]
    public async Task ExecuteAsync_FilterExpression_MatchesBasedOnPredicate()
    {
        var cfg = Cfg(new { source = "inline", content = ItemsJson, jsonPath = "$.items[?(@.price > 10)].name", resultMode = "all" });
        var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("bar");
        result.Output.Should().NotContain("foo");
        result.OutputParameters["count"].Should().Be("1");
    }

    [Fact]
    public async Task ExecuteAsync_MultiMatchInSingleMode_ReturnsFailureWithResultModeHint()
    {
        var cfg = Cfg(new { source = "inline", content = ItemsJson, jsonPath = "$.items[*].name" });
        var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("$.items[*].name");
        result.ErrorOutput.Should().Contain("'all'");
    }

    [Fact]
    public async Task ExecuteAsync_NoMatchSingleMode_SucceedsWithEmptyOutput()
    {
        var cfg = Cfg(new { source = "inline", content = ItemsJson, jsonPath = "$.nothing" });
        var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("");
        result.OutputParameters["count"].Should().Be("0");
    }

    [Fact]
    public async Task ExecuteAsync_InvalidJson_ReturnsFailure()
    {
        var cfg = Cfg(new { source = "inline", content = "{not json}", jsonPath = "$.x" });
        var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        // The JSON parser was switched to Newtonsoft's JsonTextReader with an explicit
        // depth guard, which changed the error prefix from "JsonQuery error: ..." to
        // "JsonQuery: parse failed:". This test just checks that failures still surface
        // with that parse-error message.
        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("parse failed");
    }

    [Fact]
    public async Task ExecuteAsync_MissingJsonPath_ReturnsFailure()
    {
        var cfg = Cfg(new { source = "inline", content = ItemsJson });
        var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("jsonPath");
    }

    [Fact]
    public async Task ExecuteAsync_FileSource_ReadsFileAndQueries()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, ItemsJson);
            var cfg = Cfg(new { source = "file", path = tempFile, jsonPath = "$.items[*].name", resultMode = "all" });
            var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

            result.Success.Should().BeTrue();
            result.OutputParameters["count"].Should().Be("2");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FileSource_NonexistentPath_ReturnsFailure()
    {
        var cfg = Cfg(new { source = "file", path = "C:\\definitely\\not-here.json", jsonPath = "$.x" });
        var result = await _activity.ExecuteAsync(Ctx(), cfg, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("not found");
    }
}
