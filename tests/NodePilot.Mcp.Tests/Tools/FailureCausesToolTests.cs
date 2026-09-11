using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests.Tools;

/// <summary>
/// The grouping itself is the server's job — the tool only has to pass the window through and hand
/// back the three fields an agent needs to act: how much failed, what the causes were, and how much
/// the returned groups leave out.
/// </summary>
public sealed class FailureCausesToolTests
{
    private static object Payload() => new
    {
        totalFailed = 42,
        groups = new object[]
        {
            new { message = "WinRM connect failed", count = 30, latestExecutionId = Guid.NewGuid(), latestStartedAt = DateTime.UtcNow },
            new { message = (string?)null, count = 5, latestExecutionId = Guid.NewGuid(), latestStartedAt = DateTime.UtcNow },
        },
        remainingCount = 7,
    };

    [Fact]
    public async Task GetFailureCauses_ReturnsGroupsAndTheWithheldCount()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/stats/failure-causes").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Payload()));

        var tools = new TelemetryTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.GetFailureCauses());

        json.Should().Contain("WinRM connect failed").And.Contain("\"totalFailed\":42");
        // Without this an agent reads the returned groups as the whole picture.
        json.Should().Contain("\"remainingCount\":7");
    }

    [Fact]
    public async Task GetFailureCauses_PassesTheWindowThrough()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/stats/failure-causes").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Payload()));

        var tools = new TelemetryTools(api.Client());
        await tools.GetFailureCauses(windowHours: 168);

        var request = api.Server.LogEntries.Should().ContainSingle().Subject;
        request.RequestMessage.RawQuery.Should().Contain("windowHours=168");
    }

    [Fact]
    public async Task GetFailureCauses_QuietWindow_ReportsNothingFailed()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/stats/failure-causes").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { totalFailed = 0, groups = Array.Empty<object>(), remainingCount = 0 }));

        var tools = new TelemetryTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.GetFailureCauses());

        json.Should().Contain("\"totalFailed\":0");
    }
}
